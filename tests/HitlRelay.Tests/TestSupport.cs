using HitlRelay;
using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HitlRelay.Tests;

public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Mock IWorkflowResumer: records calls and can be told to fail.</summary>
public sealed class MockWorkflowResumer : IWorkflowResumer
{
    public List<(string RunId, string NodeId, IReadOnlyDictionary<string, object?> Outputs)> Resumed { get; } = new();
    public List<(string RunId, string NodeId, string Reason)> Failed { get; } = new();
    public bool ThrowOnResume { get; set; }

    public Task ResumeAsync(string workflowRunId, string nodeId, IReadOnlyDictionary<string, object?> outputs, CancellationToken ct = default)
    {
        if (ThrowOnResume) throw new InvalidOperationException("host engine down");
        lock (Resumed) Resumed.Add((workflowRunId, nodeId, outputs));
        return Task.CompletedTask;
    }

    public Task FailAsync(string workflowRunId, string nodeId, string reason, CancellationToken ct = default)
    {
        lock (Failed) Failed.Add((workflowRunId, nodeId, reason));
        return Task.CompletedTask;
    }
}

/// <summary>Shared recorder so tests can see what every fake adapter sent.</summary>
public sealed class FakeChannelLog
{
    public List<(string Channel, string Recipient, string Prompt)> Sent { get; } = new();
    public List<(string Channel, string Text)> Texts { get; } = new();
    public List<(string Channel, HitlOutcomeStatus Status)> Acks { get; } = new();
    public int FailNextSends { get; set; }
    public bool Unreachable { get; set; }
    private int _counter;
    public int Next() => Interlocked.Increment(ref _counter);
}

public abstract class FakeAdapter : IHitlChannelAdapter
{
    private readonly FakeChannelLog _log;
    protected FakeAdapter(string key, FakeChannelLog log) { ChannelKey = key; _log = log; }

    public string ChannelKey { get; }

    public Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        if (_log.Unreachable) throw new HitlRecipientUnreachableException("not reachable");
        if (_log.FailNextSends > 0) { _log.FailNextSends--; throw new HttpRequestException("boom"); }
        var n = _log.Next();
        lock (_log.Sent) _log.Sent.Add((ChannelKey, request.Recipient, request.Prompt));
        return Task.FromResult(new SendResult($"msg-{n}", null, $"conv-{n}"));
    }

    public bool VerifyInbound(HttpRequest httpRequest, string rawBody) => true;
    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody) => null;

    public Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct)
    {
        lock (_log.Acks) _log.Acks.Add((ChannelKey, outcome.Status));
        return Task.CompletedTask;
    }

    public Task SendTextAsync(HitlRequest request, string text, CancellationToken ct)
    {
        lock (_log.Texts) _log.Texts.Add((ChannelKey, text));
        return Task.CompletedTask;
    }
}

public sealed class FakeSlackAdapter : FakeAdapter { public FakeSlackAdapter(FakeChannelLog l) : base(ChannelKeys.Slack, l) { } }
public sealed class FakeTelegramAdapter : FakeAdapter { public FakeTelegramAdapter(FakeChannelLog l) : base(ChannelKeys.Telegram, l) { } }
public sealed class FakeWhatsAppAdapter : FakeAdapter { public FakeWhatsAppAdapter(FakeChannelLog l) : base(ChannelKeys.WhatsApp, l) { } }

/// <summary>Builds the real core (SQLite file DB) with fake channel adapters. Pass a path to simulate a restart.</summary>
public sealed class Harness : IDisposable
{
    public ServiceProvider Provider { get; }
    public MockWorkflowResumer Resumer { get; } = new();
    public FakeChannelLog Channels { get; } = new();
    public FakeTimeProvider Clock { get; } = new();
    public string DbFile { get; }

