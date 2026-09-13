using Orchestrator.Core;

namespace Orchestrator.Tests;

/// <summary>
/// Covers dynamic graph expansion: a running stage calls
/// <see cref="WorkflowContext.RequestStages"/>, the engine drains and commits the request
/// between scheduling ticks (never while stages run concurrently), and a rejected expansion
/// stops the run instead of silently dropping the work the stage planned.
/// </summary>
public class WorkflowEngineExpansionTests
{
    /// <summary>Planner -> Barrier, where Planner derives its own downstream work at runtime.</summary>
    private static (WorkflowGraph Graph, List<string> Order, NoOpAgent BarrierAgent) BuildExpandingGraph(
        IReadOnlyList<StageDefinition> requested,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? additionalDependencies,
        List<string> order)
    {
        var plannerAgent = new CallbackAgent("Planner",
            ctx => ctx.RequestStages(requested, additionalDependencies), order);
        var barrierAgent = new NoOpAgent();

        var planner = StageBuilder.Make("Planner", plannerAgent);
        var barrier = StageBuilder.Make("Barrier", barrierAgent, dependsOn: ["Planner"]);

        return (new WorkflowGraph([planner, barrier]), order, barrierAgent);
    }

    [Fact]
    public async Task RequestedStages_AreAddedAndExecuted_AndTheBarrierWaitsForThem()
    {
        var order = new List<string>();
        var task1 = StageBuilder.Make("Task-1", new OrderTrackingAgent("Task-1", order), dependsOn: ["Planner"]);
        var task2 = StageBuilder.Make("Task-2", new OrderTrackingAgent("Task-2", order), dependsOn: ["Planner"]);

        var plannerAgent = new CallbackAgent("Planner",
            ctx => ctx.RequestStages(
                [task1, task2],
                new Dictionary<string, IReadOnlyList<string>> { ["Barrier"] = ["Task-1", "Task-2"] }),
            order);

        var planner = StageBuilder.Make("Planner", plannerAgent);
        var barrier = StageBuilder.Make("Barrier", new OrderTrackingAgent("Barrier", order), dependsOn: ["Planner"]);

        var graph = new WorkflowGraph([planner, barrier]);
        Assert.False(graph.Contains("Task-1"));

        var ctx = new WorkflowContext("run-expansion", "scenario");
        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.True(report.Success);
        Assert.True(graph.Contains("Task-1"));
        Assert.True(graph.Contains("Task-2"));
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Task-1"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Task-2"]);

        // Planner first, then the derived work, then the pre-existing barrier that was rewired
        // to wait for it. The barrier must not have run before either new stage.
        Assert.Equal(4, order.Count);
        Assert.Equal("Planner", order[0]);
        Assert.Equal("Barrier", order[^1]);
        Assert.Contains("Task-1", order);
        Assert.Contains("Task-2", order);
        Assert.True(order.IndexOf("Barrier") > order.IndexOf("Task-1"));
        Assert.True(order.IndexOf("Barrier") > order.IndexOf("Task-2"));

        Assert.Contains("Task-1", graph.Get("Barrier").DependsOn);
        Assert.Contains("Task-2", graph.Get("Barrier").DependsOn);

        // The derived stages' results are visible to later stages and to policy rules.
        Assert.Contains("Task-1", ctx.AllResults.Keys);
        Assert.Contains("Task-2", ctx.AllResults.Keys);
    }

    [Fact]
    public async Task SuccessfulExpansion_RecordsAGraphExpandedAuditEntry()
    {
        var order = new List<string>();
        var task1 = StageBuilder.Make("Task-1", new OrderTrackingAgent("Task-1", order), dependsOn: ["Planner"]);

        var (graph, _, _) = BuildExpandingGraph([task1], null, order);
        var ctx = new WorkflowContext("run-expansion-audit", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.True(report.Success);
        var expanded = Assert.Single(ctx.AuditLog, e => e.EventType == AuditEventType.GraphExpanded);
        Assert.Contains("Task-1", expanded.Details);
        Assert.Contains(report.AuditLog, e => e.EventType == AuditEventType.GraphExpanded);
    }

    [Fact]
    public async Task Expansion_ReferencingAnUnknownDependency_StopsTheRun_AndAddsNothing()
    {
        var order = new List<string>();
        var orphan = StageBuilder.Make("Orphan", new OrderTrackingAgent("Orphan", order), dependsOn: ["Ghost"]);

        var (graph, _, barrierAgent) = BuildExpandingGraph([orphan], null, order);
        var ctx = new WorkflowContext("run-expansion-unknown-dep", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        AssertRejected(report, graph, ctx, rejectedStageId: "Orphan", order, barrierAgent);
        Assert.Contains("Ghost", report.StopReason!);
    }

    [Fact]
    public async Task Expansion_ThatWouldCreateACycle_StopsTheRun_AndLeavesDependenciesUntouched()
    {
        var order = new List<string>();
        // Extra depends on Barrier, and Barrier is asked to depend on Extra: a 2-cycle.
        var extra = StageBuilder.Make("Extra", new OrderTrackingAgent("Extra", order), dependsOn: ["Barrier"]);

        var (graph, _, barrierAgent) = BuildExpandingGraph(
            [extra],
            new Dictionary<string, IReadOnlyList<string>> { ["Barrier"] = ["Extra"] },
            order);
        var ctx = new WorkflowContext("run-expansion-cycle", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        AssertRejected(report, graph, ctx, rejectedStageId: "Extra", order, barrierAgent);
        Assert.Contains("Cycle", report.StopReason!, StringComparison.OrdinalIgnoreCase);

        // The rejected rewrite must have been rolled back, not left half-applied.
        Assert.Equal(["Planner"], graph.Get("Barrier").DependsOn);
    }

    [Fact]
    public async Task Expansion_DuplicatingAnExistingStageId_StopsTheRun()
    {
        var order = new List<string>();
        var duplicate = StageBuilder.Make("Barrier", new OrderTrackingAgent("Duplicate", order));

        var (graph, _, barrierAgent) = BuildExpandingGraph([duplicate], null, order);
        var ctx = new WorkflowContext("run-expansion-duplicate", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.False(report.Success);
        Assert.NotNull(report.StopReason);
        Assert.StartsWith("Rejected graph expansion", report.StopReason);
        Assert.Contains("Barrier", report.StopReason);
        Assert.DoesNotContain("Duplicate", order);
        Assert.Equal(0, barrierAgent.CallCount);
        // The original stage definition is untouched.
        Assert.Same(barrierAgent, graph.Get("Barrier").Agent);
    }

    [Fact]
    public async Task Expansion_AddingADependencyToAnAlreadySucceededStage_StopsTheRun()
    {
        var order = new List<string>();
        var extra = StageBuilder.Make("Extra", new OrderTrackingAgent("Extra", order));

        // Planner has already Succeeded by the time its own expansion is committed, so rewiring
        // it would misrepresent what it actually waited for.
        var (graph, _, barrierAgent) = BuildExpandingGraph(
            [extra],
            new Dictionary<string, IReadOnlyList<string>> { ["Planner"] = ["Extra"] },
            order);
        var ctx = new WorkflowContext("run-expansion-late-rewire", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        AssertRejected(report, graph, ctx, rejectedStageId: "Extra", order, barrierAgent);
        Assert.Contains("Succeeded", report.StopReason!);
        Assert.Equal([], graph.Get("Planner").DependsOn);
    }

    private static void AssertRejected(
        RunReport report,
        WorkflowGraph graph,
        WorkflowContext ctx,
        string rejectedStageId,
        List<string> order,
        NoOpAgent barrierAgent)
    {
        Assert.False(report.Success);
        Assert.NotNull(report.StopReason);
        Assert.StartsWith("Rejected graph expansion", report.StopReason);

        Assert.True(ctx.SafeStopRequested);
        Assert.Contains(ctx.AuditLog, e => e.EventType == AuditEventType.SafeStopTriggered);
        Assert.DoesNotContain(ctx.AuditLog, e => e.EventType == AuditEventType.GraphExpanded);

        // Nothing from the bad expansion exists or ran, and the run stopped before the barrier.
        Assert.False(graph.Contains(rejectedStageId));
        Assert.DoesNotContain(rejectedStageId, order);
        Assert.DoesNotContain(rejectedStageId, report.StageStatuses.Keys);
        Assert.Equal(0, barrierAgent.CallCount);
        Assert.Equal(StageStatus.Pending, report.StageStatuses["Barrier"]);
    }
}
