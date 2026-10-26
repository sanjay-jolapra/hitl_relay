using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HitlRelay.Core;
using Microsoft.Extensions.Options;

namespace HitlRelay.Channels.Teams;

/// <summary>
/// Finds or creates the Teams channel for a key (for example an order id) inside the fixed team, using Microsoft Graph
/// with the app's own credentials (application permission Channel.Create; ChannelSettings.Read.All to look channels up).
/// The key-to-channel mapping is cached (encrypted) in HitlChannelConnection so later requests reuse the channel.
/// </summary>
public sealed class TeamsChannelProvisioner
{
    private const string MappingChannel = "teams-channel";
    private static readonly SemaphoreSlim CreateLock = new(1, 1); // one creator per process; Graph rejects duplicate names across instances

    private readonly HttpClient _http;
    private readonly TeamsOptions _opt;
    private readonly ChannelConnectionStore _store;
    private readonly ILogger<TeamsChannelProvisioner> _log;
    private static string? _token;
    private static DateTimeOffset _tokenExpiry;

    public TeamsChannelProvisioner(HttpClient http, IOptions<TeamsOptions> opt, ChannelConnectionStore store,
        ILogger<TeamsChannelProvisioner> log)
    {
        _http = http;
        _opt = opt.Value;
        _store = store;
        _log = log;
    }

    /// <summary>Returns the Graph/Teams channel id (19:...@thread.tacv2) for the key, creating the channel if needed.</summary>
    public async Task<string> EnsureChannelAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.TeamId))
            throw new InvalidOperationException("Channels:Teams:TeamId must be set to use channel-per-order mode.");

        var cached = await _store.GetAsync(MappingChannel, key, ct);
        if (cached != null) return cached;

        await CreateLock.WaitAsync(ct);
        try
        {
            cached = await _store.GetAsync(MappingChannel, key, ct); // another caller may have created it meanwhile
            if (cached != null) return cached;

            var name = ChannelName(_opt.ChannelNamePrefix, key);
            var id = await CreateAsync(name, ct) ?? await FindByNameAsync(name, ct)
                ?? throw new HttpRequestException($"Could not create or find Teams channel '{name}'.");
            await _store.SaveAsync(MappingChannel, key, id, ct);
            _log.LogInformation("Teams channel '{Name}' ready for key {Key}", name, key);
            return id;
        }
        finally
        {
            CreateLock.Release();
        }
    }

    /// <summary>
    /// Teams channel names: at most 50 characters, none of ~ # % &amp; * { } + / \ : &lt; &gt; ? | ' " and no leading or trailing
    /// dots or spaces. Forbidden characters become '-'. Two keys that differ only in forbidden characters share a channel.
    /// </summary>
    public static string ChannelName(string prefix, string key)
    {
        var name = Regex.Replace(prefix + key, @"[~#%&*{}+/\\:<>?|'""]", "-").Trim().Trim('.').Trim();
        if (name.Length > 50) name = name[..50].TrimEnd('.', ' ');
        return name.Length == 0 ? "Order" : name;
    }

    private async Task<string?> CreateAsync(string name, CancellationToken ct)
    {
        using var resp = await HttpHelpers.SendAsync(_http, () => Authorized(
            new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/teams/{_opt.TeamId}/channels")
            {
                Content = JsonContent.Create(new { displayName = name, description = _opt.ChannelDescription, membershipType = "standard" }),
            }, ct), ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString();
        }
        // 400/409 "NameAlreadyExists": the channel exists (created earlier, by another instance, or by hand).
        if (body.Contains("NameAlreadyExists", StringComparison.OrdinalIgnoreCase) || (int)resp.StatusCode == 409)
            return null;
        throw new HttpRequestException($"Graph create channel failed ({(int)resp.StatusCode}): {body}");
    }

    private async Task<string?> FindByNameAsync(string name, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq '{name.Replace("'", "''")}'");
        using var resp = await HttpHelpers.SendAsync(_http, () => Authorized(
            new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/teams/{_opt.TeamId}/channels?$filter={filter}"), ct), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Graph channel lookup failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? first.GetProperty("id").GetString() : null;
    }

    private HttpRequestMessage Authorized(HttpRequestMessage req, CancellationToken ct)
    {
        // Token is fetched synchronously-cached; the first call blocks briefly inside the factory.
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetTokenAsync(ct).GetAwaiter().GetResult());
        return req;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token != null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

        using var resp = await _http.PostAsync(
            $"https://login.microsoftonline.com/{_opt.MicrosoftAppTenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _opt.MicrosoftAppId,
                ["client_secret"] = _opt.MicrosoftAppPassword,
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
