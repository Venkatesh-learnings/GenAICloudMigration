using System.Collections.Concurrent;
using System.Diagnostics;

namespace Orchestrator.Core;

/// <summary>
/// Executes a <see cref="WorkflowGraph"/> to completion. Stages whose
/// dependencies are satisfied in the same scheduling tick run concurrently
/// (Task.WhenAll), and any stage with more than one dependency is therefore
/// a natural synchronization barrier — this is what gives the engine
/// non-linear, parallel-with-sync execution instead of a simple chain.
///
/// Each stage passes through policy guardrails, an optional human approval
/// checkpoint, then bounded retries with exponential backoff; exhausting
/// retries triggers rollback. A stage whose output changes on a re-run
/// (detected via output hashing) marks every downstream stage stale so the
/// engine re-plans and re-executes them automatically. A policy Deny or an
/// agent explicitly requesting it trips a safe-stop that halts scheduling of
/// new stages.
/// </summary>
public class WorkflowEngine
{
    /// <param name="resumeFrom">
    /// Optional statuses to seed the run from instead of all-Pending — e.g. a previous
    /// <see cref="RunReport.StageStatuses"/> with one stage forced back to
    /// <see cref="StageStatus.Stale"/> to simulate "an upstream input changed, re-plan
    /// from here." Passing the same <paramref name="ctx"/> across such calls is what makes
    /// re-planning observable: decision lineage and the audit log accumulate across calls,
    /// and <see cref="WorkflowContext.RecordOutputHash"/> compares each stage's new output
    /// against what it recorded last time, so a genuinely different result cascades
    /// Stale through every descendant exactly as a single in-flight run would.
    /// </param>
    public async Task<RunReport> RunAsync(
        WorkflowGraph graph,
        WorkflowContext ctx,
        IApprovalProvider approvals,
        IEnumerable<IPolicyRule>? policies = null,
        IReadOnlyDictionary<string, StageStatus>? resumeFrom = null,
        CancellationToken ct = default)
    {
        var guardrails = new PolicyGuardrailEngine(policies ?? []);
        var metrics = new MetricsCollector();
        var statuses = new ConcurrentDictionary<string, StageStatus>(
            graph.Stages.ToDictionary(s => s.Id, s => resumeFrom?.GetValueOrDefault(s.Id) ?? StageStatus.Pending));

        string? stopReason = null;

        while (true)
        {
            if (ctx.SafeStopRequested)
            {
                stopReason = ctx.SafeStopReason;
                ctx.Audit("*", AuditEventType.SafeStopTriggered, stopReason ?? "safe-stop requested");
                break;
            }

            StageStatus StatusOf(string stageId) => statuses.GetOrAdd(stageId, StageStatus.Pending);

            var blockingChanged = PropagateBlocking(graph, statuses, ctx);

            var depsSatisfied = graph.Stages
                .Where(s => StatusOf(s.Id) is StageStatus.Pending or StageStatus.Stale)
                .Where(s => s.DependenciesSatisfiedBy(StatusOf))
                .ToList();

            var gatedOut = depsSatisfied.Where(s => !s.EntryGate(ctx)).ToList();
            foreach (var stage in gatedOut)
            {
                statuses[stage.Id] = StageStatus.Skipped;
                ctx.Audit(stage.Id, AuditEventType.StageBlocked, "entry gate returned false; stage skipped");
            }

            var ready = depsSatisfied.Where(s => StatusOf(s.Id) is StageStatus.Pending or StageStatus.Stale).ToList();

            if (ready.Count == 0)
            {
                // A stage becoming Blocked/Skipped just now might be exactly what unblocks
                // (or cascades a skip into) some other stage — but that only gets noticed the
                // *next* time PropagateBlocking runs. So before declaring a deadlock, give it
                // one more tick whenever this tick actually changed something; only a tick that
                // makes zero progress is a genuine deadlock.
                if (blockingChanged || gatedOut.Count > 0)
                {
                    continue;
                }

                var unresolved = graph.Stages.Where(s => StatusOf(s.Id) is StageStatus.Pending or StageStatus.Stale).ToList();
                if (unresolved.Count > 0)
                {
                    stopReason = $"Deadlock: stage(s) {string.Join(", ", unresolved.Select(s => s.Id))} can never become ready.";
                }
                break;
            }

            foreach (var stage in ready)
            {
                statuses[stage.Id] = StageStatus.Running;
            }

            await Task.WhenAll(ready.Select(stage =>
                ExecuteStageAsync(stage, ctx, statuses, guardrails, approvals, metrics, graph, ct)));

            ApplyRequestedExpansions(graph, statuses, ctx);
        }

        var overallSuccess = stopReason is null &&
            graph.Stages.All(s => statuses[s.Id] is StageStatus.Succeeded or StageStatus.Skipped);

        return new RunReport(
            ctx.RunId,
            ctx.ScenarioName,
            overallSuccess,
            stopReason,
            statuses.ToDictionary(kv => kv.Key, kv => kv.Value),
            ctx.DecisionLineage,
            ctx.AuditLog,
            metrics.Snapshot());
    }

