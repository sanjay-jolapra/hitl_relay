using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HitlRelay.Channels.Teams;

/// <summary>
/// Encrypted storage for per-recipient state in HitlChannelConnection (ConfigJson is encrypted with ASP.NET
/// Data Protection). Persist the key ring in production, or stored references become unreadable after a restart.
/// </summary>
public sealed class ChannelConnectionStore
{
    private readonly HitlDbContext _db;
    private readonly IDataProtector _protector;

    public ChannelConnectionStore(HitlDbContext db, IDataProtectionProvider provider)
    {
        _db = db;
        _protector = provider.CreateProtector("HitlRelay.ChannelConnection.v1");
    }

    public async Task SaveAsync(string channel, string key, string json, CancellationToken ct = default)
    {
        var row = await _db.ChannelConnections.FirstOrDefaultAsync(c => c.Channel == channel && c.Key == key, ct);
        if (row == null)
        {
            row = new HitlChannelConnection { Channel = channel, Key = key };
            _db.ChannelConnections.Add(row);
        }
        row.ConfigJson = _protector.Protect(json);
        row.Enabled = true;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two activities for the same user can race on the unique (Channel, Key) index; the winner's row is fine.
            _db.Entry(row).State = EntityState.Detached;
        }
    }

    public async Task<string?> GetAsync(string channel, string key, CancellationToken ct = default)
    {
        var row = await _db.ChannelConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Channel == channel && c.Key == key && c.Enabled, ct);
        return row == null ? null : _protector.Unprotect(row.ConfigJson);
    }
}

/// <summary>
/// Microsoft Teams through a Bot Framework bot. The recipient is the user's AAD object id or Teams user id; the bot
/// must have been installed for that user (or the user must have messaged it) so a ConversationReference exists.
/// Verification: CloudAdapter validates the Bot Framework JWT on /api/messages before any of our code runs and
/// answers 401 itself, so VerifyInbound below only guards the body shape.
/// </summary>
public sealed class TeamsAdapter : IHitlChannelAdapter
{
    private const string Channel = ChannelKeys.Teams;
    private const string AdaptiveCardType = "application/vnd.microsoft.card.adaptive";

    private readonly CloudAdapter _bot;
    private readonly ChannelConnectionStore _store;
    private readonly TeamsOptions _opt;

    public TeamsAdapter(CloudAdapter bot, ChannelConnectionStore store, IOptions<TeamsOptions> opt)
    {
        _bot = bot;
        _store = store;
        _opt = opt.Value;
    }

    public string ChannelKey => Channel;

    public async Task SaveReferenceAsync(Activity activity, CancellationToken ct)
    {
        var json = JsonConvert.SerializeObject(activity.GetConversationReference());
        // Index under both ids so the node's Recipient can be either.
        if (!string.IsNullOrEmpty(activity.From?.Id)) await _store.SaveAsync(Channel, activity.From.Id, json, ct);
        if (!string.IsNullOrEmpty(activity.From?.AadObjectId)) await _store.SaveAsync(Channel, activity.From.AadObjectId, json, ct);
    }

    public async Task<SendResult> SendAsync(HitlRequest request, CancellationToken ct)
    {
        var reference = await LoadReferenceAsync(request.Recipient, ct)
            ?? throw new HitlRecipientUnreachableException(
                "No Teams conversation reference for this recipient: the bot must be installed for (or messaged by) the user first.");

        string? activityId = null, conversationId = null;
        await _bot.ContinueConversationAsync(_opt.MicrosoftAppId, reference, async (turn, token) =>
        {
            var response = await turn.SendActivityAsync(MessageFactory.Attachment(Card(request, null)), token);
            activityId = response.Id;
            conversationId = turn.Activity.Conversation.Id;
        }, ct);

        return new SendResult(activityId, null, conversationId);
    }

    public bool VerifyInbound(HttpRequest httpRequest, string rawBody) => !string.IsNullOrWhiteSpace(rawBody);

    public InboundReply? ParseInbound(HttpRequest httpRequest, string rawBody)
    {
        var activity = JsonConvert.DeserializeObject<Activity>(rawBody);
        return activity == null ? null : ParseActivity(activity);
    }

