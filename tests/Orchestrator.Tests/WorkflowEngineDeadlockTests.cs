using Orchestrator.Core;

namespace Orchestrator.Tests;

/// <summary>
/// Covers PropagateBlocking's cascading-skip branch (a stage whose dependency is Skipped
/// becomes Skipped too, audited via AuditEventType.StageBlocked mentioning "cascading skip").
///
/// The main RunAsync scheduling loop no longer declares a deadlock the instant a tick finds
/// zero ready stages: it first checks whether that tick's PropagateBlocking call actually
/// changed a stage's status, or whether EntryGate just skipped a stage this tick
/// (`blockingChanged || gatedOut.Count > 0`). Either signal means real progress happened, so
/// the loop takes one more tick — which is exactly what lets PropagateBlocking see a
/// brand-new Skipped status and cascade it (transitively, via its own internal fixed-point
/// do-while loop) to every descendant. Only a tick that produces zero ready stages AND zero
/// status changes AND zero new EntryGate skips is a genuine deadlock. As a result, even a
/// fully isolated skip chain (nothing else left runnable in the graph) now cascades to
/// Skipped instead of leaving descendants stuck Pending — see the tests below, confirmed
/// against the current src/Orchestrator.Core/WorkflowEngine.cs.
/// </summary>
public class WorkflowEngineDeadlockTests
{
    [Fact]
    public async Task IsolatedSkipChain_TwoStages_CascadesToSkipped()
    {
        // Upstream is skipped via EntryGate on the first tick (gatedOut.Count > 0), so the loop
        // takes one more tick instead of declaring a deadlock; that next tick's
        // PropagateBlocking call sees Upstream already Skipped and cascades Downstream.
        var upstream = StageBuilder.Make("Upstream", new NoOpAgent(), entryGate: _ => false);
        var downstream = StageBuilder.Make("Downstream", new NoOpAgent(), dependsOn: ["Upstream"]);

        var graph = new WorkflowGraph([upstream, downstream]);
        var ctx = new WorkflowContext("run-isolated-cascade", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["Upstream"]);
        Assert.Equal(StageStatus.Skipped, report.StageStatuses["Downstream"]);
        Assert.Null(report.StopReason);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task IsolatedSkipChain_ThreeLevels_CascadesTransitively()
    {
        // Same as above but a 3-level pure chain (A -> B -> C), still isolated: A is skipped via
        // EntryGate on the first tick, which keeps the loop alive for another tick; from there
        // PropagateBlocking's internal fixed-point loop cascades the skip through B and then C
        // in that single call.
        var a = StageBuilder.Make("A", new NoOpAgent(), entryGate: _ => false);
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["B"]);

        var graph = new WorkflowGraph([a, b, c]);
        var ctx = new WorkflowContext("run-isolated-chain-cascade", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["A"]);
        Assert.Equal(StageStatus.Skipped, report.StageStatuses["B"]);
        Assert.Equal(StageStatus.Skipped, report.StageStatuses["C"]);
        Assert.Null(report.StopReason);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task DependencyOnOneSkippedAndOneSucceeded_CascadesToSkipped()
    {
        // Here the cascade DOES fire: SucceededDep is still runnable in the same tick that
        // SkippedDep gets skipped, so the loop does not break — it executes SucceededDep and
        // loops again, at which point PropagateBlocking sees SkippedDep already Skipped and
        // cascades Downstream. Any one Skipped dependency is enough, even alongside a Succeeded
        // sibling dependency.
        var skippedDep = StageBuilder.Make("SkippedDep", new NoOpAgent(), entryGate: _ => false);
        var succeededDep = StageBuilder.Make("SucceededDep", new NoOpAgent());
        var downstream = StageBuilder.Make("Downstream", new NoOpAgent(), dependsOn: ["SkippedDep", "SucceededDep"]);

        var graph = new WorkflowGraph([skippedDep, succeededDep, downstream]);
        var ctx = new WorkflowContext("run-mixed-deps", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["SkippedDep"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["SucceededDep"]);
        Assert.Equal(StageStatus.Skipped, report.StageStatuses["Downstream"]);
        Assert.Null(report.StopReason);
        Assert.True(report.Success);

        var cascadeEntry = Assert.Single(ctx.AuditLog, e => e.StageId == "Downstream" && e.EventType == AuditEventType.StageBlocked);
        Assert.Contains("SkippedDep", cascadeEntry.Details);
        Assert.Contains("cascading skip", cascadeEntry.Details);
    }

    [Fact]
    public async Task ThreeLevelChain_WithUnrelatedKeepAliveBranch_CascadesSkipTransitively()
    {
        // Same 3-level pure chain as the isolated test above, but with an unrelated branch
        // (KeepAlive -> AfterKeepAlive) that still needs to execute after A is skipped. That
        // keeps the scheduling loop from breaking on this tick, so the next tick's
        // PropagateBlocking call sees A already Skipped and cascades through B *and* C in the
        // same fixed-point loop — confirming the cascade is transitive once it actually fires.
        var a = StageBuilder.Make("A", new NoOpAgent(), entryGate: _ => false);
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["B"]);
        var keepAlive = StageBuilder.Make("KeepAlive", new NoOpAgent());
        var afterKeepAlive = StageBuilder.Make("AfterKeepAlive", new NoOpAgent(), dependsOn: ["KeepAlive"]);

        var graph = new WorkflowGraph([a, b, c, keepAlive, afterKeepAlive]);
        var ctx = new WorkflowContext("run-chain-cascade-kept-alive", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["A"]);
        Assert.Equal(StageStatus.Skipped, report.StageStatuses["B"]);
        Assert.Equal(StageStatus.Skipped, report.StageStatuses["C"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["KeepAlive"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["AfterKeepAlive"]);
        Assert.Null(report.StopReason);
        Assert.True(report.Success);

        Assert.Contains(ctx.AuditLog, e => e.StageId == "B" && e.EventType == AuditEventType.StageBlocked && e.Details.Contains("cascading skip"));
        Assert.Contains(ctx.AuditLog, e => e.StageId == "C" && e.EventType == AuditEventType.StageBlocked && e.Details.Contains("cascading skip"));
    }
}
