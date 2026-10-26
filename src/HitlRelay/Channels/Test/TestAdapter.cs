using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;

namespace HitlRelay.Channels.Test;

/// <summary>
/// Loopback channel for the test console. "Sending" just records what the person would have seen as audit events
/// (visible in the console); replies are injected with POST /demo/simulate-reply.
/// </summary>
public sealed class TestAdapter : IHitlChannelAdapter
{
    public const string Key = "test";

    private readonly HitlDbContext _db;
    private readonly AuditService _audit;

    public TestAdapter(HitlDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public string ChannelKey => Key;

    public Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        var id = "test-" + Guid.NewGuid().ToString("N")[..8];
        return Task.FromResult(new SendResult(id, null, "test-" + request.Recipient));
    }

    public bool VerifyInbound(HttpRequest httpRequest, string rawBody) => false; // no real webhook
    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody) => null;

    public Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct) =>
        Record(request, OutcomeFormatter.Format(outcome), ct);

    public Task SendTextAsync(HitlRequest request, string text, CancellationToken ct) => Record(request, text, ct);

    private async Task Record(HitlRequest request, string text, CancellationToken ct) =>
        await _audit.WriteAsync(request.Id, "test_outbound", new { text }, ct);
}
