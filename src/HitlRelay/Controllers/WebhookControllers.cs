using System.Text.Json;
using HitlRelay.Channels.Slack;
using HitlRelay.Channels.Teams;
using HitlRelay.Channels.Telegram;
using HitlRelay.Channels.WhatsApp;
using HitlRelay.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace HitlRelay.Controllers;

/// <summary>
/// Shared webhook pipeline: read raw body, verify signature (401 on failure, no state change), parse,
/// enqueue for background processing and answer 200 immediately (platforms retry slow or failing webhooks).
/// </summary>
public abstract class WebhookControllerBase : ControllerBase
{
    protected abstract ILogger Log { get; }
    protected InboundQueue Queue { get; }

    protected WebhookControllerBase(InboundQueue queue) => Queue = queue;

    // Typed as the interface on purpose: ParseInboundAll is a default interface method.
    protected async Task<IActionResult> ProcessAsync(IHitlChannelAdapter adapter, string raw)
    {
        if (!adapter.VerifyInbound(Request, raw))
        {
            Log.LogWarning("Rejected {Channel} webhook: signature/token verification failed", adapter.ChannelKey);
            return Unauthorized();
        }
        foreach (var reply in adapter.ParseInboundAll(Request, raw))
            Queue.Enqueue(reply);
        return Ok();
    }
}

[ApiController]
public sealed class TelegramController : WebhookControllerBase
{
    private readonly IHitlChannelAdapter _adapter;
    private readonly ILogger<TelegramController> _log;

    public TelegramController(TelegramAdapter adapter, InboundQueue queue, ILogger<TelegramController> log) : base(queue)
    {
        _adapter = adapter;
        _log = log;
    }

    protected override ILogger Log => _log;

    [HttpPost("/telegram/webhook")]
    public async Task<IActionResult> Post() => await ProcessAsync(_adapter, await WebhookBody.ReadAsync(Request));
}

[ApiController]
public sealed class SlackController : WebhookControllerBase
{
    private readonly IHitlChannelAdapter _adapter;
    private readonly ILogger<SlackController> _log;

    public SlackController(SlackAdapter adapter, InboundQueue queue, ILogger<SlackController> log) : base(queue)
    {
        _adapter = adapter;
        _log = log;
    }

    protected override ILogger Log => _log;

    [HttpPost("/slack/events")]
    public async Task<IActionResult> Events()
    {
        var raw = await WebhookBody.ReadAsync(Request);
        if (!_adapter.VerifyInbound(Request, raw)) return Unauthorized(); // also rejects timestamps older than 5 min

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "url_verification")
            return Content(doc.RootElement.GetProperty("challenge").GetString() ?? "", "text/plain");

        return await ProcessAsync(_adapter, raw);
    }

    // Button clicks. Returns 200 straight away (Slack allows 3 seconds); the work happens in the background queue.
    [HttpPost("/slack/interactivity")]
    public async Task<IActionResult> Interactivity() => await ProcessAsync(_adapter, await WebhookBody.ReadAsync(Request));
}

[ApiController]
public sealed class WhatsAppController : WebhookControllerBase
{
    private readonly WhatsAppAdapter _whatsApp;
    private readonly ILogger<WhatsAppController> _log;

    public WhatsAppController(WhatsAppAdapter adapter, InboundQueue queue, ILogger<WhatsAppController> log) : base(queue)
    {
        _whatsApp = adapter;
        _log = log;
    }

    protected override ILogger Log => _log;

    [HttpGet("/whatsapp/webhook")]
    public IActionResult Verify([FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && _whatsApp.VerifyHandshake(token))
            return Content(challenge ?? "", "text/plain");
        _log.LogWarning("WhatsApp verification handshake rejected");
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpPost("/whatsapp/webhook")]
    public async Task<IActionResult> Post() => await ProcessAsync(_whatsApp, await WebhookBody.ReadAsync(Request));
}

[ApiController]
public sealed class TeamsController : ControllerBase
{
    private readonly CloudAdapter _adapter;
    private readonly IBot _bot;

    public TeamsController(CloudAdapter adapter, TeamsHitlBot bot)
    {
        _adapter = adapter;
        _bot = bot;
    }

    // CloudAdapter validates the Bot Framework JWT and writes 401 itself for forged calls.
    [HttpPost("/api/messages")]
    public Task Post(CancellationToken ct) => _adapter.ProcessAsync(Request, Response, _bot, ct);
}