    /// <summary>
    /// Commits any stages that just-finished stages asked for, between scheduling ticks
    /// so the graph is never mutated while stages run concurrently. A rejected expansion
    /// (unknown dependency, cycle, or a barrier that already started) fails the run via
    /// safe-stop rather than silently dropping the work the stage planned.
    /// </summary>
    private static void ApplyRequestedExpansions(
        WorkflowGraph graph,
        ConcurrentDictionary<string, StageStatus> statuses,
        WorkflowContext ctx)
    {
        foreach (var expansion in ctx.DrainRequestedExpansions())
        {
            try
            {
                graph.AddStages(
                    expansion.NewStages,
                    expansion.AdditionalDependencies,
                    stageId => statuses.GetValueOrDefault(stageId, StageStatus.Pending));

                foreach (var stage in expansion.NewStages)
                {
                    statuses.TryAdd(stage.Id, StageStatus.Pending);
                }

                ctx.Audit("*", AuditEventType.GraphExpanded,
                    $"graph expanded with {expansion.NewStages.Count} stage(s): {string.Join(", ", expansion.NewStages.Select(s => s.Id))}");
            }
            catch (InvalidOperationException ex)
            {
                ctx.RequestSafeStop($"Rejected graph expansion: {ex.Message}");
            }
        }
    }

    /// <returns>true if any stage's status changed during this call.</returns>
    private static bool PropagateBlocking(WorkflowGraph graph, ConcurrentDictionary<string, StageStatus> statuses, WorkflowContext ctx)
    {
        var everChanged = false;
        bool changed;
        do
        {
            changed = false;
            foreach (var stage in graph.Stages)
            {
                StageStatus StatusOf(string stageId) => statuses.GetOrAdd(stageId, StageStatus.Pending);

                if (StatusOf(stage.Id) is not (StageStatus.Pending or StageStatus.Stale))
                {
                    continue;
                }

                var blockedBy = stage.DependsOn.FirstOrDefault(dep =>
                    StatusOf(dep) is StageStatus.Failed or StageStatus.Blocked or StageStatus.RolledBack);

                if (blockedBy is not null)
                {
                    statuses[stage.Id] = StageStatus.Blocked;
                    ctx.Audit(stage.Id, AuditEventType.StageBlocked, $"upstream dependency '{blockedBy}' did not succeed");
                    changed = true;
                    continue;
                }

                // A dependency that was deliberately gated out (EntryGate false) can never
                // produce the output this stage needed, so cascade the skip rather than leave
                // the stage stuck forever. Stages declaring AllSettled opted out of that:
                // they treat a skipped dependency as satisfied and still run.
                if (stage.DependencyRule == DependencyRule.AllSucceeded)
                {
                    var skippedDep = stage.DependsOn.FirstOrDefault(dep => StatusOf(dep) == StageStatus.Skipped);
                    if (skippedDep is not null)
                    {
                        statuses[stage.Id] = StageStatus.Skipped;
                        ctx.Audit(stage.Id, AuditEventType.StageBlocked, $"upstream dependency '{skippedDep}' was skipped; cascading skip");
                        changed = true;
                    }
                }
            }
            everChanged |= changed;
        } while (changed);

        return everChanged;
    }

