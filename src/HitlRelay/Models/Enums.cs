namespace HitlRelay.Models;

public enum HitlResponseMode { Keyword, OpenEnded }

public enum HitlRequestStatus { Pending, Answered, TimedOut, Cancelled }

public enum HitlTimeoutAction { Fail, ContinueWithDefault, Escalate, Reminder }

public enum HitlOutcomeStatus { Answered, TimedOut, Cancelled }

public enum WorkflowRunStatus { Running, Waiting, Resumed, ResumeFailed, Failed, Completed }

public static class ChannelKeys
{
    public const string Teams = "teams";
    public const string Slack = "slack";
    public const string WhatsApp = "whatsapp";
    public const string Telegram = "telegram";
    public const string Email = "email";

    public static readonly string[] All = { Teams, Slack, WhatsApp, Telegram, Email };
}
