using System.Collections;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HitlRelay.Data;
using HitlRelay.Models;

namespace HitlRelay.Core;

/// <summary>Replaces {{order.id}}-style placeholders from a workflow variable bag.</summary>
public static class TemplateRenderer
{
    private static readonly Regex Token = new(@"\{\{\s*([\w\.\-]+)\s*\}\}", RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, object?>? variables) =>
        Token.Replace(template ?? "", m => Resolve(variables, m.Groups[1].Value)?.ToString() ?? "");

    private static object? Resolve(IReadOnlyDictionary<string, object?>? variables, string path)
    {
        object? current = variables;
        foreach (var part in path.Split('.'))
        {
            current = current switch
            {
                IDictionary<string, object?> d => d.TryGetValue(part, out var v) ? v : null,
                IReadOnlyDictionary<string, object?> d => d.TryGetValue(part, out var v) ? v : null,
                IDictionary d => d.Contains(part) ? d[part] : null,
                JsonElement { ValueKind: JsonValueKind.Object } je => je.TryGetProperty(part, out var p) ? p : null,
                _ => null,
            };
            if (current == null) return null;
        }
        return current;
    }
}

public static class KeywordMatcher
{
    /// <summary>
    /// Typed-text matching (requirements 3.2): trim, case-insensitive, then keyword key, label and synonyms.
    /// Assumption: a bare 1-based number selects the keyword at that position (the WhatsApp numbered fallback).
    /// </summary>
    public static KeywordDef? Match(IReadOnlyList<KeywordDef> keywords, string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        foreach (var k in keywords)
        {
            if (Eq(k.Key, text) || Eq(k.Label, text) || k.Synonyms.Any(s => Eq(s, text)))
                return k;
        }

        if (int.TryParse(text, out var n) && n >= 1 && n <= keywords.Count)
            return keywords[n - 1];

        return null;
    }

    public static string Options(IReadOnlyList<KeywordDef> keywords) =>
        string.Join(", ", keywords.Select(k => k.Label));

    private static bool Eq(string a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
}

public static class ReplyValidator
{
    /// <summary>Returns an error message, or null when the open-ended reply is acceptable.</summary>
    public static string? ValidateOpenEnded(HitlNodeConfig cfg, string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return "Please send a text reply.";
        if (cfg.MinLength is int min && t.Length < min)
            return cfg.ValidationMessage ?? $"Your reply must be at least {min} characters.";
        if (!string.IsNullOrEmpty(cfg.ValidationRegex))
        {
            try
            {
                if (!Regex.IsMatch(t, cfg.ValidationRegex, RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                    return cfg.ValidationMessage ?? "Your reply is not in the expected format.";
            }
            catch (RegexMatchTimeoutException)
            {
                return cfg.ValidationMessage ?? "Your reply is not in the expected format.";
            }
        }
        return null;
    }
}

public static class OutcomeFormatter
{
    public static string Format(HitlOutcome o)
    {
        var time = o.At.ToString("HH:mm") + " UTC";
        return o.Status switch
        {
            HitlOutcomeStatus.Answered => $"✅ {o.Text ?? o.Value} by {o.ResponderDisplayName ?? "someone"} at {time}",
            HitlOutcomeStatus.TimedOut => $"⏱ No answer received; request timed out at {time}" +
                                          (string.IsNullOrEmpty(o.Text) ? "" : $" ({o.Text})"),
            _ => $"🚫 Request cancelled at {time}",
        };
    }
}

public static class Mask
{
    /// <summary>Masks personal data for logs: "priya@x.com" becomes "pr***".</summary>
    public static string Value(string? s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 2 ? "***" : s[..2] + "***");
}

public static class Crypto
{
    public static string RandomToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string ShortCode() => Base64Url(RandomNumberGenerator.GetBytes(9)); // 12 chars

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        return Convert.FromBase64String(t);
    }

    public static byte[] HmacSha256(string key, string data) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data));

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

public static class HitlTelemetry
{
    public static readonly Meter Meter = new("HitlRelay");
    public static readonly Counter<long> Sent = Meter.CreateCounter<long>("hitl.sent");
    public static readonly Counter<long> Answered = Meter.CreateCounter<long>("hitl.answered");
    public static readonly Counter<long> TimedOut = Meter.CreateCounter<long>("hitl.timed_out");
    public static readonly Histogram<double> TimeToRespond = Meter.CreateHistogram<double>("hitl.time_to_respond", "s");
}

/// <summary>Writes the audit trail (requirements 3.4 item 6). Add() only stages; callers save.</summary>
public sealed class AuditService
{
    private readonly HitlDbContext _db;
    private readonly TimeProvider _clock;

    public AuditService(HitlDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Add(Guid? requestId, string eventType, object? detail = null) =>
        _db.AuditEvents.Add(new HitlAuditEvent
        {
            RequestId = requestId,
            EventType = eventType,
            DetailJson = detail == null ? null : HitlJson.Serialize(detail),
            At = _clock.GetUtcNow().UtcDateTime,
        });

    public async Task WriteAsync(Guid? requestId, string eventType, object? detail = null, CancellationToken ct = default)
    {
        Add(requestId, eventType, detail);
        await _db.SaveChangesAsync(ct);
    }
}

public static class HttpHelpers
{
    /// <summary>Sends and honours HTTP 429 Retry-After (requirements section 7).</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = factory();
            var resp = await http.SendAsync(req, ct);
            if ((int)resp.StatusCode != 429 || attempt >= 3) return resp;

            var retry = resp.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);
            resp.Dispose();
            await Task.Delay(delay, ct);
        }
    }
}