    private static async Task ExecuteStageAsync(
        StageDefinition stage,
        WorkflowContext ctx,
        ConcurrentDictionary<string, StageStatus> statuses,
        PolicyGuardrailEngine guardrails,
        IApprovalProvider approvals,
        MetricsCollector metrics,
        WorkflowGraph graph,
        CancellationToken ct)
    {
        ctx.Audit(stage.Id, AuditEventType.StageStarted, "stage started");
        var sw = Stopwatch.StartNew();

        var (policyDecision, reasons) = guardrails.Evaluate(ctx, stage);
        if (policyDecision == PolicyDecision.Deny)
        {
            ctx.Audit(stage.Id, AuditEventType.PolicyBlocked, string.Join("; ", reasons));
            statuses[stage.Id] = StageStatus.Failed;
            metrics.RecordStageOutcome(stage.Id, false, sw.Elapsed);
            ctx.RequestSafeStop($"Policy denied stage '{stage.Id}': {string.Join("; ", reasons)}");
            return;
        }

        var needsApproval = stage.RequiresApproval || policyDecision == PolicyDecision.RequireApproval;
        var approvalReason = reasons.Count > 0 ? string.Join("; ", reasons) : "explicit approval gate configured for this stage";

        StageResult? lastResult = null;
        Exception? lastError = null;

        for (var attempt = 0; attempt <= stage.MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested || ctx.SafeStopRequested)
            {
                statuses[stage.Id] = StageStatus.Blocked;
                return;
            }

            try
            {
                if (needsApproval && attempt == 0)
                {
                    statuses[stage.Id] = StageStatus.AwaitingApproval;
                    ctx.Audit(stage.Id, AuditEventType.ApprovalRequested, approvalReason);

                    var preview = StageResult.Ok($"About to execute stage '{stage.Id}'", approvalReason);
                    var decision = await approvals.RequestApprovalAsync(ctx, stage, preview, ct);

                    if (decision == ApprovalDecision.Rejected)
                    {
                        ctx.Audit(stage.Id, AuditEventType.ApprovalDenied, "human rejected the stage");
                        statuses[stage.Id] = StageStatus.Blocked;
                        metrics.RecordStageOutcome(stage.Id, false, sw.Elapsed);
                        return;
                    }

                    ctx.Audit(stage.Id, AuditEventType.ApprovalGranted, "human approved the stage");
                    statuses[stage.Id] = StageStatus.Running;
                }

                var result = await stage.Agent.ExecuteAsync(ctx, stage, ct);

                if (result.Success && stage.ExitGate(ctx, result))
                {
                    ctx.SetResult(stage.Id, result);
                    var outputChanged = ctx.RecordOutputHash(stage.Id, result);
                    statuses[stage.Id] = StageStatus.Succeeded;
                    metrics.RecordStageOutcome(stage.Id, true, sw.Elapsed);
                    ctx.Audit(stage.Id, AuditEventType.StageSucceeded, result.Summary);

                    if (outputChanged)
                    {
                        Replan(stage.Id, ctx, statuses, graph);
                    }

                    return;
                }

                lastResult = result;
                throw new InvalidOperationException(
                    result.Error?.Message ?? $"Stage '{stage.Id}' failed or did not pass its exit gate: {result.Summary}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (attempt < stage.MaxRetries)
                {
                    metrics.RecordRetry(stage.Id);
                    ctx.Audit(stage.Id, AuditEventType.RetryAttempted, $"attempt {attempt + 1} failed: {ex.Message}");
                    var delay = TimeSpan.FromMilliseconds(stage.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                    await Task.Delay(delay, ct);
                }
            }
        }

        metrics.RecordRollback(stage.Id);
        if (stage.Agent is IRollbackable rollbackable && lastResult is not null)
        {
            await rollbackable.RollbackAsync(ctx, stage, lastResult, ct);
        }

        ctx.Audit(stage.Id, AuditEventType.RolledBack, lastError?.Message ?? "max retries exhausted");
        statuses[stage.Id] = StageStatus.RolledBack;
        metrics.RecordStageOutcome(stage.Id, false, sw.Elapsed);
    }

    private static void Replan(string stageId, WorkflowContext ctx, ConcurrentDictionary<string, StageStatus> statuses, WorkflowGraph graph)
    {
        foreach (var descendantId in graph.GetDescendants(stageId))
        {
            if (statuses[descendantId] is StageStatus.Succeeded or StageStatus.Failed or StageStatus.RolledBack or StageStatus.Skipped)
            {
                statuses[descendantId] = StageStatus.Stale;
                ctx.Audit(descendantId, AuditEventType.StageReplanned,
                    $"upstream stage '{stageId}' output changed on re-run; marking stale for re-execution");
            }
        }
    }
}
