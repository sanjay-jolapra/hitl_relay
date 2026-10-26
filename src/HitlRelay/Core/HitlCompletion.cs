using HitlRelay.Data;
using HitlRelay.Models;
using Microsoft.EntityFrameworkCore;

namespace HitlRelay.Core;

/// <summary>
/// The only place a request leaves Pending. Closing the request and claiming the run happen in one
/// transaction guarded by optimistic concurrency on Status, so two simultaneous replies cannot both
/// resume the workflow (requirements 6.4). IWorkflowResumer is called only after the commit.
/// </summary>
public sealed class HitlCompletion
{
    private readonly HitlDbContext _db;
    private readonly IServiceProvider _services;
    private readonly AdapterRegistry _adapters;
    private readonly AuditService _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<HitlCompletion> _log;

    public HitlCompletion(HitlDbContext db, IServiceProvider services, AdapterRegistry adapters,
        AuditService audit, TimeProvider clock, ILogger<HitlCompletion> log)
    {
        _db = db;
        _services = services;
        _adapters = adapters;
        _audit = audit;
        _clock = clock;
        _log = log;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A valid reply: request Answered, run Resumed, then resume the workflow.</summary>
    public Task<bool> AnswerAsync(Guid requestId, HitlResponse response, Dictionary<string, object?> outputs,
        HitlOutcome outcome, CancellationToken ct) =>
        CompleteAsync(requestId, HitlRequestStatus.Answered, response, outputs, null, true, outcome, "answered", ct);

    /// <summary>Timeout rule ContinueWithDefault: request TimedOut, workflow resumes with the default.</summary>
    public Task<bool> TimeoutContinueAsync(Guid requestId, Dictionary<string, object?> outputs, HitlOutcome outcome, CancellationToken ct) =>
        CompleteAsync(requestId, HitlRequestStatus.TimedOut, null, outputs, null, true, outcome, "timed_out_continue", ct);

    /// <summary>Request closed and the node fails (timeout rule Fail, cancel).</summary>
    public Task<bool> FailAsync(Guid requestId, HitlRequestStatus finalStatus, string reason, HitlOutcome outcome, CancellationToken ct) =>
        CompleteAsync(requestId, finalStatus, null, null, reason, true, outcome,
            finalStatus == HitlRequestStatus.Cancelled ? "cancelled" : "timed_out_fail", ct);

    /// <summary>Request closed but the run keeps waiting (the escalation replacement takes over).</summary>
    public Task<bool> CloseAsync(Guid requestId, HitlRequestStatus finalStatus, HitlOutcome outcome, CancellationToken ct) =>
        CompleteAsync(requestId, finalStatus, null, null, null, false, outcome, "timed_out_escalated", ct);

    private async Task<bool> CompleteAsync(Guid requestId, HitlRequestStatus newStatus, HitlResponse? response,
        Dictionary<string, object?>? outputs, string? failReason, bool touchRun, HitlOutcome outcome,
        string auditType, CancellationToken ct)
    {
        HitlRequest? req = null;
        WorkflowRun? run = null;
        var committed = false;

        // SQLite takes a database-level write lock, so concurrent writers can get SQLITE_BUSY surfaced as
        // DbUpdateException; retrying lets the loser observe the final status. SQL Server / Postgres do not need this.
        for (var attempt = 1; attempt <= 6 && !committed; attempt++)
        {
            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                req = await _db.Requests.FindAsync(new object[] { requestId }, ct);
                if (req == null) return false;
                await _db.Entry(req).ReloadAsync(ct);
                if (req.Status != HitlRequestStatus.Pending) return false;

                req.Status = newStatus;
                req.AnsweredAt = Now;
                if (response != null) _db.Responses.Add(response);

                if (touchRun)
                {
                    run = await _db.Runs.FindAsync(new object[] { req.WorkflowRunId }, ct);
                    if (run == null || run.Status != WorkflowRunStatus.Waiting)
                    {
                        _log.LogWarning("Run {RunId} is not Waiting; request {RequestId} left unchanged", req.WorkflowRunId, req.Id);
                        _db.ChangeTracker.Clear();
                        return false;
                    }
                    await _db.Entry(run).ReloadAsync(ct);
                    run.Status = failReason == null ? WorkflowRunStatus.Resumed : WorkflowRunStatus.Failed;
                    run.OutputsJson = outputs == null ? null : HitlJson.Serialize(outputs);
                    run.UpdatedAt = Now;
                }

                _audit.Add(req.Id, auditType, new { outcome.Value, responder = Mask.Value(outcome.ResponderDisplayName), failReason });
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                committed = true;
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                return false; // another writer closed the request first
            }
            catch (DbUpdateException ex) when (attempt < 6)
            {
                _log.LogDebug(ex, "Database busy completing {RequestId}; retrying", requestId);
                _db.ChangeTracker.Clear();
                await Task.Delay(50 * attempt, ct);
            }
        }

        if (!committed || req == null) return false;

        using var scope = _log.BeginScope(new Dictionary<string, object> { ["RequestId"] = req.Id, ["WorkflowRunId"] = req.WorkflowRunId });
        RecordMetrics(req, newStatus);

        if (touchRun && run != null)
            await ResumeAsync(run, req.NodeId, outputs, failReason, ct);

        try
        {
            await _adapters.Get(req.Channel).AcknowledgeAsync(req, outcome, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Acknowledging request {RequestId} failed (non-fatal)", req.Id);
        }
        return true;
    }

    private async Task ResumeAsync(WorkflowRun run, string nodeId, Dictionary<string, object?>? outputs, string? failReason, CancellationToken ct)
    {
        try
        {
            // Resolved lazily: the host's resumer may itself depend on HitlService (as the sample engine does).
            var resumer = _services.GetRequiredService<IWorkflowResumer>();
            if (failReason == null)
                await resumer.ResumeAsync(run.Id, nodeId, outputs ?? new Dictionary<string, object?>(), ct);
            else
                await resumer.FailAsync(run.Id, nodeId, failReason, ct);
            await _audit.WriteAsync(null, "resumed", new { runId = run.Id, nodeId }, ct);
        }
        catch (Exception ex)
        {
            // Do not ask the person to answer again: keep the stored outputs and surface the run for retry.
            _log.LogError(ex, "Resuming run {RunId} failed", run.Id);
            _db.ChangeTracker.Clear();
            var stored = await _db.Runs.FindAsync(new object[] { run.Id }, ct);
            if (stored != null)
            {
                stored.Status = WorkflowRunStatus.ResumeFailed;
                stored.UpdatedAt = Now;
                _audit.Add(null, "resume_failed", new { runId = run.Id, nodeId, error = ex.Message });
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>Retries the resume of a run stuck in ResumeFailed, using the stored outputs.</summary>
    public async Task<bool> RetryResumeAsync(string runId, CancellationToken ct)
    {
        var run = await _db.Runs.FindAsync(new object[] { runId }, ct);
        if (run == null || run.Status != WorkflowRunStatus.ResumeFailed) return false;

        var req = await _db.Requests.Where(r => r.WorkflowRunId == runId && r.Status != HitlRequestStatus.Pending)
            .OrderByDescending(r => r.AnsweredAt).FirstOrDefaultAsync(ct);
        if (req == null) return false;

        var outputs = HitlJson.Deserialize<Dictionary<string, object?>>(run.OutputsJson) ?? new Dictionary<string, object?>();
        run.Status = WorkflowRunStatus.Resumed;
        await _db.SaveChangesAsync(ct);
        await ResumeAsync(run, req.NodeId, outputs, null, ct);
        return true;
    }

    private static void RecordMetrics(HitlRequest req, HitlRequestStatus status)
    {
        var tag = new KeyValuePair<string, object?>("channel", req.Channel);
        if (status == HitlRequestStatus.Answered)
        {
            HitlTelemetry.Answered.Add(1, tag);
            if (req.AnsweredAt is { } at) HitlTelemetry.TimeToRespond.Record((at - req.CreatedAt).TotalSeconds, tag);
        }
        else if (status == HitlRequestStatus.TimedOut)
        {
            HitlTelemetry.TimedOut.Add(1, tag);
        }
    }
}
