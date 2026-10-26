using System.ComponentModel.DataAnnotations.Schema;

namespace HitlRelay.Models;

/// <summary>One question sent to one person on one channel for one workflow node.</summary>
public class HitlRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkflowRunId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Prompt { get; set; } = "";
    public HitlResponseMode Mode { get; set; }
    public string KeywordsJson { get; set; } = "[]";
    /// <summary>Optimistic concurrency token: only one writer can move Pending to a final state.</summary>
    public HitlRequestStatus Status { get; set; } = HitlRequestStatus.Pending;
    /// <summary>Random 256-bit single-use value (URL-safe base64).</summary>
    public string CorrelationToken { get; set; } = "";
    /// <summary>Short lookup id for channels with tiny payload limits (Telegram callback_data).</summary>
    public string ShortCode { get; set; } = "";
    public string? ExternalMessageId { get; set; }
    public string? ExternalThreadId { get; set; }
    public string? ExternalConversationId { get; set; }
    public int RetryCount { get; set; }
    public int ReminderCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? NextReminderAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    /// <summary>The node configuration this request was created from (timeout rule, validation, etc.).</summary>
    public string ConfigJson { get; set; } = "{}";
    public Guid? EscalatedFromRequestId { get; set; }

    [NotMapped]
    public IReadOnlyList<KeywordDef> Keywords =>
        HitlJson.Deserialize<List<KeywordDef>>(KeywordsJson) ?? new List<KeywordDef>();

    [NotMapped]
    public HitlNodeConfig Config => HitlJson.Deserialize<HitlNodeConfig>(ConfigJson) ?? new HitlNodeConfig();
}

public class HitlResponse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public string ResponderId { get; set; } = "";
    public string? ResponderDisplayName { get; set; }
    public string? Value { get; set; }
    public string? Text { get; set; }
    /// <summary>Only filled when Hitl:StoreRawPayload is true.</summary>
    public string? RawPayloadJson { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public class HitlAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? RequestId { get; set; }
    public string EventType { get; set; } = "";
    public string? DetailJson { get; set; }
    public DateTime At { get; set; }
}

/// <summary>
/// Encrypted per-channel configuration or per-recipient state (for example Teams conversation references).
/// Assumption: one table with a Key column replaces a separate Teams reference table.
/// </summary>
public class HitlChannelConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Channel { get; set; } = "";
    public string Key { get; set; } = "";
    public string ConfigJson { get; set; } = "";   // encrypted with ASP.NET Data Protection
    public bool Enabled { get; set; } = true;
}

/// <summary>Minimal workflow-run row so the module can suspend and resume without a host engine.</summary>
public class WorkflowRun
{
    public string Id { get; set; } = "";
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Running;
    public string ContextJson { get; set; } = "{}";
    public string? OutputsJson { get; set; }
    public string? Log { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Idempotency ledger: webhook deliveries are retried by every platform.</summary>
public class ProcessedInbound
{
    public string Channel { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public DateTime At { get; set; }
}
