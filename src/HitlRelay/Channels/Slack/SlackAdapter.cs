using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.Extensions.Options;

namespace HitlRelay.Channels.Slack;

/// <summary>
/// Slack app: bot scopes chat:write, im:write, im:history (and channels:history for channel threads).
/// Keyword mode uses Block Kit buttons; open-ended mode correlates thread replies by thread_ts.
/// Assumption: the optional modal flow is not implemented; thread replies and DM replies cover open-ended mode.
/// </summary>
public sealed class SlackAdapter : IHitlChannelAdapter
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly HashSet<string> UnreachableErrors = new()
    {
        "channel_not_found", "user_not_found", "not_in_channel", "cannot_dm_bot", "user_disabled", "is_archived", "account_inactive",
    };

    private readonly HttpClient _http;
    private readonly SlackOptions _opt;
    private readonly TimeProvider _clock;

    private readonly SlackChannelProvisioner? _channels;

    // channels is optional only so the adapter can be constructed without a database (signature tests).
    public SlackAdapter(HttpClient http, IOptions<SlackOptions> opt, TimeProvider clock, SlackChannelProvisioner? channels = null)
    {
        _http = http;
        _opt = opt.Value;
        _clock = clock;
        _channels = channels;
    }

    public string ChannelKey => ChannelKeys.Slack;

    public async Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        var blocks = new List<object> { Section(request.Prompt) };
        if (request.Mode == HitlResponseMode.Keyword)
        {
            blocks.Add(new
            {
                type = "actions",
                block_id = "hitl_actions",
                elements = request.Keywords.Select((k, i) => new
                {
                    type = "button",
                    text = new { type = "plain_text", text = k.Label },
                    action_id = $"hitl:{request.CorrelationToken}:{i}", // action_id embeds the correlation token
                    value = k.Key,
                }).ToArray(),
            });
        }
        else
        {
            blocks.Add(new { type = "context", elements = new[] { new { type = "mrkdwn", text = "_Reply in this thread to answer._" } } });
        }

        // Channel-per-key mode: find or create the channel, then post a NEW top-level message (its own thread) in it.
        var target = request.Recipient;
        var key = request.Config.SlackChannelKey;
        if (!string.IsNullOrWhiteSpace(key))
            target = await (_channels ?? throw new InvalidOperationException("Slack channel provisioning is not configured."))
                .EnsureChannelAsync(key, ct);

        var resp = await CallAsync("chat.postMessage", new { channel = target, text = request.Prompt, blocks }, ct);
        var ts = resp.GetProperty("ts").GetString();
        var channel = resp.GetProperty("channel").GetString();
        return new SendResult(ts, ts, channel);
    }

    public bool VerifyInbound(HttpRequest httpRequest, string rawBody)
    {
        if (string.IsNullOrEmpty(_opt.SigningSecret)) return false;
        var timestamp = httpRequest.Headers["X-Slack-Request-Timestamp"].ToString();
        var signature = httpRequest.Headers["X-Slack-Signature"].ToString();
        if (!long.TryParse(timestamp, out var ts)) return false;
        if (Math.Abs(_clock.GetUtcNow().ToUnixTimeSeconds() - ts) > 300) return false; // replay protection: 5 minutes

        var expected = "v0=" + Crypto.Hex(Crypto.HmacSha256(_opt.SigningSecret, $"v0:{timestamp}:{rawBody}"));
        return Crypto.FixedEquals(expected, signature);
    }

    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody)
    {
        // Interactivity posts application/x-www-form-urlencoded with a single "payload" JSON field.
        if (rawBody.StartsWith("payload=", StringComparison.Ordinal))
        {
            var payload = HttpUtility.ParseQueryString(rawBody)["payload"];
            return payload == null ? null : ParseInteractivity(payload);
        }
        return ParseEvent(rawBody);
    }

    private InboundReply? ParseInteractivity(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "block_actions") return null;

        var action = root.GetProperty("actions")[0];
        var actionId = action.GetProperty("action_id").GetString() ?? "";
        var parts = actionId.Split(':');
        if (parts.Length != 3 || parts[0] != "hitl") return null;

        var user = root.GetProperty("user");
        return new InboundReply
        {
            Channel = ChannelKey,
            CorrelationToken = parts[1],
            ActionValue = action.GetProperty("value").GetString(),
            // action_ts is identical on Slack's redelivery of the same click, so it is a safe idempotency key.
            ExternalMessageId = $"action:{parts[1]}:{action.GetProperty("action_ts").GetString()}",
            ExternalConversationId = root.TryGetProperty("channel", out var ch) ? ch.GetProperty("id").GetString() : null,
            ExternalThreadId = root.TryGetProperty("message", out var m) ? m.GetProperty("ts").GetString() : null,
            ResponderId = user.GetProperty("id").GetString() ?? "",
            ResponderDisplayName = user.TryGetProperty("name", out var n) ? n.GetString() : null,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    private InboundReply? ParseEvent(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "event_callback") return null; // url_verification handled by the controller

        var ev = root.GetProperty("event");
        if (ev.GetProperty("type").GetString() != "message") return null;
        if (ev.TryGetProperty("subtype", out _) || ev.TryGetProperty("bot_id", out _)) return null; // edits, bot echoes

        var text = ev.TryGetProperty("text", out var t) ? t.GetString() : null;
        var user = ev.TryGetProperty("user", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(user)) return null;

        var threadTs = ev.TryGetProperty("thread_ts", out var tt) ? tt.GetString() : null;
        var channelType = ev.TryGetProperty("channel_type", out var ct) ? ct.GetString() : null;
        // Un-threaded messages are only trusted in a 1:1 DM; in channels they are ordinary chatter.
        if (threadTs == null && channelType != "im") return null;

        return new InboundReply
        {
            Channel = ChannelKey,
            ExternalMessageId = "event:" + root.GetProperty("event_id").GetString(),
            ExternalThreadId = threadTs,
            ExternalConversationId = ev.GetProperty("channel").GetString(),
            ResponderId = user,
            Text = text,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    public Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct) =>
        CallAsync("chat.update", new
        {
            channel = request.ExternalConversationId,
            ts = request.ExternalMessageId,
            text = request.Prompt,
            // Replacing the blocks drops the buttons and shows the outcome instead.
            blocks = new object[] { Section(request.Prompt), Section(OutcomeFormatter.Format(outcome)) },
        }, ct);

    public Task SendTextAsync(HitlRequest request, string text, CancellationToken ct) =>
        CallAsync("chat.postMessage", new
        {
            channel = request.ExternalConversationId ?? request.Recipient,
            thread_ts = request.ExternalThreadId,
            text,
        }, ct);

    private static object Section(string text) => new { type = "section", text = new { type = "mrkdwn", text } };

    private async Task<JsonElement> CallAsync(string method, object payload, CancellationToken ct)
    {
        using var resp = await HttpHelpers.SendAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}")
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.BotToken);
            return req;
        }, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return root;

        var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown_error" : "unknown_error";
        if (method == "chat.postMessage" && UnreachableErrors.Contains(error))
            throw new HitlRecipientUnreachableException($"Slack recipient is not reachable: {error}");
        throw new HttpRequestException($"Slack {method} failed: {error}");
    }
}
