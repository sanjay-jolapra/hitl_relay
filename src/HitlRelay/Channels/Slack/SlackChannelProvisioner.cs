using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HitlRelay.Channels.Teams;
using HitlRelay.Core;
using Microsoft.Extensions.Options;

namespace HitlRelay.Channels.Slack;

/// <summary>
/// Finds or creates the Slack channel for a key (for example an order id) in the workspace the bot token belongs to.
/// Slack cannot create standalone workspaces through its API, so the workspace itself must already exist and have the
/// app installed; only channels are created here. Bot scopes needed: channels:manage (public), groups:write (private),
/// channels:join, channels:read, groups:read. The key to channel id mapping is cached (encrypted) in HitlChannelConnection.
/// </summary>
public sealed class SlackChannelProvisioner
{
    private const string MappingChannel = "slack-channel";
    private static readonly SemaphoreSlim CreateLock = new(1, 1);

    private readonly HttpClient _http;
    private readonly SlackOptions _opt;
    private readonly ChannelConnectionStore _store;
    private readonly ILogger<SlackChannelProvisioner> _log;

    public SlackChannelProvisioner(HttpClient http, IOptions<SlackOptions> opt, ChannelConnectionStore store,
        ILogger<SlackChannelProvisioner> log)
    {
        _http = http;
        _opt = opt.Value;
        _store = store;
        _log = log;
    }

    /// <summary>Returns the Slack channel id for a key (prefix + key becomes the name), creating the channel if needed.</summary>
    public Task<string> EnsureChannelAsync(string key, IReadOnlyList<string>? inviteEmails, CancellationToken ct) =>
        EnsureAsync(key, ChannelName(_opt.ChannelNamePrefix, key), inviteEmails, ct);

    /// <summary>Same, for a channel the node names directly (for example "#approvals"): no prefix is added.</summary>
    public Task<string> EnsureNamedChannelAsync(string name, IReadOnlyList<string>? inviteEmails, CancellationToken ct)
    {
        var clean = ChannelName("", name.TrimStart('#'));
        return EnsureAsync("name:" + clean, clean, inviteEmails, ct);
    }

    /// <summary>Drops the cached id so the next Ensure looks the channel up again, or creates it (deleted/archived channel).</summary>
    public Task ForgetAsync(string cacheKey, CancellationToken ct) => _store.DeleteAsync(MappingChannel, cacheKey, ct);

    public static string CacheKeyFor(string key) => key;
    public static string NamedCacheKey(string name) => "name:" + ChannelName("", name.TrimStart('#'));

    private async Task<string> EnsureAsync(string cacheKey, string name, IReadOnlyList<string>? inviteEmails, CancellationToken ct)
    {
        var cached = await _store.GetAsync(MappingChannel, cacheKey, ct);
        if (cached != null) return cached;

        await CreateLock.WaitAsync(ct);
        try
        {
            cached = await _store.GetAsync(MappingChannel, cacheKey, ct); // another caller may have created it meanwhile
            if (cached != null) return cached;

            string id;
            var created = await TryCreateAsync(name, ct);
            if (created != null)
            {
                id = created;
                await InviteAsync(id, inviteEmails, ct); // only on creation, so people who left are not re-added later
                _log.LogInformation("Slack channel #{Name} created", name);
            }
            else
            {
                var (foundId, archived) = await FindByNameAsync(name, ct)
                    ?? throw new HttpRequestException($"Slack channel #{name} exists but could not be found (is a read scope missing, or is it a private channel the bot is not in?).");
                if (archived) await UnarchiveAsync(foundId, name, ct);
                id = foundId;
                await JoinAsync(id, ct);
                _log.LogInformation("Slack channel #{Name} already existed; reusing it", name);
            }

            await _store.SaveAsync(MappingChannel, cacheKey, id, ct);
            return id;
        }
        finally
        {
            CreateLock.Release();
        }
    }

    /// <summary>
    /// Slack channel names: lowercase, at most 80 characters, only letters, digits, hyphens and underscores.
    /// Anything else becomes '-'. Keys differing only in case or punctuation share a channel.
    /// </summary>
    public static string ChannelName(string prefix, string key)
    {
        var name = Regex.Replace((prefix + key).ToLowerInvariant(), @"[^a-z0-9_\-]+", "-").Trim('-', '_');
        if (name.Length > 80) name = name[..80].TrimEnd('-', '_');
        return name.Length == 0 ? "order" : name;
    }

