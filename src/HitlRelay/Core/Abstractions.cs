using HitlRelay.Models;
using Microsoft.AspNetCore.Http;

namespace HitlRelay.Core;

/// <summary>Every channel implements this one interface (requirements section 4).</summary>
public interface IHitlChannelAdapter
{
    string ChannelKey { get; }

    /// <summary>Sends the request; returns the channel's own message/thread reference.</summary>
    Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct);

    /// <summary>Verifies the signature or token of an inbound HTTP call.</summary>
    bool VerifyInbound(HttpRequest httpRequest, string rawBody);

    /// <summary>Turns the native payload into an InboundReply; null for non-reply events.</summary>
    InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody);

    /// <summary>Edits or follows up on the original message after resolution.</summary>
    Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct);

    // ---- Assumed additions to the section 4 interface (default implementations keep adapters small) ----

    /// <summary>Sends plain text to the request's recipient/thread: re-prompts, reminders, "already answered".</summary>
    Task SendTextAsync(HitlRequest request, string text, CancellationToken ct);

    /// <summary>Payloads that can carry several replies (WhatsApp batches). Defaults to ParseInbound.</summary>
    IEnumerable<InboundReply> ParseInboundAll(HttpRequest httpRequest, string rawBody)
    {
        var reply = ParseInbound(httpRequest, rawBody);
        if (reply != null) yield return reply;
    }

    /// <summary>Immediately acknowledges the inbound event (for example Telegram answerCallbackQuery).</summary>
    Task AcknowledgeInboundAsync(InboundReply reply, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The one method the host application implements to continue a run (plus FailAsync for the error branch).</summary>
public interface IWorkflowResumer
{
    /// <summary>Continue from the node after <paramref name="nodeId"/>, with the response as its output.</summary>
    Task ResumeAsync(string workflowRunId, string nodeId, IReadOnlyDictionary<string, object?> outputs, CancellationToken ct = default);

    /// <summary>Assumption: a timeout with rule Fail (or a failed send) needs the host to take the error branch.</summary>
    Task FailAsync(string workflowRunId, string nodeId, string reason, CancellationToken ct = default);
}

/// <summary>Thrown when the recipient cannot be reached (bot not started, number not opted in); never retried.</summary>
public sealed class HitlRecipientUnreachableException : Exception
{
    public HitlRecipientUnreachableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class HitlSendException : Exception
{
    public HitlSendException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Maps a channel key to the adapter type that serves it.</summary>
public sealed record AdapterRegistration(string ChannelKey, Type AdapterType);

/// <summary>
/// Resolves adapters lazily, per channel, so a misconfigured channel (for example Teams without credentials)
/// cannot break the other four.
/// </summary>
public sealed class AdapterRegistry
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Type> _types;
    private readonly Dictionary<string, IHitlChannelAdapter> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public AdapterRegistry(IServiceProvider services, IEnumerable<AdapterRegistration> registrations)
    {
        _services = services;
        _types = registrations.ToDictionary(r => r.ChannelKey, r => r.AdapterType, StringComparer.OrdinalIgnoreCase);
    }

    public IHitlChannelAdapter Get(string channel)
    {
        if (_resolved.TryGetValue(channel, out var existing)) return existing;
        if (!_types.TryGetValue(channel, out var type))
            throw new InvalidOperationException($"No HITL adapter registered for channel '{channel}'.");
        var adapter = (IHitlChannelAdapter)_services.GetRequiredService(type);
        _resolved[channel] = adapter;
        return adapter;
    }
}
