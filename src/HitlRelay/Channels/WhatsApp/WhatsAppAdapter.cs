using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.Extensions.Options;

namespace HitlRelay.Channels.WhatsApp;

/// <summary>
/// WhatsApp Business Cloud API.
///
/// IMPORTANT 24-HOUR RULE: the first message to a person outside the 24-hour customer-service window MUST be a
/// pre-approved template. Set HitlNodeConfig.WhatsAppTemplate (name, language, body variables) for such sends;
/// free-form and interactive messages only work inside the window. When a template is configured we send it
/// (with quick-reply buttons carrying the correlation payload when the template defines them); otherwise we send
/// a free-form interactive/text message and surface Meta's "re-engagement" error clearly.
///
/// Correlation: there are no threads, so replies match context.id (the wamid of our message) when present,
/// otherwise the sender's single most recent open request. HitlService enforces ONE OPEN REQUEST PER PHONE NUMBER
/// (across all workflows) so that fallback is unambiguous.
/// </summary>
public sealed class WhatsAppAdapter : IHitlChannelAdapter
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<WhatsAppAdapter> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public string ChannelKey => ChannelKeys.WhatsApp;

    private static string Digits(string phone) => new(phone.Where(char.IsDigit).ToArray());
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public async Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        var to = Digits(request.Recipient);
        var cfg = request.Config;
        object payload;

        if (cfg.WhatsAppTemplate is { Name.Length: > 0 } tpl)
            payload = BuildTemplate(request, to, tpl);
        else if (request.Mode == HitlResponseMode.Keyword)
            payload = BuildInteractive(request, to);
        else
            payload = new { messaging_product = "whatsapp", to, type = "text", text = new { body = Clip(request.Prompt, 4096) } };

        var root = await PostMessageAsync(payload, ct);
        var wamid = root.GetProperty("messages")[0].GetProperty("id").GetString();
        return new SendResult(wamid, null, to);
    }

    private object BuildTemplate(HitlRequest request, string to, WhatsAppTemplateConfig tpl)
    {
        var components = new List<object>();
        if (tpl.Variables.Count > 0)
        {
            components.Add(new
            {
                type = "body",
                parameters = tpl.Variables.Select(v => new { type = "text", text = Clip(v, 1024) }).ToArray(),
            });
        }
        if (request.Mode == HitlResponseMode.Keyword)
        {
            // Quick-reply buttons defined in the template; their payload comes back in the "button" message.
            for (var i = 0; i < Math.Min(request.Keywords.Count, 3); i++)
            {
                components.Add(new
                {
                    type = "button",
                    sub_type = "quick_reply",
                    index = i.ToString(),
                    parameters = new[] { new { type = "payload", payload = $"hitl:{request.CorrelationToken}:{i}" } },
                });
            }
        }
        return new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new { name = tpl.Name, language = new { code = tpl.Language }, components },
        };
    }

    private object BuildInteractive(HitlRequest request, string to)
    {
        var kws = request.Keywords;
        string Id(int i) => $"hitl:{request.CorrelationToken}:{i}";

        if (kws.Count <= 3)
        {
            return new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = Clip(request.Prompt, 1024) },
                    action = new
                    {
                        buttons = kws.Select((k, i) => new { type = "reply", reply = new { id = Id(i), title = Clip(k.Label, 20) } }).ToArray(),
                    },
                },
            };
        }

        if (kws.Count <= 10)
        {
            return new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    body = new { text = Clip(request.Prompt, 1024) },
                    action = new
                    {
                        button = "Choose",
                        sections = new[]
                        {
                            new { title = "Options", rows = kws.Select((k, i) => new { id = Id(i), title = Clip(k.Label, 24) }).ToArray() },
                        },
                    },
                },
            };
        }

        // More than 10 options: numbered text ("Reply 1 for Approve, 2 for Reject"); KeywordMatcher accepts the numbers.
        var lines = string.Join("\n", kws.Select((k, i) => $"Reply {i + 1} for {k.Label}"));
        return new { messaging_product = "whatsapp", to, type = "text", text = new { body = Clip($"{request.Prompt}\n\n{lines}", 4096) } };
    }

    public bool VerifyInbound(HttpRequest httpRequest, string rawBody)
    {
        if (string.IsNullOrEmpty(_opt.AppSecret)) return false;
        var header = httpRequest.Headers["X-Hub-Signature-256"].ToString();
        var expected = "sha256=" + Crypto.Hex(Crypto.HmacSha256(_opt.AppSecret, rawBody));
        return Crypto.FixedEquals(expected, header);
    }

    /// <summary>GET verification handshake (hub.verify_token).</summary>
    public bool VerifyHandshake(string? verifyToken) =>
        !string.IsNullOrEmpty(_opt.VerifyToken) && verifyToken != null && Crypto.FixedEquals(verifyToken, _opt.VerifyToken);

    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody) =>
        ParseInboundAll(httpRequest, rawBody).FirstOrDefault();

    /// <summary>Webhooks can batch several messages; status events (sent/delivered/read) are only logged.</summary>
    public IEnumerable<InboundReply> ParseInboundAll(HttpRequest httpRequest, string rawBody)
    {
        var replies = new List<InboundReply>();
        using var doc = JsonDocument.Parse(rawBody);
        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return replies;

        foreach (var entry in entries.EnumerateArray())
        foreach (var change in entry.GetProperty("changes").EnumerateArray())
        {
            var value = change.GetProperty("value");
            if (value.TryGetProperty("statuses", out var statuses))
                foreach (var s in statuses.EnumerateArray())
                    _log.LogDebug("WhatsApp status {Status}", s.TryGetProperty("status", out var st) ? st.GetString() : "?");

            if (!value.TryGetProperty("messages", out var messages)) continue;
            var name = value.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0 &&
                       contacts[0].TryGetProperty("profile", out var profile) && profile.TryGetProperty("name", out var n)
                ? n.GetString() : null;

            foreach (var m in messages.EnumerateArray())
            {
                var reply = ParseMessage(m, name);
                if (reply != null) replies.Add(reply);
            }
        }
        return replies;
    }

    private InboundReply? ParseMessage(JsonElement m, string? displayName)
    {
        var from = m.GetProperty("from").GetString() ?? "";
        var type = m.GetProperty("type").GetString();
        string? text = null, token = null;
        int? index = null;

        switch (type)
        {
            case "text":
                text = m.GetProperty("text").GetProperty("body").GetString();
                break;
            case "interactive":
                var inter = m.GetProperty("interactive");
                var kind = inter.GetProperty("type").GetString();
                var chosen = kind == "list_reply" ? inter.GetProperty("list_reply") : inter.GetProperty("button_reply");
                (token, index) = ParsePayload(chosen.GetProperty("id").GetString());
                text = chosen.TryGetProperty("title", out var title) ? title.GetString() : null;
                break;
            case "button": // template quick-reply
                var btn = m.GetProperty("button");
                (token, index) = ParsePayload(btn.TryGetProperty("payload", out var p) ? p.GetString() : null);
                text = btn.TryGetProperty("text", out var bt) ? bt.GetString() : null;
                break;
            default:
                return null; // images, reactions, etc. (attachments are a later phase)
        }

        return new InboundReply
        {
            Channel = ChannelKey,
            CorrelationToken = token,
            ActionIndex = token != null ? index : null,
            ExternalMessageId = m.GetProperty("id").GetString(),
            ExternalThreadId = m.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("id", out var cid) ? cid.GetString() : null,
            ExternalConversationId = from,
            ResponderId = from,
            ResponderDisplayName = displayName ?? from,
            Text = text,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    private static (string? token, int? index) ParsePayload(string? id)
    {
        var parts = id?.Split(':');
        return parts is { Length: 3 } && parts[0] == "hitl" && int.TryParse(parts[2], out var i) ? (parts[1], i) : (null, null);
    }

    // WhatsApp messages cannot be edited, so the outcome is sent as a follow-up.
    public Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct) =>
        SendTextAsync(request, OutcomeFormatter.Format(outcome), ct);

    public async Task SendTextAsync(HitlRequest request, string text, CancellationToken ct) =>
        await PostMessageAsync(new
        {
            messaging_product = "whatsapp",
            to = Digits(request.Recipient),
            type = "text",
            text = new { body = Clip(text, 4096) },
        }, ct);

    private async Task<JsonElement> PostMessageAsync(object payload, CancellationToken ct)
    {
        using var resp = await HttpHelpers.SendAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.facebook.com/{_opt.ApiVersion}/{_opt.PhoneNumberId}/messages")
            { Content = JsonContent.Create(payload, options: Json) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.AccessToken);
            return req;
        }, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
        {
            using var ok = JsonDocument.Parse(body);
            return ok.RootElement.Clone();
        }

        var code = 0;
        var message = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.GetProperty("error");
            code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            message = err.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
        }
        catch (JsonException) { /* keep raw body */ }

        // 131026 undeliverable/not on WhatsApp, 131047 outside 24h window (template required),
        // 131049 ecosystem engagement limit, 131030 recipient not in allowed list (test numbers).
        if (code is 131026 or 131047 or 131049 or 131030)
            throw new HitlRecipientUnreachableException(
                $"WhatsApp recipient cannot be messaged (code {code}): {message}. " +
                "Outside the 24-hour window you must send an approved template (set WhatsAppTemplate).");
        throw new HttpRequestException($"WhatsApp API error {(int)resp.StatusCode} (code {code}): {message}");
    }
}