    public Harness(string? existingDbFile = null)
    {
        DbFile = existingDbFile ?? Path.Combine(Path.GetTempPath(), $"hitl-test-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hitl:ConnectionString"] = $"Data Source={DbFile};Default Timeout=30",
            ["Hitl:SendRetryBaseDelayMs"] = "0",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHitlCore(config);
        services.AddSingleton<TimeProvider>(Clock); // overrides the default registered by AddHitlCore
        services.AddSingleton(Channels);
        services.AddSingleton<IWorkflowResumer>(Resumer);
        foreach (var (key, type) in new[]
        {
            (ChannelKeys.Slack, typeof(FakeSlackAdapter)),
            (ChannelKeys.Telegram, typeof(FakeTelegramAdapter)),
            (ChannelKeys.WhatsApp, typeof(FakeWhatsAppAdapter)),
        })
        {
            services.AddScoped(type);
            services.AddSingleton(new AdapterRegistration(key, type));
        }
        Provider = services.BuildServiceProvider();

        using var scope = Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<HitlDbContext>().Database.EnsureCreated();
    }

    public static HitlNodeConfig KeywordConfig(string channel = ChannelKeys.Slack) => new()
    {
        Channel = channel,
        Recipient = "U123",
        MessageTemplate = "Approve order {{order.id}}?",
        ResponseMode = HitlResponseMode.Keyword,
        AllowedKeywords = new List<KeywordDef>
        {
            new() { Key = "approve", Label = "Approve", Synonyms = { "yes", "ok" } },
            new() { Key = "reject", Label = "Reject", Synonyms = { "no" } },
        },
    };

    public static HitlNodeConfig OpenConfig(string channel = ChannelKeys.Slack) => new()
    {
        Channel = channel,
        Recipient = "U123",
        MessageTemplate = "What should we write?",
        ResponseMode = HitlResponseMode.OpenEnded,
    };

    public async Task<HitlRequest> CreateAsync(HitlNodeConfig cfg, string runId = "run-1")
    {
        using var scope = Provider.CreateScope();
        var vars = new Dictionary<string, object?> { ["order"] = new Dictionary<string, object?> { ["id"] = "A-7" } };
        return await scope.ServiceProvider.GetRequiredService<HitlService>().CreateAsync(cfg, runId, "node-1", vars);
    }

    public async Task<ReplyResult> ReplyAsync(InboundReply reply)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HitlReplyHandler>().HandleAsync(reply);
    }

    public async Task<T> QueryAsync<T>(Func<HitlDbContext, Task<T>> query)
    {
        using var scope = Provider.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<HitlDbContext>());
    }

    public Task<HitlRequest> LoadAsync(Guid id) => QueryAsync(db => db.Requests.AsNoTracking().FirstAsync(r => r.Id == id));
    public Task<WorkflowRun> LoadRunAsync(string id) => QueryAsync(db => db.Runs.AsNoTracking().FirstAsync(r => r.Id == id));

    public async Task RunSchedulerAsync()
    {
        await Provider.GetRequiredService<HitlScheduler>().RunOnceAsync(CancellationToken.None);
    }

    public static InboundReply Button(HitlRequest r, string key, string msgId = "evt-1", string channel = ChannelKeys.Slack) => new()
    {
        Channel = channel,
        CorrelationToken = r.CorrelationToken,
        ActionValue = key,
        ExternalMessageId = msgId,
        ResponderId = "U123",
        ResponderDisplayName = "Priya",
    };

    public static InboundReply Typed(HitlRequest r, string text, string msgId = "evt-1", string channel = ChannelKeys.Slack) => new()
    {
        Channel = channel,
        ExternalThreadId = r.ExternalMessageId,
        ExternalConversationId = r.ExternalConversationId,
        ExternalMessageId = msgId,
        ResponderId = "U123",
        ResponderDisplayName = "Priya",
        Text = text,
    };

    public void Dispose()
    {
        Provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(DbFile); } catch (IOException) { }
    }
}
