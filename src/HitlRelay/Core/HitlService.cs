using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HitlRelay.Core;

/// <summary>Creates requests, calls the adapter, suspends the run, and applies timeout rules.</summary>
public sealed class HitlService
{
    private readonly HitlDbContext _db;
    private readonly AdapterRegistry _adapters;
    private readonly HitlCompletion _completion;
    private readonly AuditService _audit;
    private readonly TimeProvider _clock;
    private readonly HitlOptions _options;
    private readonly ILogger<HitlService> _log;

    public HitlService(HitlDbContext db, AdapterRegistry adapters, HitlCompletion completion, AuditService audit,
        TimeProvider clock, IOptions<HitlOptions> options, ILogger<HitlService> log)
    {
        _db = db;
        _adapters = adapters;
        _completion = completion;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// What the HITL workflow node calls. Returns once the request is sent and the run is saved as Waiting;
    /// nothing blocks while waiting. Throws HitlSendException if sending fails after 3 attempts (the host then
    /// takes the node's error branch; the run is already marked Failed).
    /// </summary>
    public Task<HitlRequest> CreateAsync(HitlNodeConfig cfg, string workflowRunId, string nodeId,
        IReadOnlyDictionary<string, object?> variables, CancellationToken ct = default)
    {
        var prompt = TemplateRenderer.Render(cfg.MessageTemplate, variables);
        return CreateCoreAsync(cfg, workflowRunId, nodeId, prompt, variables, null, ct);
    }

    private async Task<HitlRequest> CreateCoreAsync(HitlNodeConfig cfg, string runId, string nodeId, string prompt,
        IReadOnlyDictionary<string, object?>? variables, Guid? escalatedFrom, CancellationToken ct)
    {
        Validate(cfg);

        // Render the Teams channel key (e.g. "{{order.id}}") once, on a copy so the caller's config is untouched; the
        // rendered value is what gets stored in ConfigJson and read back by the adapter.
        if (!string.IsNullOrWhiteSpace(cfg.TeamsChannelKey))
        {
            cfg = HitlJson.Deserialize<HitlNodeConfig>(HitlJson.Serialize(cfg))!;
            cfg.TeamsChannelKey = TemplateRenderer.Render(cfg.TeamsChannelKey!, variables).Trim();
            if (cfg.TeamsChannelKey.Length == 0)
                throw new ArgumentException("TeamsChannelKey rendered to an empty value (is the workflow variable set?).");
            if (string.IsNullOrWhiteSpace(cfg.Recipient)) cfg.Recipient = "channel:" + cfg.TeamsChannelKey;
        }
        var adapter = _adapters.Get(cfg.Channel);
        var now = Now;

        // WhatsApp has no threads: replies are matched to the sender's single open request, so we enforce
        // one open request per phone number (across all workflows - the stricter reading of section 5.4).
        if (cfg.Channel.Equals(ChannelKeys.WhatsApp, StringComparison.OrdinalIgnoreCase))
        {
            var phone = new string(cfg.Recipient.Where(char.IsDigit).ToArray());
            var open = await _db.Requests.AnyAsync(r => r.Channel == ChannelKeys.WhatsApp
                && r.Status == HitlRequestStatus.Pending && r.Recipient == cfg.Recipient, ct);
            if (open)
                throw new InvalidOperationException($"A WhatsApp request is already open for this phone number ({Mask.Value(phone)}).");
        }

        // Persist the run context BEFORE sending, so the run survives an application restart.
        var run = await _db.Runs.FindAsync(new object[] { runId }, ct);
        if (run == null)
        {
            run = new WorkflowRun { Id = runId, CreatedAt = now };
            _db.Runs.Add(run);
        }
        run.Status = WorkflowRunStatus.Waiting;
        if (variables != null) run.ContextJson = HitlJson.Serialize(variables);
        run.UpdatedAt = now;

        var req = new HitlRequest
        {
            WorkflowRunId = runId,
            NodeId = nodeId,
            Channel = cfg.Channel.ToLowerInvariant(),
            Recipient = cfg.Recipient,
            Prompt = prompt,
            Mode = cfg.ResponseMode,
            KeywordsJson = HitlJson.Serialize(cfg.AllowedKeywords),
            CorrelationToken = Crypto.RandomToken(32),
            ShortCode = Crypto.ShortCode(),
            CreatedAt = now,
            ExpiresAt = now + cfg.Timeout,
            NextReminderAt = cfg.ReminderInterval is { } ri && ri < cfg.Timeout ? now + ri : null,
            ConfigJson = HitlJson.Serialize(cfg),
            EscalatedFromRequestId = escalatedFrom,
        };
        _db.Requests.Add(req);
        _audit.Add(req.Id, "created", new { channel = req.Channel, mode = req.Mode, escalatedFrom });
        await _db.SaveChangesAsync(ct);

        using var scope = _log.BeginScope(new Dictionary<string, object> { ["RequestId"] = req.Id, ["WorkflowRunId"] = runId });

        SendResult? sent = null;
        Exception? failure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                sent = await adapter.SendAsync(req, ct);
                break;
            }
            catch (HitlRecipientUnreachableException ex)
            {
                failure = ex; // fail fast, no retry
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
                _log.LogWarning(ex, "Send attempt {Attempt} failed", attempt);
                _audit.Add(req.Id, "send_retry", new { attempt, error = ex.Message });
                await _db.SaveChangesAsync(ct);
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.SendRetryBaseDelayMs * Math.Pow(2, attempt - 1)), ct);
            }
        }

        if (sent == null)
        {
            req.Status = HitlRequestStatus.Cancelled; // assumption: a never-delivered request is recorded as Cancelled
            run.Status = WorkflowRunStatus.Failed;
            run.UpdatedAt = Now;
            _audit.Add(req.Id, "send_failed", new { error = failure?.Message });
            await _db.SaveChangesAsync(ct);
            throw new HitlSendException($"Could not send HITL request on {cfg.Channel}: {failure?.Message}", failure);
        }

        req.ExternalMessageId = sent.ExternalMessageId;
        req.ExternalThreadId = sent.ExternalThreadId;
        req.ExternalConversationId = sent.ExternalConversationId;
        _audit.Add(req.Id, "sent", new { channel = req.Channel, recipient = Mask.Value(req.Recipient) });
        await _db.SaveChangesAsync(ct);
        HitlTelemetry.Sent.Add(1, new KeyValuePair<string, object?>("channel", req.Channel));
        return req;
    }

    private static void Validate(HitlNodeConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Channel)) throw new ArgumentException("Channel is required.");
        if (string.IsNullOrWhiteSpace(cfg.Recipient) && string.IsNullOrWhiteSpace(cfg.TeamsChannelKey))
            throw new ArgumentException("Recipient is required.");
        if (!string.IsNullOrWhiteSpace(cfg.TeamsChannelKey) && !cfg.Channel.Equals(ChannelKeys.Teams, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TeamsChannelKey is only valid for the Teams channel.");
        if (cfg.ResponseMode == HitlResponseMode.Keyword && cfg.AllowedKeywords.Count == 0)
            throw new ArgumentException("Keyword mode needs at least one allowed keyword.");
        if (cfg.OnTimeout == HitlTimeoutAction.ContinueWithDefault && string.IsNullOrEmpty(cfg.DefaultValue))
            throw new ArgumentException("ContinueWithDefault needs a default value.");
    }

    public async Task<bool> CancelAsync(Guid requestId, CancellationToken ct = default)
    {
        var req = await _db.Requests.FindAsync(new object[] { requestId }, ct);
        if (req == null) return false;
        return await _completion.FailAsync(requestId, HitlRequestStatus.Cancelled, "Request cancelled",
            new HitlOutcome(HitlOutcomeStatus.Cancelled, null, null, null, Now), ct);
    }

    /// <summary>Scheduler hook: sends one reminder and schedules the next (or none, if it would pass expiry).</summary>
    public async Task SendReminderAsync(Guid requestId, CancellationToken ct = default)
    {
        var req = await _db.Requests.FindAsync(new object[] { requestId }, ct);
        if (req == null || req.Status != HitlRequestStatus.Pending) return;

        var cfg = req.Config;
        var left = req.ExpiresAt - Now;
        await _adapters.Get(req.Channel).SendTextAsync(req,
            $"⏰ Reminder: this request is still waiting for your answer (about {Math.Max(1, (int)left.TotalMinutes)} min left).", ct);

        var next = cfg.ReminderInterval is { } ri ? Now + ri : (DateTime?)null;
        req.NextReminderAt = next is { } n && n < req.ExpiresAt ? n : null;
        _audit.Add(req.Id, "reminder");
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Applies the configured On timeout rule. Called by the scheduler at expiry and by the reply handler
    /// when the retry limit is exhausted.
    /// </summary>
    public async Task ApplyTimeoutRuleAsync(Guid requestId, string reason, CancellationToken ct = default)
    {
        var req = await _db.Requests.FindAsync(new object[] { requestId }, ct);
        if (req == null) return;
        await _db.Entry(req).ReloadAsync(ct);
        if (req.Status != HitlRequestStatus.Pending) return;

        var cfg = req.Config;
        var adapter = _adapters.Get(req.Channel);
        var outcome = new HitlOutcome(HitlOutcomeStatus.TimedOut, null, reason, null, Now);

        switch (cfg.OnTimeout)
        {
            case HitlTimeoutAction.Reminder when req.ReminderCount < cfg.MaxTimeoutReminders:
                req.ReminderCount++;
                req.ExpiresAt = Now + cfg.Timeout;
                req.RetryCount = 0;
                req.NextReminderAt = cfg.ReminderInterval is { } ri && ri < cfg.Timeout ? Now + ri : null;
                _audit.Add(req.Id, "timeout_reminder", new { reason, count = req.ReminderCount });
                await _db.SaveChangesAsync(ct);
                await adapter.SendTextAsync(req, "⏰ Reminder: we are still waiting for your answer to the request above.", ct);
                return;

            case HitlTimeoutAction.ContinueWithDefault:
            {
                var def = cfg.DefaultValue ?? "";
                var kw = req.Keywords.FirstOrDefault(k => string.Equals(k.Key, def, StringComparison.OrdinalIgnoreCase));
                var outputs = BuildOutputs(def, kw?.Label, def, "system:timeout", req.Channel, Now);
                outputs["response.timedOut"] = true;
                await _completion.TimeoutContinueAsync(req.Id, outputs, outcome with { Value = def, Text = $"default '{def}' applied" }, ct);
                return;
            }

            case HitlTimeoutAction.Escalate when !string.IsNullOrEmpty(cfg.FallbackChannel) && !string.IsNullOrEmpty(cfg.FallbackRecipient):
            {
                var escalated = new HitlNodeConfig
                {
                    Channel = cfg.FallbackChannel!,
                    Recipient = cfg.FallbackRecipient!,
                    MessageTemplate = cfg.MessageTemplate,
                    ResponseMode = cfg.ResponseMode,
                    AllowedKeywords = cfg.AllowedKeywords,
                    Timeout = cfg.Timeout,
                    OnTimeout = HitlTimeoutAction.Fail, // escalate once, then fail
                    AllowedResponders = new List<string>(), // fallback person is not in the original allow-list
                    MaxRetries = cfg.MaxRetries,
                    ReminderInterval = cfg.ReminderInterval,
                    MinLength = cfg.MinLength,
                    ValidationRegex = cfg.ValidationRegex,
                    ValidationMessage = cfg.ValidationMessage,
                    WhatsAppTemplate = cfg.WhatsAppTemplate,
                };
                try
                {
                    await CreateCoreAsync(escalated, req.WorkflowRunId, req.NodeId, "[Escalated] " + req.Prompt, null, req.Id, ct);
                }
                catch (HitlSendException ex)
                {
                    await _completion.FailAsync(req.Id, HitlRequestStatus.TimedOut, "Escalation failed: " + ex.Message, outcome, ct);
                    return;
                }
                await _completion.CloseAsync(req.Id, HitlRequestStatus.TimedOut, outcome with { Text = "escalated" }, ct);
                return;
            }

            default: // Fail, or Reminder exhausted, or Escalate without a fallback
                await _completion.FailAsync(req.Id, HitlRequestStatus.TimedOut, $"HITL request timed out ({reason})", outcome, ct);
                return;
        }
    }

    public static Dictionary<string, object?> BuildOutputs(string? value, string? label, string? text,
        string responder, string channel, DateTime receivedAt) => new()
    {
        ["response.value"] = value,
        ["response.label"] = label,
        ["response.text"] = text,
        ["response.responder"] = responder,
        ["response.channel"] = channel,
        ["response.receivedAt"] = receivedAt.ToString("O"),
    };
}
