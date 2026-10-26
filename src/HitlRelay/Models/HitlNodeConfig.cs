using System.Text.Json;
using System.Text.Json.Serialization;

namespace HitlRelay.Models;

/// <summary>Shared JSON settings for everything stored as JSON in the database.</summary>
public static class HitlJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

public sealed class KeywordDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Synonyms { get; set; } = new();
}

public sealed class WhatsAppTemplateConfig
{
    /// <summary>Name of a pre-approved template (required to open a conversation outside the 24-hour window).</summary>
    public string Name { get; set; } = "";
    public string Language { get; set; } = "en_US";
    /// <summary>Body variables {{1}}, {{2}}...; each entry may itself use workflow variables.</summary>
    public List<string> Variables { get; set; } = new();
}

/// <summary>HITL node configuration (requirements section 3.1).</summary>
public sealed class HitlNodeConfig
{
    public string Channel { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string MessageTemplate { get; set; } = "";
    public HitlResponseMode ResponseMode { get; set; } = HitlResponseMode.Keyword;
    public List<KeywordDef> AllowedKeywords { get; set; } = new();
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(24);
    public HitlTimeoutAction OnTimeout { get; set; } = HitlTimeoutAction.Fail;
    public string? DefaultValue { get; set; }
    public string? FallbackChannel { get; set; }
    public string? FallbackRecipient { get; set; }
    public List<string> AllowedResponders { get; set; } = new();

    // Section 3.2 / 3.3 / 3.4 settings.
    public int MaxRetries { get; set; } = 2;
    public TimeSpan? ReminderInterval { get; set; }
    /// <summary>Assumption: OnTimeout=Reminder re-arms the timeout this many times, then behaves as Fail.</summary>
    public int MaxTimeoutReminders { get; set; } = 3;
    public int? MinLength { get; set; }
    public string? ValidationRegex { get; set; }
    public string? ValidationMessage { get; set; }
    public WhatsAppTemplateConfig? WhatsAppTemplate { get; set; }

    /// <summary>
    /// Teams only. When set (it may use workflow variables, e.g. "{{order.id}}"), the request is posted into a channel
    /// named after this key in the fixed team (Channels:Teams:TeamId): one channel per key, created on first use, and
    /// every request becomes its own post (thread) in that channel. Recipient is then not required.
    /// </summary>
    public string? TeamsChannelKey { get; set; }
}
