using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HitlRelay.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace HitlRelay.Channels.Email;

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody, string? ReplyTo, string? InReplyTo = null);

/// <summary>Sends one e-mail and returns its Message-ID (without angle brackets), used for reply correlation.</summary>
public interface IEmailSender
{
    Task<string> SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>SMTP via MailKit. We set our own Message-ID so inbound In-Reply-To can be matched.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _opt;

    public SmtpEmailSender(IOptions<EmailOptions> opt) => _opt = opt.Value;

    public async Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        var domain = _opt.FromAddress.Split('@').LastOrDefault() ?? "localhost";
        var messageId = $"{Guid.NewGuid():N}@{domain}";

        var mime = new MimeMessage { MessageId = messageId, Subject = message.Subject };
        mime.From.Add(new MailboxAddress(_opt.FromName, _opt.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (!string.IsNullOrEmpty(message.ReplyTo)) mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        if (!string.IsNullOrEmpty(message.InReplyTo)) mime.InReplyTo = message.InReplyTo;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(_opt.Smtp.Host, _opt.Smtp.Port,
                _opt.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
            if (!string.IsNullOrEmpty(_opt.Smtp.User))
                await client.AuthenticateAsync(_opt.Smtp.User, _opt.Smtp.Password, ct);
            await client.SendAsync(mime, ct);
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted)
        {
            throw new HitlRecipientUnreachableException($"Mail server rejected the recipient: {ex.Message}", ex);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(true, CancellationToken.None);
        }
        return messageId;
    }
}

/// <summary>
/// Microsoft Graph variant (client-credentials, Mail.Send application permission). A draft is created first
/// because that is the only way to learn the internetMessageId before sending; the draft is then sent.
/// </summary>
public sealed class GraphEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly EmailOptions _opt;
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public GraphEmailSender(HttpClient http, IOptions<EmailOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
    }

    public async Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        var user = Uri.EscapeDataString(_opt.Graph.SenderUser);

        var draft = new
        {
            subject = message.Subject,
            body = new { contentType = "HTML", content = message.HtmlBody },
            toRecipients = new[] { new { emailAddress = new { address = message.To } } },
            replyTo = string.IsNullOrEmpty(message.ReplyTo)
                ? Array.Empty<object>()
                : new object[] { new { emailAddress = new { address = message.ReplyTo } } },
        };

        using var createResp = await HttpHelpers.SendAsync(_http, () => Authorized(token,
            new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{user}/messages")
            { Content = JsonContent.Create(draft) }), ct);
        await EnsureOkAsync(createResp, "create draft", ct);

        using var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(ct));
        var id = created.RootElement.GetProperty("id").GetString()!;
        var internetMessageId = created.RootElement.GetProperty("internetMessageId").GetString() ?? "";

        using var sendResp = await HttpHelpers.SendAsync(_http, () => Authorized(token,
            new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{user}/messages/{id}/send")), ct);
        await EnsureOkAsync(sendResp, "send", ct);

        return internetMessageId.Trim('<', '>');
    }

    private static HttpRequestMessage Authorized(string token, HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string step, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Contains("ErrorInvalidRecipients", StringComparison.OrdinalIgnoreCase))
            throw new HitlRecipientUnreachableException($"Graph rejected the recipient ({step}).");
        throw new HttpRequestException($"Graph {step} failed ({(int)resp.StatusCode}): {body}");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token != null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

        using var resp = await _http.PostAsync(
            $"https://login.microsoftonline.com/{_opt.Graph.TenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _opt.Graph.ClientId,
                ["client_secret"] = _opt.Graph.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            }), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        _token = doc.RootElement.GetProperty("access_token").GetString()!;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return _token;
    }
}

/// <summary>
/// Signed, expiring link tokens: base64url("requestId|expUnix|keywordKey") + "." + base64url(HMAC-SHA256).
/// Single use is enforced by the request status (the first answer closes the request).
/// </summary>
public sealed class EmailTokenService
{
    private readonly EmailOptions _opt;
    private readonly TimeProvider _clock;

    public EmailTokenService(IOptions<EmailOptions> opt, TimeProvider clock)
    {
        _opt = opt.Value;
        _clock = clock;
    }

    public string Create(Guid requestId, string? keywordKey, DateTime expiresUtc)
    {
        var exp = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var payload = Crypto.Base64Url(Encoding.UTF8.GetBytes($"{requestId:N}|{exp}|{keywordKey}"));
        return payload + "." + Crypto.Base64Url(Crypto.HmacSha256(Key, payload));
    }

    public bool TryValidate(string? token, out Guid requestId, out string? keywordKey)
    {
        requestId = Guid.Empty;
        keywordKey = null;
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0) return false;

        var payload = token[..dot];
        var sig = token[(dot + 1)..];
        if (!Crypto.FixedEquals(Crypto.Base64Url(Crypto.HmacSha256(Key, payload)), sig)) return false;

        try
        {
            var parts = Encoding.UTF8.GetString(Crypto.FromBase64Url(payload)).Split('|', 3);
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out requestId)) return false;
            if (!long.TryParse(parts[1], out var exp) || _clock.GetUtcNow().ToUnixTimeSeconds() > exp) return false;
            keywordKey = parts[2].Length == 0 ? null : parts[2];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string Key => string.IsNullOrEmpty(_opt.LinkSigningKey)
        ? throw new InvalidOperationException("Channels:Email:LinkSigningKey must be configured.")
        : _opt.LinkSigningKey;
}

/// <summary>Strips quoted history and signatures from reply-by-email bodies.</summary>
public static class EmailReplyCleaner
{
    private static readonly Regex[] Markers =
    {
        new(@"^\s*On .+ wrote:\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*-{2,}\s*Original Message\s*-{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*_{5,}\s*$", RegexOptions.Compiled),
        new(@"^\s*From:\s.+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*Sent from my ", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^-- ?$", RegexOptions.Compiled), // standard signature delimiter
    };

    public static string Clean(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var kept = new List<string>();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith('>')) continue;
            if (Markers.Any(m => m.IsMatch(line))) break;
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }
}