    /// <summary>
    /// Card button/submit: Activity.Value carries hitlToken + key (read directly). Plain text: correlated by
    /// conversation id and reply-to id (rules 2 and 3).
    /// </summary>
    public InboundReply? ParseActivity(Activity activity)
    {
        if (activity.Type != ActivityTypes.Message) return null;

        string? token = null, key = null, text = null;
        if (activity.Value is JObject value)
        {
            token = value.Value<string>("hitlToken");
            key = value.Value<string>("key");
            text = value.Value<string>("hitlText");
        }
        text ??= string.IsNullOrWhiteSpace(activity.Text) ? null : activity.RemoveRecipientMention()?.Trim();
        if (token == null && string.IsNullOrEmpty(text)) return null;

        return new InboundReply
        {
            Channel = Channel,
            CorrelationToken = token,
            ActionValue = key,
            ExternalMessageId = activity.Id,
            ExternalThreadId = activity.ReplyToId,
            ExternalConversationId = activity.Conversation?.Id,
            ResponderId = activity.From?.AadObjectId ?? activity.From?.Id ?? "",
            ResponderDisplayName = activity.From?.Name,
            ResponderAlias = activity.From?.Id,
            Text = text,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    public async Task AcknowledgeAsync(HitlRequest request, HitlOutcome outcome, CancellationToken ct)
    {
        var reference = await LoadReferenceAsync(request.Recipient, ct);
        if (reference == null || request.ExternalMessageId == null) return;

        await _bot.ContinueConversationAsync(_opt.MicrosoftAppId, reference, async (turn, token) =>
        {
            // UpdateActivityAsync with a card that has no actions removes the buttons and shows the outcome.
            var updated = MessageFactory.Attachment(Card(request, OutcomeFormatter.Format(outcome)));
            updated.Id = request.ExternalMessageId;
            await turn.UpdateActivityAsync(updated, token);
        }, ct);
    }

    public async Task SendTextAsync(HitlRequest request, string text, CancellationToken ct)
    {
        var reference = await LoadReferenceAsync(request.Recipient, ct);
        if (reference == null) return;
        await _bot.ContinueConversationAsync(_opt.MicrosoftAppId, reference,
            async (turn, token) => await turn.SendActivityAsync(MessageFactory.Text(text), token), ct);
    }

    private async Task<ConversationReference?> LoadReferenceAsync(string recipient, CancellationToken ct)
    {
        var json = await _store.GetAsync(Channel, recipient, ct);
        return json == null ? null : JsonConvert.DeserializeObject<ConversationReference>(json);
    }

    /// <summary>Adaptive Card JSON (see README for a rendered example).</summary>
    private static Attachment Card(HitlRequest request, string? outcomeText)
    {
        var body = new List<object> { new Dictionary<string, object> { ["type"] = "TextBlock", ["text"] = request.Prompt, ["wrap"] = true } };
        var actions = new List<object>();

        if (outcomeText != null)
        {
            body.Add(new Dictionary<string, object> { ["type"] = "TextBlock", ["text"] = outcomeText, ["wrap"] = true, ["weight"] = "Bolder" });
        }
        else if (request.Mode == HitlResponseMode.Keyword)
        {
            foreach (var k in request.Keywords)
            {
                actions.Add(new Dictionary<string, object>
                {
                    ["type"] = "Action.Submit",
                    ["title"] = k.Label,
                    ["data"] = new Dictionary<string, object> { ["hitlToken"] = request.CorrelationToken, ["key"] = k.Key },
                });
            }
        }
        else
        {
            body.Add(new Dictionary<string, object> { ["type"] = "Input.Text", ["id"] = "hitlText", ["isMultiline"] = true, ["placeholder"] = "Type your reply" });
            actions.Add(new Dictionary<string, object>
            {
                ["type"] = "Action.Submit",
                ["title"] = "Send",
                ["data"] = new Dictionary<string, object> { ["hitlToken"] = request.CorrelationToken },
            });
        }

        var card = new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["body"] = body,
            ["actions"] = actions,
        };
        return new Attachment { ContentType = AdaptiveCardType, Content = JObject.FromObject(card) };
    }
}

/// <summary>The Bot Framework bot behind /api/messages. Does no real work inline: it queues the reply.</summary>
public sealed class TeamsHitlBot : ActivityHandler
{
    private readonly TeamsAdapter _teams;
    private readonly InboundQueue _queue;

    public TeamsHitlBot(TeamsAdapter teams, InboundQueue queue)
    {
        _teams = teams;
        _queue = queue;
    }

    // ITurnContext<T>.Activity is the narrow interface type; the non-generic ITurnContext.Activity is the Activity class.
    private static Activity ActivityOf(ITurnContext turnContext) => turnContext.Activity;

    protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken ct)
    {
        var activity = ActivityOf(turnContext);
        await _teams.SaveReferenceAsync(activity, ct); // keeps proactive messaging working
        var reply = _teams.ParseActivity(activity);
        if (reply != null) _queue.Enqueue(reply);
    }

    protected override Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken ct) =>
        _teams.SaveReferenceAsync(ActivityOf(turnContext), ct);

    protected override Task OnInstallationUpdateAddAsync(ITurnContext<IInstallationUpdateActivity> turnContext, CancellationToken ct) =>
        _teams.SaveReferenceAsync(ActivityOf(turnContext), ct);
}
