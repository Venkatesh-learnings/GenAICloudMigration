using Orchestrator.Core;

namespace Orchestrator.Tests;

/// <summary>
/// Covers the newly-reachable dynamic re-planning path: RunAsync's optional
/// <c>resumeFrom</c> parameter, combined with re-using the same WorkflowContext across two
/// RunAsync calls, lets a caller simulate "an upstream input changed, please re-plan" by forcing
/// one already-Succeeded stage back to Stale and re-running. Re-planning is output-hash-driven:
/// only a stage whose new output actually differs from before cascades Stale to its descendants.
/// </summary>
public class WorkflowEngineReplanningTests
{
    private static WorkflowGraph BuildChain(IStageAgent a, IStageAgent b, IStageAgent c) => new([
        StageBuilder.Make("A", a),
        StageBuilder.Make("B", b, dependsOn: ["A"]),
        StageBuilder.Make("C", c, dependsOn: ["B"])
    ]);

    private static Dictionary<string, StageStatus> ResumeFromWithAStale(RunReport firstReport)
    {
        var resumeFrom = new Dictionary<string, StageStatus>(firstReport.StageStatuses)
        {
            ["A"] = StageStatus.Stale
        };
        return resumeFrom;
    }

    [Fact]
    public async Task ResumeFrom_SameOutput_ReExecutesOnlyTheStaleStage()
    {
        var aAgent = new ScriptedAgent(StageResult.Ok("A done", outputs: new Dictionary<string, object> { ["x"] = 1 }));
        var bAgent = new NoOpAgent();
        var cAgent = new NoOpAgent();

        var graph = BuildChain(aAgent, bAgent, cAgent);
        var ctx = new WorkflowContext("run-replan-same", "scenario");
        var engine = new WorkflowEngine();

        var firstReport = await engine.RunAsync(graph, ctx, new AutoApprovalProvider());
        Assert.True(firstReport.Success);
        Assert.Equal(1, aAgent.CallCount);
        Assert.Equal(1, bAgent.CallCount);
        Assert.Equal(1, cAgent.CallCount);

        var secondReport = await engine.RunAsync(graph, ctx, new AutoApprovalProvider(), resumeFrom: ResumeFromWithAStale(firstReport));

        Assert.True(secondReport.Success);
        // A re-executed because it was seeded Stale...
        Assert.Equal(2, aAgent.CallCount);
        // ...but its output is byte-for-byte identical, so RecordOutputHash reports no change and
        // B/C are never marked Stale — they stay at their first-run Succeeded status and are not
        // re-invoked.
        Assert.Equal(1, bAgent.CallCount);
        Assert.Equal(1, cAgent.CallCount);
        Assert.Equal(StageStatus.Succeeded, secondReport.StageStatuses["A"]);
        Assert.Equal(StageStatus.Succeeded, secondReport.StageStatuses["B"]);
        Assert.Equal(StageStatus.Succeeded, secondReport.StageStatuses["C"]);
    }

    [Fact]
    public async Task ResumeFrom_DifferentOutput_CascadesStaleAndReExecutesAllDescendants()
    {
        var aAgent = new ScriptedAgent(
            StageResult.Ok("A done v1", outputs: new Dictionary<string, object> { ["x"] = 1 }),
            StageResult.Ok("A done v2", outputs: new Dictionary<string, object> { ["x"] = 2 }));
        var bAgent = new NoOpAgent();
        var cAgent = new NoOpAgent();

        var graph = BuildChain(aAgent, bAgent, cAgent);
        var ctx = new WorkflowContext("run-replan-diff", "scenario");
        var engine = new WorkflowEngine();

        var firstReport = await engine.RunAsync(graph, ctx, new AutoApprovalProvider());
        Assert.True(firstReport.Success);
        Assert.Equal(1, aAgent.CallCount);
        Assert.Equal(1, bAgent.CallCount);
        Assert.Equal(1, cAgent.CallCount);

        var secondReport = await engine.RunAsync(graph, ctx, new AutoApprovalProvider(), resumeFrom: ResumeFromWithAStale(firstReport));

        Assert.True(secondReport.Success);
        Assert.Equal(2, aAgent.CallCount);
        // A's output genuinely differs this time, so both B and C (the full transitive
        // descendant set, not just the direct dependent B) are marked Stale and re-executed.
        Assert.Equal(2, bAgent.CallCount);
        Assert.Equal(2, cAgent.CallCount);
        Assert.Equal(StageStatus.Succeeded, secondReport.StageStatuses["A"]);
        Assert.Equal(StageStatus.Succeeded, secondReport.StageStatuses["B"]);
        Assert.Equal(StageStatus.Succeeded, secondReport.StageStatuses["C"]);

        Assert.Contains(ctx.AuditLog, e => e.StageId == "B" && e.EventType == AuditEventType.StageReplanned);
        Assert.Contains(ctx.AuditLog, e => e.StageId == "C" && e.EventType == AuditEventType.StageReplanned);
    }

    [Fact]
    public async Task DecisionLineage_AccumulatesAcrossBothRunAsyncCalls()
    {
        var aAgent = new ScriptedAgent(
            StageResult.Ok("A done v1", outputs: new Dictionary<string, object> { ["x"] = 1 }),
            StageResult.Ok("A done v2", outputs: new Dictionary<string, object> { ["x"] = 2 }));
        var bAgent = new NoOpAgent();
        var cAgent = new NoOpAgent();

        var graph = BuildChain(aAgent, bAgent, cAgent);
        var ctx = new WorkflowContext("run-replan-lineage", "scenario");
        var engine = new WorkflowEngine();

        var firstReport = await engine.RunAsync(graph, ctx, new AutoApprovalProvider());
        var lineageAfterFirstRun = ctx.DecisionLineage.ToList();
        Assert.Equal(3, lineageAfterFirstRun.Count); // A, B, C each record one decision.

        var secondReport = await engine.RunAsync(graph, ctx, new AutoApprovalProvider(), resumeFrom: ResumeFromWithAStale(firstReport));

        // The context (and thus its decision lineage) is shared across both RunAsync calls: the
        // second run's entries are appended, not a fresh/replaced log.
        Assert.Equal(6, ctx.DecisionLineage.Count); // 3 from the first run + 3 more (A, B, C) from the second.

        // Lineage from the first run is still present verbatim (same records, same order) after
        // the second call — nothing was cleared or replaced.
        Assert.Equal(lineageAfterFirstRun, ctx.DecisionLineage.Take(lineageAfterFirstRun.Count));
        Assert.Contains(ctx.DecisionLineage, d => d.StageId == "A" && d.Summary == "A done v1");
        Assert.Contains(ctx.DecisionLineage, d => d.StageId == "A" && d.Summary == "A done v2");
    }

    [Fact]
    public async Task OmittingResumeFrom_BehavesExactlyAsBefore()
    {
        // Sanity check: the simplest existing pattern (no resumeFrom argument at all) still
        // compiles and passes unchanged now that resumeFrom is an added optional parameter.
        var order = new List<string>();
        var a = StageBuilder.Make("A", new OrderTrackingAgent("A", order));
        var b = StageBuilder.Make("B", new OrderTrackingAgent("B", order), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new OrderTrackingAgent("C", order), dependsOn: ["B"]);

        var graph = new WorkflowGraph([a, b, c]);
        var ctx = new WorkflowContext("run-chain-no-resume", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.True(report.Success);
        Assert.Equal(["A", "B", "C"], order);
    }
}
