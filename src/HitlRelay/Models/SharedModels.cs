namespace HitlRelay.Models;

public sealed record SendResult(string? ExternalMessageId, string? ExternalThreadId, string? ExternalConversationId);

/// <summary>Channel-neutral view of something a person sent back.</summary>
public sealed record InboundReply
{
    public string Channel { get; init; } = "";
    /// <summary>Full correlation token (button payload, link, plus-address), when the channel carries it.</summary>
    public string? CorrelationToken { get; init; }
    /// <summary>Short lookup id (Telegram callback_data), resolved against HitlRequest.ShortCode.</summary>
    public string? CorrelationShortCode { get; init; }
    /// <summary>Unique id of this inbound event/message; used for idempotency.</summary>
    public string? ExternalMessageId { get; init; }
    /// <summary>The message being replied to / thread root (Slack thread_ts, Telegram reply_to, WhatsApp context.id, Email In-Reply-To).</summary>
    public string? ExternalThreadId { get; init; }
    public string? ExternalConversationId { get; init; }
    public string ResponderId { get; init; } = "";
    public string? ResponderDisplayName { get; init; }
    public string? ResponderEmail { get; init; }
    /// <summary>Handle such as a Telegram username; checked against Allowed responders too.</summary>
    public string? ResponderAlias { get; init; }
    public string? Text { get; init; }
    /// <summary>Button key, when the channel delivers the keyword key directly.</summary>
    public string? ActionValue { get; init; }
    /// <summary>Button position, when the channel can only carry an index (Telegram callback_data).</summary>
    public int? ActionIndex { get; init; }
    /// <summary>Channel-specific handle needed to acknowledge the click (Telegram callback_query id).</summary>
    public string? AckToken { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    public string? RawPayload { get; init; }
}

public sealed record HitlOutcome(
    HitlOutcomeStatus Status,
    string? Value,
    string? Text,
    string? ResponderDisplayName,
    DateTime At);