    private async Task<string?> TryCreateAsync(string name, CancellationToken ct)
    {
        var (ok, error, root) = await PostAsync("conversations.create", new { name, is_private = _opt.PrivateChannels }, ct);
        if (ok) return root.GetProperty("channel").GetProperty("id").GetString();
        if (error == "name_taken") return null;
        if (error is "missing_scope" or "not_allowed_token_type" or "restricted_action")
            throw new HitlRecipientUnreachableException($"Slack refused to create channel #{name} ({error}): the bot needs the channels:manage (and groups:write for private channels) scope.");
        throw new HttpRequestException($"Slack conversations.create failed: {error}");
    }

    /// <summary>
    /// Invites the configured user ids plus the teammates named by e-mail to a newly created channel. Best effort: the
    /// channel already exists and the request can still be posted, so problems are logged, not thrown. Each person is invited
    /// with their own call, so one unknown address does not stop the others.
    /// </summary>
    private async Task InviteAsync(string channelId, IReadOnlyList<string>? emails, CancellationToken ct)
    {
        var userIds = new List<string>(_opt.InviteUserIds);
        foreach (var email in emails ?? Array.Empty<string>())
        {
            var (ok, error, root) = await GetAsync($"users.lookupByEmail?email={Uri.EscapeDataString(email)}", ct);
            if (ok) userIds.Add(root.GetProperty("user").GetProperty("id").GetString()!);
            else _log.LogWarning("Slack user for {Email} not found ({Error}); not invited. Needs the users:read.email scope, and the person must be in the workspace.", Mask.Value(email), error);
        }

        foreach (var userId in userIds.Distinct())
        {
            var (ok, error, _) = await PostAsync("conversations.invite", new { channel = channelId, users = userId }, ct);
            if (!ok && error is not ("already_in_channel" or "cant_invite_self"))
                _log.LogWarning("Inviting {User} to Slack channel {Channel} failed: {Error}", userId, channelId, error);
        }
    }

    /// <summary>Joins a public channel (no-op for private channels: a bot cannot join those itself).</summary>
    public async Task JoinAsync(string channelId, CancellationToken ct)
    {
        if (_opt.PrivateChannels) return;
        var (ok, error, _) = await PostAsync("conversations.join", new { channel = channelId }, ct);
        if (!ok) _log.LogWarning("Joining Slack channel {Channel} failed: {Error} (needs channels:join)", channelId, error);
    }

    private async Task UnarchiveAsync(string channelId, string name, CancellationToken ct)
    {
        var (ok, error, _) = await PostAsync("conversations.unarchive", new { channel = channelId }, ct);
        if (!ok)
            throw new HitlRecipientUnreachableException(
                $"Slack channel #{name} is archived and could not be restored automatically ({error}). Unarchive it, or use a different key.");
    }

    private async Task<(string id, bool archived)?> FindByNameAsync(string name, CancellationToken ct)
    {
        var types = _opt.PrivateChannels ? "public_channel,private_channel" : "public_channel";
        string? cursor = null;
        do
        {
            var url = $"conversations.list?exclude_archived=false&limit=200&types={types}" +
                      (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var (ok, error, root) = await GetAsync(url, ct);
            if (!ok) throw new HttpRequestException($"Slack conversations.list failed: {error}");

            foreach (var ch in root.GetProperty("channels").EnumerateArray())
            {
                if (ch.GetProperty("name").GetString() != name) continue;
                var archived = ch.TryGetProperty("is_archived", out var ia) && ia.GetBoolean();
                return (ch.GetProperty("id").GetString()!, archived);
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) && meta.TryGetProperty("next_cursor", out var c)
                ? c.GetString() : null;
        }
        while (!string.IsNullOrEmpty(cursor));
        return null;
    }

    private async Task<(bool ok, string error, JsonElement root)> PostAsync(string method, object payload, CancellationToken ct)
    {
        using var resp = await HttpHelpers.SendAsync(_http, () => Authorized(
            new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}") { Content = JsonContent.Create(payload) }), ct);
        return await ReadAsync(resp, ct);
    }

    private async Task<(bool ok, string error, JsonElement root)> GetAsync(string methodAndQuery, CancellationToken ct)
    {
        using var resp = await HttpHelpers.SendAsync(_http, () => Authorized(
            new HttpRequestMessage(HttpMethod.Get, $"https://slack.com/api/{methodAndQuery}")), ct);
        return await ReadAsync(resp, ct);
    }

    private HttpRequestMessage Authorized(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.BotToken);
        return req;
    }

    private static async Task<(bool, string, JsonElement)> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement.Clone();
        var ok = root.TryGetProperty("ok", out var o) && o.GetBoolean();
        var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        return (ok, error, root);
    }
}
