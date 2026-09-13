using System.Collections.Concurrent;
using System.Diagnostics;
using Orchestrator.Core;

namespace Orchestrator.Tests;

public class WorkflowEngineSchedulingTests
{
    [Fact]
    public async Task DiamondGraph_RunsSiblingsConcurrently_AndBarriersOnJoin()
    {
        var log = new ConcurrentBag<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
        var delay = TimeSpan.FromMilliseconds(80);

        var a = StageBuilder.Make("A", new NoOpAgent());
        var b = StageBuilder.Make("B", new DelayAgent("B", delay, log), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new DelayAgent("C", delay, log), dependsOn: ["A"]);
        var d = StageBuilder.Make("D", new NoOpAgent(), dependsOn: ["B", "C"]);

        var graph = new WorkflowGraph([a, b, c, d]);
        var ctx = new WorkflowContext("run-diamond", "scenario");

        var sw = Stopwatch.StartNew();
        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());
        sw.Stop();

        Assert.True(report.Success);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["D"]);

        // If B and C ran sequentially this would take >= 160ms; parallel execution keeps it well under that.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(150),
            $"Expected concurrent execution to finish well under 150ms, took {sw.Elapsed.TotalMilliseconds}ms");

        var bEntry = log.Single(x => x.Id == "B");
        var cEntry = log.Single(x => x.Id == "C");

        // Prove actual overlap: each one's window intersects the other's.
        Assert.True(bEntry.Start < cEntry.End && cEntry.Start < bEntry.End,
            "Expected B and C execution windows to overlap (run concurrently).");
    }

    [Fact]
    public async Task LinearChain_RunsInDependencyOrder()
    {
        var order = new List<string>();

        var a = StageBuilder.Make("A", new OrderTrackingAgent("A", order));
        var b = StageBuilder.Make("B", new OrderTrackingAgent("B", order), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new OrderTrackingAgent("C", order), dependsOn: ["B"]);

        var graph = new WorkflowGraph([a, b, c]);
        var ctx = new WorkflowContext("run-chain", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.True(report.Success);
        Assert.Equal(["A", "B", "C"], order);
    }

    [Fact]
    public async Task EntryGateFalse_MarksStageSkipped_AndOverallRunStillSucceeds()
    {
        var agent = new NoOpAgent();
        var gated = StageBuilder.Make("Gated", agent, entryGate: _ => false);

        var graph = new WorkflowGraph([gated]);
        var ctx = new WorkflowContext("run-gate", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["Gated"]);
        Assert.Equal(0, agent.CallCount);
        // Skipped-only run counts as overall success per the engine's definition.
        Assert.True(report.Success);
    }

    [Fact]
    public async Task IndependentBranches_BothRun_EvenIfOneIsSkipped()
    {
        // A is skipped via entry gate; unrelated branch B->C should still run to completion.
        var skipped = StageBuilder.Make("Skipped", new NoOpAgent(), entryGate: _ => false);
        var bAgent = new NoOpAgent();
        var b = StageBuilder.Make("B", bAgent);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["B"]);

        var graph = new WorkflowGraph([skipped, b, c]);
        var ctx = new WorkflowContext("run-independent", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["Skipped"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["B"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["C"]);
        Assert.True(report.Success);
    }
}
