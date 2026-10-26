using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;
using HitlRelay.Sample;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HitlRelay.Controllers;

public abstract class KeyProtectedController : ControllerBase
{
    private readonly HitlOptions _options;
    private readonly IHostEnvironment _env;

    protected KeyProtectedController(IOptions<HitlOptions> options, IHostEnvironment env)
    {
        _options = options.Value;
        _env = env;
    }

    /// <summary>Hitl:ApiKey must match X-Api-Key; with no key configured only Development is open.</summary>
    protected bool IsAuthorized()
    {
        if (string.IsNullOrEmpty(_options.ApiKey)) return _env.IsDevelopment();
        return Crypto.FixedEquals(Request.Headers["X-Api-Key"].ToString(), _options.ApiKey);
    }
}

[ApiController]
public sealed class HitlRequestsController : KeyProtectedController
{
    private readonly HitlDbContext _db;
    private readonly HitlService _service;
    private readonly HitlCompletion _completion;

    public HitlRequestsController(HitlDbContext db, HitlService service, HitlCompletion completion,
        IOptions<HitlOptions> options, IHostEnvironment env) : base(options, env)
    {
        _db = db;
        _service = service;
        _completion = completion;
    }

    [HttpGet("/api/hitl/requests/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        var r = await _db.Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r == null) return NotFound();

        var response = await _db.Responses.AsNoTracking().Where(x => x.RequestId == id)
            .OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(ct);
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.WorkflowRunId, ct);
        var audit = await _db.AuditEvents.AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.At)
            .Select(x => new { x.EventType, x.At, x.DetailJson }).ToListAsync(ct);

        return Ok(new
        {
            r.Id, r.WorkflowRunId, r.NodeId, r.Channel, r.Prompt, r.Recipient, keywords = r.Keywords, mode = r.Mode.ToString(), status = r.Status.ToString(),
            r.RetryCount, r.CreatedAt, r.ExpiresAt, r.AnsweredAt,
            runStatus = run?.Status.ToString(),
            response = response == null ? null : new { response.Value, response.Text, response.ResponderDisplayName, response.ReceivedAt },
            audit,
        });
    }

    [HttpPost("/api/hitl/requests/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        return await _service.CancelAsync(id, ct) ? Ok(new { status = "Cancelled" }) : Conflict(new { error = "Request not found or not pending." });
    }

    /// <summary>Retries the resume of a run stuck in ResumeFailed (the person is not asked again).</summary>
    [HttpPost("/api/hitl/runs/{runId}/retry-resume")]
    public async Task<IActionResult> RetryResume(string runId, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        return await _completion.RetryResumeAsync(runId, ct) ? Ok() : Conflict(new { error = "Run is not in ResumeFailed." });
    }
}

public sealed record DemoStartRequest(string Channel, string Recipient, string Mode = "keyword", string? OrderId = null,
    string? WhatsAppTemplate = null, string? FallbackChannel = null, string? FallbackRecipient = null,
    int? TimeoutMinutes = null, string? OnTimeout = null, string? DefaultValue = null,
    string? AllowedResponders = null, int? MaxRetries = null);

public sealed record SimulateReplyRequest(Guid RequestId, string? Key = null, string? Text = null, string? Responder = null);

/// <summary>Sample workflow: Start, HITL, then a branch on response.value or an echo of response.text.</summary>
[ApiController]
public sealed class DemoController : KeyProtectedController
{
    private readonly SampleWorkflowEngine _engine;
    private readonly HitlDbContext _db;
    private readonly HitlReplyHandler _handler;
    private readonly HitlOptions _options;

    public DemoController(SampleWorkflowEngine engine, HitlDbContext db, HitlReplyHandler handler,
        IOptions<HitlOptions> options, IHostEnvironment env) : base(options, env)
    {
        _engine = engine;
        _db = db;
        _handler = handler;
        _options = options.Value;
    }

    /// <summary>Latest requests for the test console.</summary>
    [HttpGet("/demo/requests")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!_options.EnableDemo) return NotFound();
        if (!IsAuthorized()) return Unauthorized();
        var rows = await _db.Requests.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(50)
            .Select(r => new { r.Id, r.WorkflowRunId, r.Channel, r.Recipient, status = r.Status.ToString(), r.CreatedAt, r.ExpiresAt, r.RetryCount, r.Prompt })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>
    /// Injects a reply as if the channel had delivered it, going through the same handler (correlation, validation,
    /// allowed responders, resume). Lets you test without vendor accounts. A key simulates a button click; text
    /// simulates a typed reply correlated by thread/conversation.
    /// </summary>
    [HttpPost("/demo/simulate-reply")]
    public async Task<IActionResult> SimulateReply([FromBody] SimulateReplyRequest body, CancellationToken ct)
    {
        if (!_options.EnableDemo) return NotFound();
        if (!IsAuthorized()) return Unauthorized();
        var req = await _db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == body.RequestId, ct);
        if (req == null) return NotFound();

        var responder = string.IsNullOrWhiteSpace(body.Responder) ? req.Recipient : body.Responder.Trim();
        var reply = new InboundReply
        {
            Channel = req.Channel,
            CorrelationToken = string.IsNullOrEmpty(body.Key) ? null : req.CorrelationToken,
            ActionValue = string.IsNullOrEmpty(body.Key) ? null : body.Key,
            Text = body.Text,
            ExternalThreadId = req.ExternalMessageId,
            ExternalConversationId = req.ExternalConversationId,
            ExternalMessageId = "sim:" + Guid.NewGuid().ToString("N"),
            ResponderId = responder,
            ResponderEmail = responder.Contains('@') ? responder : null,
            ResponderDisplayName = responder,
            ReceivedAt = DateTime.UtcNow,
        };
        var result = await _handler.HandleAsync(reply, ct);
        return Ok(new { status = result.Status.ToString(), message = result.Message });
    }

    [HttpPost("/demo/start")]
    public async Task<IActionResult> Start([FromBody] DemoStartRequest body, CancellationToken ct)
    {
        if (!_options.EnableDemo) return NotFound();
        if (!IsAuthorized()) return Unauthorized();
        try
        {
            var (runId, requestId) = await _engine.StartAsync(body, ct);
            return Ok(new { runId, requestId });
        }
        catch (HitlSendException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpGet("/demo/runs/{runId}")]
    public async Task<IActionResult> Run(string runId, CancellationToken ct)
    {
        if (!_options.EnableDemo) return NotFound();
        if (!IsAuthorized()) return Unauthorized();
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return run == null ? NotFound() : Ok(new { run.Id, status = run.Status.ToString(), run.Log, run.OutputsJson });
    }
}
