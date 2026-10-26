using System.Net;
using System.Security.Cryptography;
using System.Text;
using HitlRelay.Channels.Email;
using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HitlRelay.Controllers;

[ApiController]
public sealed class EmailController : WebhookControllerBase
{
    private readonly EmailTokenService _tokens;
    private readonly HitlDbContext _db;
    private readonly HitlReplyHandler _handler;
    private readonly IHitlChannelAdapter _adapter;
    private readonly ILogger<EmailController> _log;

    public EmailController(EmailTokenService tokens, HitlDbContext db, HitlReplyHandler handler, EmailAdapter adapter,
        InboundQueue queue, ILogger<EmailController> log) : base(queue)
    {
        _tokens = tokens;
        _db = db;
        _handler = handler;
        _adapter = adapter;
        _log = log;
    }

    protected override ILogger Log => _log;

    /// <summary>
    /// Link landing page. GET only shows a confirmation page; mail scanners prefetch links, so nothing is
    /// recorded until the person presses the button (POST).
    /// </summary>
    [HttpGet("/hitl/email/respond")]
    public async Task<IActionResult> Landing([FromQuery] string? token, CancellationToken ct)
    {
        if (!_tokens.TryValidate(token, out var requestId, out var key))
            return Page("Link not valid", "This link is invalid or has expired.", 400);

        var req = await _db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (req == null || req.Channel != ChannelKeys.Email)
            return Page("Link not valid", "This link is invalid or has expired.", 400);
        if (req.Status != HitlRequestStatus.Pending)
            return Page("Already answered", HitlReplyHandler.AlreadyAnsweredText);

        var prompt = WebUtility.HtmlEncode(req.Prompt).Replace("\n", "<br/>");
        var tokenAttr = WebUtility.HtmlEncode(token);
        string form;
        if (req.Mode == HitlResponseMode.Keyword)
        {
            var label = req.Keywords.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase))?.Label;
            if (label == null) return Page("Link not valid", "This link is invalid or has expired.", 400);
            form = $"<p>You chose: <b>{WebUtility.HtmlEncode(label)}</b></p>" +
                   $"<form method=\"post\"><input type=\"hidden\" name=\"token\" value=\"{tokenAttr}\"/><button type=\"submit\">Confirm</button></form>";
        }
        else
        {
            form = $"<form method=\"post\"><input type=\"hidden\" name=\"token\" value=\"{tokenAttr}\"/>" +
                   "<textarea name=\"text\" rows=\"6\" style=\"width:100%\" required></textarea><br/><button type=\"submit\">Send reply</button></form>";
        }
        return Page("Your response", $"<p>{prompt}</p>{form}", rawBody: true);
    }

    [HttpPost("/hitl/email/respond")]
    public async Task<IActionResult> Submit(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        if (!_tokens.TryValidate(token, out var requestId, out var key))
            return Page("Link not valid", "This link is invalid or has expired.", 400);

        var req = await _db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (req == null || req.Channel != ChannelKeys.Email)
            return Page("Link not valid", "This link is invalid or has expired.", 400);

        // The link was sent to the recipient, so whoever holds it is treated as the recipient. The same
        // Allowed responders check as every other channel then applies to the recipient's address.
        var reply = new InboundReply
        {
            Channel = ChannelKeys.Email,
            CorrelationToken = req.CorrelationToken,
            ActionValue = key,
            Text = req.Mode == HitlResponseMode.OpenEnded ? form["text"].ToString() : null,
            // Includes the text so a corrected resubmission after a validation error is not mistaken for a duplicate.
            ExternalMessageId = "link:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + "|" + form["text"].ToString()))),
            ExternalConversationId = req.ExternalConversationId,
            ResponderId = req.Recipient.ToLowerInvariant(),
            ResponderEmail = req.Recipient.ToLowerInvariant(),
            ResponderDisplayName = req.Recipient,
            ReceivedAt = DateTime.UtcNow,
        };

        // Handled inline (not queued) because the person is waiting for a result page.
        var result = await _handler.HandleAsync(reply, ct);
        return result.Status switch
        {
            ReplyStatus.Resumed => Page("Thank you", "Your answer was recorded."),
            ReplyStatus.Duplicate or ReplyStatus.AlreadyAnswered => Page("Already answered", HitlReplyHandler.AlreadyAnsweredText),
            ReplyStatus.Invalid => Page("Please check your reply", result.Message),
            ReplyStatus.NotAllowed => Page("Not allowed", result.Message, 403),
            _ => Page("Could not record answer", result.Message, 400),
        };
    }

    /// <summary>Optional inbound-mail webhook for reply-by-email (provider posts parsed JSON; see EmailAdapter).</summary>
    [HttpPost("/hitl/email/inbound")]
    public async Task<IActionResult> Inbound() => await ProcessAsync(_adapter, await WebhookBody.ReadAsync(Request));

    private ContentResult Page(string title, string body, int status = 200, bool rawBody = false)
    {
        var inner = rawBody ? body : $"<p>{WebUtility.HtmlEncode(body)}</p>";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>" +
                   $"<title>{WebUtility.HtmlEncode(title)}</title>" +
                   "<style>body{font-family:Arial,sans-serif;max-width:560px;margin:40px auto;padding:0 16px}button{padding:10px 18px;background:#1a56db;color:#fff;border:0;border-radius:6px;font-size:16px;cursor:pointer}</style>" +
                   $"</head><body><h2>{WebUtility.HtmlEncode(title)}</h2>{inner}</body></html>";
        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = status };
    }
}
