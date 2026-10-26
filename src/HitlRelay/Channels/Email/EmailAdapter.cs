using System.Net;
using System.Text.Json;
using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.Extensions.Options;

namespace HitlRelay.Channels.Email;

/// <summary>
/// E-mail channel. Keyword mode: one button-styled link per keyword; each link opens a confirmation page and
/// only the POST on that page records the answer (mail scanners prefetch links with GET).
/// Open-ended mode: a link to a hosted response page (recommended), plus reply-by-email when
/// Channels:Email:InboundAddressPattern is configured (correlated by In-Reply-To or plus-address).
/// </summary>
public sealed class EmailAdapter : IHitlChannelAdapter
{
    private readonly IEmailSender _sender;
    private readonly EmailTokenService _tokens;
    private readonly EmailOptions _opt;
    private readonly HitlOptions _hitl;

    public EmailAdapter(IEmailSender sender, EmailTokenService tokens, IOptions<EmailOptions> opt, IOptions<HitlOptions> hitl)
    {
        _sender = sender;
        _tokens = tokens;
        _opt = opt.Value;
        _hitl = hitl.Value;
    }

    public string ChannelKey => ChannelKeys.Email;

    public async Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        var baseUrl = _hitl.BaseUrl.TrimEnd('/');
        string Link(string? key) => $"{baseUrl}/hitl/email/respond?token={_tokens.Create(request.Id, key, request.ExpiresAt)}";
        var prompt = WebUtility.HtmlEncode(request.Prompt).Replace("\n", "<br/>");

        string html, text;
        if (request.Mode == HitlResponseMode.Keyword)
        {
            var buttons = string.Concat(request.Keywords.Select(k =>
                $"<a href=\"{Link(k.Key)}\" style=\"display:inline-block;margin:4px 6px 4px 0;padding:10px 18px;background:#1a56db;color:#fff;text-decoration:none;border-radius:6px;font-family:Arial,sans-serif\">{WebUtility.HtmlEncode(k.Label)}</a>"));
            html = $"<div style=\"font-family:Arial,sans-serif\"><p>{prompt}</p><p>{buttons}</p><p style=\"color:#666;font-size:12px\">You will be asked to confirm your choice on the next page.</p></div>";
            text = request.Prompt + "\n\n" + string.Join("\n", request.Keywords.Select(k => $"{k.Label}: {Link(k.Key)}"));
        }
        else
        {
            var replyHint = string.IsNullOrEmpty(_opt.InboundAddressPattern) ? "" : "<p style=\"color:#666;font-size:12px\">You can also simply reply to this e-mail.</p>";
            html = $"<div style=\"font-family:Arial,sans-serif\"><p>{prompt}</p><p><a href=\"{Link(null)}\" style=\"display:inline-block;padding:10px 18px;background:#1a56db;color:#fff;text-decoration:none;border-radius:6px\">Write your reply</a></p>{replyHint}</div>";
            text = request.Prompt + "\n\nReply here: " + Link(null);
        }

        var replyTo = string.IsNullOrEmpty(_opt.InboundAddressPattern)
            ? null
            : _opt.InboundAddressPattern.Replace("{token}", request.CorrelationToken);

        var subject = "Action required: " + (request.Prompt.Length > 60 ? request.Prompt[..60] + "…" : request.Prompt).Replace('\n', ' ');
        var messageId = await _sender.SendAsync(new EmailMessage(request.Recipient, subject, html, text, replyTo), ct);
        return new SendResult(messageId, null, request.Recipient.ToLowerInvariant());
    }

    /// <summary>Inbound-mail webhook: a shared secret header (the provider cannot sign for us).</summary>
    public bool VerifyInbound(HttpRequest httpRequest, string rawBody)
    {
        if (string.IsNullOrEmpty(_opt.InboundSecret)) return false;
        return Crypto.FixedEquals(httpRequest.Headers["X-Hitl-Inbound-Secret"].ToString(), _opt.InboundSecret);
    }

    /// <summary>
    /// Expects a simple provider-neutral JSON body:
    /// { "messageId", "from", "fromName", "to": ["..."], "inReplyTo", "references", "text" }.
    /// </summary>
    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var from = Str("from")?.Trim().ToLowerInvariant();
        var text = EmailReplyCleaner.Clean(Str("text"));
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(text)) return null;

        // Plus-address: hitl+{token}@domain
        string? token = null;
        if (root.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Array)
        {
            foreach (var addr in to.EnumerateArray())
            {
                var a = addr.GetString() ?? "";
                var plus = a.IndexOf('+');
                var at = a.IndexOf('@');
                if (plus >= 0 && at > plus) { token = a[(plus + 1)..at]; break; }
            }
        }

        var inReplyTo = Str("inReplyTo") ?? Str("references")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return new InboundReply
        {
            Channel = ChannelKey,
            CorrelationToken = token,
            ExternalMessageId = Str("messageId")?.Trim('<', '>'),
            ExternalThreadId = inReplyTo?.Trim('<', '>'),
            ExternalConversationId = from,
            ResponderId = from,
            ResponderEmail = from,
            ResponderDisplayName = Str("fromName") ?? from,
            Text = text,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    // The confirmation page already tells the person the outcome; no follow-up e-mail is sent (assumption).
    public Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct) => Task.CompletedTask;

    public async Task SendTextAsync(HitlRequest request, string text, CancellationToken ct)
    {
        var html = $"<div style=\"font-family:Arial,sans-serif\"><p>{WebUtility.HtmlEncode(text)}</p></div>";
        await _sender.SendAsync(new EmailMessage(request.Recipient, "Re: " + (request.Prompt.Length > 50 ? request.Prompt[..50] : request.Prompt).Replace('\n', ' '),
            html, text, null, request.ExternalMessageId), ct);
    }
}
