using System.Threading.Channels;
using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HitlRelay.Core;

/// <summary>In-process queue so webhook endpoints can answer within 3 seconds and do the work afterwards.</summary>
public sealed class InboundQueue
{
    private readonly Channel<InboundReply> _channel = Channel.CreateUnbounded<InboundReply>();

    public void Enqueue(InboundReply reply) => _channel.Writer.TryWrite(reply);

    public IAsyncEnumerable<InboundReply> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class InboundProcessor : BackgroundService
{
    private readonly InboundQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InboundProcessor> _log;

    public InboundProcessor(InboundQueue queue, IServiceScopeFactory scopes, ILogger<InboundProcessor> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reply in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<HitlReplyHandler>();
                await handler.HandleAsync(reply, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Processing inbound {Channel} reply failed", reply.Channel);
            }
        }
    }
}

/// <summary>Checks open requests every minute: reminders first, then timeout rules at expiry.</summary>
public sealed class HitlScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly HitlOptions _options;
    private readonly ILogger<HitlScheduler> _log;

    public HitlScheduler(IServiceScopeFactory scopes, TimeProvider clock, IOptions<HitlOptions> options, ILogger<HitlScheduler> log)
    {
        _scopes = scopes;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.SchedulerIntervalSeconds)));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Scheduler tick failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One scheduler pass; public so it can be driven directly by tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        List<Guid> expired, reminders;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HitlDbContext>();
            expired = await db.Requests.Where(r => r.Status == HitlRequestStatus.Pending && r.ExpiresAt <= now)
                .Select(r => r.Id).ToListAsync(ct);
            reminders = await db.Requests.Where(r => r.Status == HitlRequestStatus.Pending && r.ExpiresAt > now
                    && r.NextReminderAt != null && r.NextReminderAt <= now)
                .Select(r => r.Id).ToListAsync(ct);
        }

        foreach (var id in reminders)
            await RunItemAsync(id, (svc, i) => svc.SendReminderAsync(i, ct), "reminder");
        foreach (var id in expired)
            await RunItemAsync(id, (svc, i) => svc.ApplyTimeoutRuleAsync(i, "timeout", ct), "timeout");
    }

    // One scope per request, so a failure on one request cannot poison the others.
    private async Task RunItemAsync(Guid id, Func<HitlService, Guid, Task> action, string what)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<HitlService>(), id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scheduler {What} failed for request {RequestId}", what, id);
        }
    }
}

public static class WebhookBody
{
    public static async Task<string> ReadAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }
}
