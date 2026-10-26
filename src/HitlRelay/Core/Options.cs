namespace HitlRelay.Core;

public sealed class HitlOptions
{
    public const string Section = "Hitl";

    /// <summary>Public HTTPS base URL of this app (used in e-mail links and webhook registration).</summary>
    public string BaseUrl { get; set; } = "https://localhost:5001";
    public string ConnectionString { get; set; } = "Data Source=hitl.db;Default Timeout=30";
    public bool StoreRawPayload { get; set; }
    public int SchedulerIntervalSeconds { get; set; } = 60;
    public int SendRetryBaseDelayMs { get; set; } = 1000;
    public bool EnableDemo { get; set; }
    /// <summary>Protects /api/hitl/* and /demo/*. Required outside Development.</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class SlackOptions
{
    public string BotToken { get; set; } = "";
    public string SigningSecret { get; set; } = "";
}

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public bool AutoRegisterWebhook { get; set; }
}

public sealed class WhatsAppOptions
{
    public string AccessToken { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string VerifyToken { get; set; } = "";
    public string ApiVersion { get; set; } = "v20.0";
}

public sealed class TeamsOptions
{
    // Read by ConfigurationBotFrameworkAuthentication under these exact names.
    public string MicrosoftAppId { get; set; } = "";
    public string MicrosoftAppPassword { get; set; } = "";
    public string MicrosoftAppType { get; set; } = "SingleTenant";
    public string MicrosoftAppTenantId { get; set; } = "";
}

public sealed class EmailOptions
{
    /// <summary>"Smtp" or "Graph".</summary>
    public string Provider { get; set; } = "Smtp";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "HITL Relay";
    public SmtpOptions Smtp { get; set; } = new();
    public GraphOptions Graph { get; set; } = new();
    /// <summary>HMAC key for e-mail link tokens (at least 32 random characters).</summary>
    public string LinkSigningKey { get; set; } = "";
    /// <summary>Optional reply-by-email address pattern, e.g. hitl+{token}@example.com.</summary>
    public string InboundAddressPattern { get; set; } = "";
    /// <summary>Shared secret expected in X-Hitl-Inbound-Secret on /hitl/email/inbound.</summary>
    public string InboundSecret { get; set; } = "";
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Mailbox (user principal name) that sends the e-mail; needs Mail.Send application permission.</summary>
    public string SenderUser { get; set; } = "";
}
