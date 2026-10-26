using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.Extensions.Options;

namespace HitlRelay.Channels.Telegram;

/// <summary>
/// Telegram Bot API. The recipient is a numeric chat_id: the person must open the bot and press Start once
/// (see README) - otherwise sendMessage returns 403/400 and we fail fast as "unreachable".
/// </summary>
public sealed class TelegramAdapter : IHitlChannelAdapter
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly TelegramOptions _opt;
    private readonly ILogger<TelegramAdapter> _log;

    public TelegramAdapter(HttpClient http, IOptions<TelegramOptions> opt, ILogger<TelegramAdapter> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public string ChannelKey => ChannelKeys.Telegram;

    public async Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        object replyMarkup = request.Mode == HitlResponseMode.Keyword
            ? new
            {
                // callback_data is limited to 64 bytes, so it carries a lookup id + keyword index, not the token.
                inline_keyboard = request.Keywords
                    .Select((k, i) => new[] { new { text = k.Label, callback_data = $"h|{request.ShortCode}|{i}" } })
                    .ToArray(),
            }
            : new { force_reply = true, input_field_placeholder = "Type your reply" };

        var result = await CallAsync("sendMessage", new { chat_id = request.Recipient, text = Truncate(request.Prompt), reply_markup = replyMarkup }, ct);
        var msg = result.GetProperty("result");
        return new SendResult(
            msg.GetProperty("message_id").GetInt64().ToString(),
            null,
            msg.GetProperty("chat").GetProperty("id").GetInt64().ToString());
    }

    public bool VerifyInbound(HttpRequest httpRequest, string rawBody)
    {
        var expected = _opt.WebhookSecret;
        if (string.IsNullOrEmpty(expected)) return false; // never accept unauthenticated webhooks
        var actual = httpRequest.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        return Crypto.FixedEquals(actual, expected);
    }

    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        var updateId = root.TryGetProperty("update_id", out var u) ? u.GetInt64().ToString() : null;

        if (root.TryGetProperty("callback_query", out var cb))
        {
            var parts = (cb.TryGetProperty("data", out var d) ? d.GetString() : null)?.Split('|');
            if (parts is not { Length: 3 } || parts[0] != "h" || !int.TryParse(parts[2], out var index)) return null;

            var from = cb.GetProperty("from");
            var message = cb.TryGetProperty("message", out var m) ? m : default;
            return new InboundReply
            {
                Channel = ChannelKey,
                CorrelationShortCode = parts[1],
                ActionIndex = index,
                AckToken = cb.GetProperty("id").GetString(),
                ExternalMessageId = $"update:{updateId}",
                ExternalConversationId = message.ValueKind == JsonValueKind.Object
                    ? message.GetProperty("chat").GetProperty("id").GetInt64().ToString() : null,
                ExternalThreadId = message.ValueKind == JsonValueKind.Object
                    ? message.GetProperty("message_id").GetInt64().ToString() : null,
                ResponderId = from.GetProperty("id").GetInt64().ToString(),
                ResponderDisplayName = DisplayName(from),
                ResponderAlias = from.TryGetProperty("username", out var un) ? un.GetString() : null,
                ReceivedAt = DateTime.UtcNow,
            };
        }

        if (root.TryGetProperty("message", out var msg))
        {
            var from = msg.GetProperty("from");
            if (from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean()) return null;
            var text = msg.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(text) || text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) return null;

            return new InboundReply
            {
                Channel = ChannelKey,
                ExternalMessageId = $"update:{updateId}",
                // ForceReply: the user's message points back at ours via reply_to_message.
                ExternalThreadId = msg.TryGetProperty("reply_to_message", out var rt)
                    ? rt.GetProperty("message_id").GetInt64().ToString() : null,
                ExternalConversationId = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString(),
                ResponderId = from.GetProperty("id").GetInt64().ToString(),
                ResponderDisplayName = DisplayName(from),
                ResponderAlias = from.TryGetProperty("username", out var un2) ? un2.GetString() : null,
                Text = text,
                ReceivedAt = DateTime.UtcNow,
            };
        }

        return null; // edited_message, my_chat_member, etc.
    }

    public Task AcknowledgeInboundAsync(InboundReply reply, CancellationToken ct) =>
        reply.AckToken == null
            ? Task.CompletedTask
            : CallAsync("answerCallbackQuery", new { callback_query_id = reply.AckToken }, ct);

    public async Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct)
    {
        if (request.ExternalMessageId == null) return;
        var messageId = long.Parse(request.ExternalMessageId);
        if (request.Mode == HitlResponseMode.Keyword)
        {
            await CallAsync("editMessageReplyMarkup", new
            {
                chat_id = request.ExternalConversationId,
                message_id = messageId,
                reply_markup = new { inline_keyboard = Array.Empty<object>() },
            }, ct);
        }
        await CallAsync("editMessageText", new
        {
            chat_id = request.ExternalConversationId,
            message_id = messageId,
            text = Truncate($"{request.Prompt}\n\n{OutcomeFormatter.Format(outcome)}"),
        }, ct);
    }

    public Task SendTextAsync(HitlRequest request, string text, CancellationToken ct) =>
        CallAsync("sendMessage", new { chat_id = request.ExternalConversationId ?? request.Recipient, text = Truncate(text) }, ct);

    /// <summary>Registers the webhook with a secret_token (called at startup when AutoRegisterWebhook is on).</summary>
    public Task SetWebhookAsync(string url, CancellationToken ct) =>
        CallAsync("setWebhook", new { url, secret_token = _opt.WebhookSecret, allowed_updates = new[] { "message", "callback_query" } }, ct);

    private async Task<JsonElement> CallAsync(string method, object payload, CancellationToken ct)
    {
        using var resp = await HttpHelpers.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post,
            $"https://api.telegram.org/bot{_opt.BotToken}/{method}")
        { Content = JsonContent.Create(payload, options: Json) }, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return root;

        var code = root.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)resp.StatusCode;
        var desc = root.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : body;
        if (method == "sendMessage" && (code == 403 || (code == 400 && desc.Contains("chat not found", StringComparison.OrdinalIgnoreCase))))
            throw new HitlRecipientUnreachableException(
                "Telegram chat is not reachable: the person must start the bot (press Start) before it can message them. " + desc);
        throw new HttpRequestException($"Telegram {method} failed ({code}): {desc}");
    }

    private static string DisplayName(JsonElement user)
    {
        var first = user.TryGetProperty("first_name", out var f) ? f.GetString() : null;
        var last = user.TryGetProperty("last_name", out var l) ? l.GetString() : null;
        var name = $"{first} {last}".Trim();
        return name.Length > 0 ? name : user.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
    }

    private static string Truncate(string s) => s.Length <= 4096 ? s : s[..4090] + "...";
}

/// <summary>Registers the Telegram webhook on startup when Channels:Telegram:AutoRegisterWebhook is true.</summary>
public sealed class TelegramWebhookRegistrar : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<TelegramOptions> _opt;
    private readonly IOptions<HitlOptions> _hitl;
    private readonly ILogger<TelegramWebhookRegistrar> _log;

    public TelegramWebhookRegistrar(IServiceScopeFactory scopes, IOptions<TelegramOptions> opt, IOptions<HitlOptions> hitl,
        ILogger<TelegramWebhookRegistrar> log)
    {
        _scopes = scopes;
        _opt = opt;
        _hitl = hitl;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_opt.Value.AutoRegisterWebhook) return;
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TelegramAdapter>()
                .SetWebhookAsync($"{_hitl.Value.BaseUrl.TrimEnd('/')}/telegram/webhook", ct);
            _log.LogInformation("Telegram webhook registered");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Telegram webhook registration failed");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
