using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HitlRelay.Core;

public enum ReplyStatus { Resumed, Duplicate, Unmatched, AlreadyAnswered, NotAllowed, Invalid, TimedOut }

public sealed record ReplyResult(ReplyStatus Status, string Message);

/// <summary>Correlates an InboundReply to a request (section 6.3), validates it, then resumes the run.</summary>
public sealed class HitlReplyHandler
{
    public const string AlreadyAnsweredText = "This request was already answered. Thank you!";
    public const string NotAllowedText = "Sorry, you are not able to respond to this request.";

    private readonly HitlDbContext _db;
    private readonly AdapterRegistry _adapters;
    private readonly HitlCompletion _completion;
    private readonly HitlService _service;
    private readonly AuditService _audit;
    private readonly TimeProvider _clock;
    private readonly HitlOptions _options;
    private readonly ILogger<HitlReplyHandler> _log;

    public HitlReplyHandler(HitlDbContext db, AdapterRegistry adapters, HitlCompletion completion, HitlService service,
        AuditService audit, TimeProvider clock, IOptions<HitlOptions> options, ILogger<HitlReplyHandler> log)
    {
        _db = db;
        _adapters = adapters;
        _completion = completion;
        _service = service;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<ReplyResult> HandleAsync(InboundReply reply, CancellationToken ct = default)
    {
        var adapter = _adapters.Get(reply.Channel);

        // Idempotency: platforms retry webhooks; a repeated delivery must not do anything twice.
        if (!await TryMarkProcessedAsync(reply, ct))
        {
            _log.LogInformation("Duplicate {Channel} delivery ignored", reply.Channel);
            return new ReplyResult(ReplyStatus.Duplicate, "Already received.");
        }

        try { await adapter.AcknowledgeInboundAsync(reply, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Inbound acknowledge failed (non-fatal)"); }

        var req = await CorrelateAsync(reply, ct);
        if (req == null)
        {
            // Never resume a run on an unmatched message.
            await _audit.WriteAsync(null, "unmatched_reply", new { reply.Channel, responder = Mask.Value(reply.ResponderId) }, ct);
            return new ReplyResult(ReplyStatus.Unmatched, "No open request found.");
        }

        using var scope = _log.BeginScope(new Dictionary<string, object> { ["RequestId"] = req.Id, ["WorkflowRunId"] = req.WorkflowRunId });
        var cfg = req.Config;

        if (req.Status != HitlRequestStatus.Pending)
            return await AlreadyAnsweredAsync(adapter, req, ct);

        if (!IsAllowed(cfg, reply))
        {
            await _audit.WriteAsync(req.Id, "responder_rejected", new { responder = Mask.Value(reply.ResponderId) }, ct);
            await TrySendAsync(adapter, req, NotAllowedText, ct);
            return new ReplyResult(ReplyStatus.NotAllowed, NotAllowedText);
        }

        await _audit.WriteAsync(req.Id, "reply_received", new { responder = Mask.Value(reply.ResponderId), hasAction = reply.ActionValue != null || reply.ActionIndex != null }, ct);

        // ---- validate against the response mode ----
        string? value, label, text, error;
        if (req.Mode == HitlResponseMode.Keyword)
        {
            var kws = req.Keywords;
            KeywordDef? kw;
            if (reply.ActionIndex is int i && i >= 0 && i < kws.Count) kw = kws[i];
            else if (!string.IsNullOrEmpty(reply.ActionValue))
                kw = kws.FirstOrDefault(k => string.Equals(k.Key, reply.ActionValue, StringComparison.OrdinalIgnoreCase));
            else kw = KeywordMatcher.Match(kws, reply.Text);

            value = kw?.Key; label = kw?.Label; text = reply.Text ?? kw?.Label;
            error = kw == null ? $"Sorry, I did not understand that. Please reply with one of: {KeywordMatcher.Options(kws)}" : null;
        }
        else
        {
            text = (reply.Text ?? reply.ActionValue)?.Trim();
            value = text; label = null;
            error = ReplyValidator.ValidateOpenEnded(cfg, text);
        }

        if (error != null)
            return await InvalidAsync(adapter, req, cfg, error, ct);

        // ---- valid: close request, claim run, resume ----
        var now = Now;
        var display = reply.ResponderDisplayName ?? reply.ResponderId;
        var response = new HitlResponse
        {
            RequestId = req.Id,
            ResponderId = reply.ResponderId,
            ResponderDisplayName = reply.ResponderDisplayName,
            Value = value,
            Text = text,
            RawPayloadJson = _options.StoreRawPayload ? reply.RawPayload : null,
            ReceivedAt = now,
        };
        var outputs = HitlService.BuildOutputs(value, label, text, display, req.Channel, reply.ReceivedAt);
        var outcome = new HitlOutcome(HitlOutcomeStatus.Answered, value, label ?? text, display, now);

        var won = await _completion.AnswerAsync(req.Id, response, outputs, outcome, ct);
        if (!won)
        {
            _db.ChangeTracker.Clear();
            var fresh = await _db.Requests.FindAsync(new object[] { req.Id }, ct);
            return await AlreadyAnsweredAsync(adapter, fresh ?? req, ct);
        }
        return new ReplyResult(ReplyStatus.Resumed, "Thank you, your answer was recorded.");
    }

    private async Task<ReplyResult> InvalidAsync(IHitlChannelAdapter adapter, HitlRequest req, HitlNodeConfig cfg, string error, CancellationToken ct)
    {
        req.RetryCount++;
        await _audit.WriteAsync(req.Id, "invalid_reply", new { retry = req.RetryCount, max = cfg.MaxRetries }, ct);

        if (req.RetryCount > cfg.MaxRetries)
        {
            await _audit.WriteAsync(req.Id, "retries_exhausted", null, ct);
            await _service.ApplyTimeoutRuleAsync(req.Id, "retry limit reached", ct);
            return new ReplyResult(ReplyStatus.TimedOut, "Too many invalid replies.");
        }

        await TrySendAsync(adapter, req, error, ct);
        return new ReplyResult(ReplyStatus.Invalid, error);
    }

    private async Task<ReplyResult> AlreadyAnsweredAsync(IHitlChannelAdapter adapter, HitlRequest req, CancellationToken ct)
    {
        await _audit.WriteAsync(req.Id, "late_reply", null, ct);
        await TrySendAsync(adapter, req, AlreadyAnsweredText, ct);
        return new ReplyResult(ReplyStatus.AlreadyAnswered, AlreadyAnsweredText);
    }

    private async Task TrySendAsync(IHitlChannelAdapter adapter, HitlRequest req, string text, CancellationToken ct)
    {
        try { await adapter.SendTextAsync(req, text, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Follow-up message failed (non-fatal)"); }
    }

    private static bool IsAllowed(HitlNodeConfig cfg, InboundReply r)
    {
        if (cfg.AllowedResponders.Count == 0) return true;
        bool Match(string? candidate) => !string.IsNullOrEmpty(candidate) &&
            cfg.AllowedResponders.Any(a => string.Equals(a.Trim().TrimStart('@'), candidate.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase));
        return Match(r.ResponderId) || Match(r.ResponderEmail) || Match(r.ResponderAlias);
    }

    /// <summary>Section 6.3: first rule that applies wins.</summary>
    private async Task<HitlRequest?> CorrelateAsync(InboundReply reply, CancellationToken ct)
    {
        var channel = reply.Channel.ToLowerInvariant();

        // 1. Correlation token (button payload, link, plus-address) or the short lookup code.
        if (!string.IsNullOrEmpty(reply.CorrelationToken))
            return await _db.Requests.FirstOrDefaultAsync(r => r.Channel == channel && r.CorrelationToken == reply.CorrelationToken, ct);
        if (!string.IsNullOrEmpty(reply.CorrelationShortCode))
            return await _db.Requests.FirstOrDefaultAsync(r => r.Channel == channel && r.ShortCode == reply.CorrelationShortCode, ct);

        // 2. Thread or reply reference.
        if (!string.IsNullOrEmpty(reply.ExternalThreadId))
        {
            var byThread = await _db.Requests
                .Where(r => r.Channel == channel && (r.ExternalThreadId == reply.ExternalThreadId || r.ExternalMessageId == reply.ExternalThreadId))
                .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            if (byThread != null) return byThread;
        }

        // 3. Conversation plus responder: the latest Pending request for that conversation/recipient.
        var conversation = reply.ExternalConversationId;
        var responder = reply.ResponderId;
        if (string.IsNullOrEmpty(conversation) && string.IsNullOrEmpty(responder)) return null;
        return await _db.Requests
            .Where(r => r.Channel == channel && r.Status == HitlRequestStatus.Pending &&
                        ((conversation != null && r.ExternalConversationId == conversation) ||
                         (responder != "" && r.Recipient == responder)))
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> TryMarkProcessedAsync(InboundReply reply, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(reply.ExternalMessageId)) return true; // channel gave no id; rely on the status check
        var channel = reply.Channel.ToLowerInvariant();
        var key = reply.ExternalMessageId;

        if (await _db.ProcessedInbounds.AnyAsync(p => p.Channel == channel && p.DedupeKey == key, ct)) return false;
        var row = new ProcessedInbound { Channel = channel, DedupeKey = key, At = Now };
        _db.ProcessedInbounds.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            if (await _db.ProcessedInbounds.AnyAsync(p => p.Channel == channel && p.DedupeKey == key, ct)) return false;
            throw;
        }
    }
}
