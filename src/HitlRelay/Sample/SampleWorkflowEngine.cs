using HitlRelay.Controllers;
using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;

namespace HitlRelay.Sample;

/// <summary>
/// A tiny stand-in for the host workflow engine, and the real IWorkflowResumer for this sample app:
/// Start node -> HITL node -> (keyword) branch on response.value, or (open-ended) echo response.text.
/// A host application would replace this class with its own IWorkflowResumer.
/// </summary>
public sealed class SampleWorkflowEngine : IWorkflowResumer
{
    private const string HitlNodeId = "hitl-1";

    private readonly HitlDbContext _db;
    private readonly HitlService _hitl;
    private readonly TimeProvider _clock;

    public SampleWorkflowEngine(HitlDbContext db, HitlService hitl, TimeProvider clock)
    {
        _db = db;
        _hitl = hitl;
        _clock = clock;
    }

    public async Task<(string runId, Guid requestId)> StartAsync(DemoStartRequest start, CancellationToken ct)
    {
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        var open = start.Mode.Equals("open", StringComparison.OrdinalIgnoreCase);

        // Node 1: Start. Seeds the workflow variables.
        var variables = new Dictionary<string, object?>
        {
            ["order"] = new Dictionary<string, object?> { ["id"] = start.OrderId ?? "A-1001", ["amount"] = "1,250.00 USD" },
            ["demoMode"] = open ? "open" : "keyword",
        };

        // Node 2: HITL.
        var cfg = new HitlNodeConfig
        {
            Channel = start.Channel,
            Recipient = start.Recipient,
            MessageTemplate = open
                ? "Order {{order.id}} needs a note. What should we write on the shipping label?"
                : "Please approve order {{order.id}} for {{order.amount}}.",
            ResponseMode = open ? HitlResponseMode.OpenEnded : HitlResponseMode.Keyword,
            Timeout = TimeSpan.FromHours(24),
            OnTimeout = string.IsNullOrEmpty(start.FallbackChannel) ? HitlTimeoutAction.Fail : HitlTimeoutAction.Escalate,
            FallbackChannel = start.FallbackChannel,
            FallbackRecipient = start.FallbackRecipient,
            ReminderInterval = TimeSpan.FromHours(8),
            AllowedKeywords = open ? new List<KeywordDef>() : new List<KeywordDef>
            {
                new KeywordDef { Key = "approve", Label = "Approve", Synonyms = { "yes", "ok", "approved" } },
                new KeywordDef { Key = "reject", Label = "Reject", Synonyms = { "no", "deny", "rejected" } },
            },
            WhatsAppTemplate = string.IsNullOrEmpty(start.WhatsAppTemplate) ? null : new WhatsAppTemplateConfig
            {
                Name = start.WhatsAppTemplate,
                Variables = { "{{order.id}}", "{{order.amount}}" },
            },
        };

        // Optional test-console overrides.
        if (start.TimeoutMinutes is int tm) cfg.Timeout = TimeSpan.FromMinutes(tm);
        if (Enum.TryParse<HitlTimeoutAction>(start.OnTimeout, true, out var action)) cfg.OnTimeout = action;
        if (!string.IsNullOrEmpty(start.DefaultValue)) cfg.DefaultValue = start.DefaultValue;
        if (start.MaxRetries is int mr) cfg.MaxRetries = mr;
        if (!string.IsNullOrWhiteSpace(start.AllowedResponders))
            cfg.AllowedResponders = start.AllowedResponders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        await AppendLogAsync(runId, "Start node executed", ct);
        var request = await _hitl.CreateAsync(cfg, runId, HitlNodeId, variables, ct); // run is now Waiting, no thread is held
        await AppendLogAsync(runId, $"HITL node sent request {request.Id} on {cfg.Channel}; run suspended", ct);
        return (runId, request.Id);
    }

    public async Task ResumeAsync(string workflowRunId, string nodeId, IReadOnlyDictionary<string, object?> outputs, CancellationToken ct = default)
    {
        var run = await _db.Runs.FindAsync(new object[] { workflowRunId }, ct)
            ?? throw new InvalidOperationException($"Unknown run {workflowRunId}");
        var vars = HitlJson.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(run.ContextJson);
        var keywordMode = vars == null || !vars.TryGetValue("demoMode", out var m) || m.GetString() != "open";

        string step;
        if (keywordMode)
        {
            var value = outputs.TryGetValue("response.value", out var v) ? v?.ToString() : null;
            step = value switch
            {
                "approve" => "Branch: APPROVED -> releasing order",
                "reject" => "Branch: REJECTED -> cancelling order",
                _ => $"Branch: default path (value '{value}')",
            };
        }
        else
        {
            step = $"Echo: \"{(outputs.TryGetValue("response.text", out var t) ? t : null)}\"";
        }

        await AppendLogAsync(workflowRunId, $"Resumed after {nodeId} with responder {outputs.GetValueOrDefault("response.responder")}. {step}", ct);
        run = await _db.Runs.FindAsync(new object[] { workflowRunId }, ct);
        run!.Status = WorkflowRunStatus.Completed;
        run.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
    }

    public Task FailAsync(string workflowRunId, string nodeId, string reason, CancellationToken ct = default) =>
        AppendLogAsync(workflowRunId, $"Error branch taken: {reason}", ct);

    private async Task AppendLogAsync(string runId, string line, CancellationToken ct)
    {
        var run = await _db.Runs.FindAsync(new object[] { runId }, ct);
        if (run == null)
        {
            run = new WorkflowRun { Id = runId, CreatedAt = _clock.GetUtcNow().UtcDateTime };
            _db.Runs.Add(run);
        }
        run.Log = (run.Log == null ? "" : run.Log + "\n") + $"{_clock.GetUtcNow():HH:mm:ss} {line}";
        await _db.SaveChangesAsync(ct);
    }
}
