using System.Net;
using System.Text;
using HitlRelay.Channels.Slack;
using HitlRelay.Channels.Teams;
using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HitlRelay.Tests;

/// <summary>Scripted Slack Web API: queues a JSON reply per method and records the calls made.</summary>
public sealed class StubSlackHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<string>> _replies = new();
    public List<string> Calls { get; } = new();

    public StubSlackHandler Reply(string method, string json)
    {
        if (!_replies.TryGetValue(method, out var q)) _replies[method] = q = new Queue<string>();
        q.Enqueue(json);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.AbsolutePath.TrimStart('/').Replace("api/", "");
        Calls.Add(method);
        var json = _replies.TryGetValue(method, out var q) && q.Count > 0 ? q.Dequeue() : "{\"ok\":false,\"error\":\"unscripted\"}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

public class SlackProvisioningTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly StubSlackHandler _slack = new();

    public void Dispose() => _h.Dispose();

    private async Task<SendResult> SendAsync(string recipient, string? key = null)
    {
        using var scope = _h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HitlRelay.Data.HitlDbContext>();
        var http = new HttpClient(_slack);
        var opts = Options.Create(new SlackOptions { BotToken = "x", ChannelNamePrefix = "order-" });
        var store = new ChannelConnectionStore(db, new EphemeralDataProtectionProvider());
        var prov = new SlackChannelProvisioner(http, opts, store, NullLogger<SlackChannelProvisioner>.Instance);
        var adapter = new SlackAdapter(http, opts, _h.Clock, prov);

        var cfg = new HitlNodeConfig { Channel = "slack", Recipient = recipient, SlackChannelKey = key };
        var req = new HitlRequest
        {
            Prompt = "Approve?",
            Recipient = recipient,
            Mode = HitlResponseMode.Keyword,
            KeywordsJson = HitlJson.Serialize(new[] { new KeywordDef { Key = "approve", Label = "Approve" } }),
            CorrelationToken = "tok",
            ConfigJson = HitlJson.Serialize(cfg),
        };
        return await adapter.SendAsync(req, CancellationToken.None);
    }

    private const string Ok = "{\"ok\":true,\"ts\":\"1.1\",\"channel\":\"C111\"}";
    private static string Created(string id) => $"{{\"ok\":true,\"channel\":{{\"id\":\"{id}\"}}}}";
    private const string NotFound = "{\"ok\":false,\"error\":\"channel_not_found\"}";

    [Fact]
    public async Task ChannelName_NotFound_CreatesTheChannelInsteadOfFailing()
    {
        _slack.Reply("conversations.create", Created("C111")).Reply("chat.postMessage", Ok);

        var result = await SendAsync("#order-77");

        Assert.Equal(new[] { "conversations.create", "chat.postMessage" }, _slack.Calls);
        Assert.Equal("C111", result.ExternalConversationId);
    }

    [Fact]
    public async Task ChannelKey_CreatesTheChannel_AndReusesItForTheNextRequest()
    {
        _slack.Reply("conversations.create", Created("C111")).Reply("chat.postMessage", Ok).Reply("chat.postMessage", Ok);

        await SendAsync("", key: "A-7");
        await SendAsync("", key: "A-7");

        Assert.Equal(1, _slack.Calls.Count(c => c == "conversations.create")); // created once, cached afterwards
        Assert.Equal(2, _slack.Calls.Count(c => c == "chat.postMessage"));      // two requests, two posts
    }

    [Fact]
    public async Task NameTaken_ReusesTheExistingChannel()
    {
        _slack.Reply("conversations.create", "{\"ok\":false,\"error\":\"name_taken\"}")
              .Reply("conversations.list", "{\"ok\":true,\"channels\":[{\"id\":\"C999\",\"name\":\"order-77\",\"is_archived\":false}]}")
              .Reply("conversations.join", "{\"ok\":true}")
              .Reply("chat.postMessage", "{\"ok\":true,\"ts\":\"1.1\",\"channel\":\"C999\"}");

        var result = await SendAsync("#order-77");

        Assert.Equal("C999", result.ExternalConversationId);
        Assert.DoesNotContain("conversations.unarchive", _slack.Calls);
    }

    [Fact]
    public async Task StaleCachedChannel_IsRecreatedAndTheMessageRetried()
    {
        _slack.Reply("conversations.create", Created("C111")).Reply("chat.postMessage", Ok);
        await SendAsync("", key: "A-7");                                        // creates C111 and caches it

        // The channel was deleted in Slack: the cached id now fails with channel_not_found.
        _slack.Reply("chat.postMessage", NotFound)
              .Reply("conversations.create", Created("C222"))
              .Reply("chat.postMessage", "{\"ok\":true,\"ts\":\"2.2\",\"channel\":\"C222\"}");

        var result = await SendAsync("", key: "A-7");

        Assert.Equal("C222", result.ExternalConversationId);
        Assert.Equal(2, _slack.Calls.Count(c => c == "conversations.create"));
    }

    [Fact]
    public async Task RawSlackId_ThatDoesNotExist_ReportsUnreachableWithAHint()
    {
        _slack.Reply("chat.postMessage", NotFound);

        var ex = await Assert.ThrowsAsync<HitlRecipientUnreachableException>(() => SendAsync("C0123456789"));

        Assert.Contains("#approvals", ex.Message);
        Assert.DoesNotContain("conversations.create", _slack.Calls); // an id cannot be created
    }
}
