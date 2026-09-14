using Orchestrator.Core;

namespace Orchestrator.Tests;

public class WorkflowEngineRetryRollbackTests
{
    [Fact]
    public async Task AgentFailsOnceThenSucceeds_EndsSucceeded_WithOneRetryRecorded()
    {
        var agent = new ScriptedAgent(
            StageResult.Fail("transient failure"),
            StageResult.Ok("worked on retry"));

        var stage = StageBuilder.Make("Flaky", agent, maxRetries: 2, retryBaseDelay: TimeSpan.FromMilliseconds(1));
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-retry-success", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Flaky"]);
        Assert.True(report.Success);
        Assert.Equal(2, agent.CallCount);
        Assert.Equal(1, report.Metrics.TotalRetries);
        Assert.Equal(0, report.Metrics.TotalRollbacks);
    }

    [Fact]
    public async Task AgentAlwaysFails_ExhaustsRetries_RollsBackExactlyOnce_AndBlocksDownstream()
    {
        var agent = new AlwaysFailAgent();
        var upstream = StageBuilder.Make("Upstream", agent, maxRetries: 1, retryBaseDelay: TimeSpan.FromMilliseconds(1));
        var downstream = StageBuilder.Make("Downstream", new NoOpAgent(), dependsOn: ["Upstream"]);

        var graph = new WorkflowGraph([upstream, downstream]);
        var ctx = new WorkflowContext("run-retry-exhausted", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.RolledBack, report.StageStatuses["Upstream"]);
        Assert.Equal(StageStatus.Blocked, report.StageStatuses["Downstream"]);
        Assert.False(report.Success);

        // maxRetries=1 -> attempts 0 and 1 both execute and fail.
        Assert.Equal(2, agent.ExecuteCount);
        Assert.Equal(1, agent.RollbackCount);
        Assert.Equal(1, report.Metrics.TotalRollbacks);
        Assert.Equal(1, report.Metrics.TotalRetries);
    }

    [Fact]
    public async Task AgentAlwaysFails_WithoutIRollbackable_StillEndsRolledBack_NoCrash()
    {
        var agent = new AlwaysFailNoRollbackAgent();
        var stage = StageBuilder.Make("Upstream", agent, maxRetries: 0, retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-no-rollback", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.RolledBack, report.StageStatuses["Upstream"]);
        Assert.False(report.Success);
        Assert.Equal(1, agent.ExecuteCount);
        // Rollback metric is still recorded even though the agent has no rollback logic to run.
        Assert.Equal(1, report.Metrics.TotalRollbacks);
    }

    [Fact]
    public async Task ExitGateFailure_IsTreatedAsFailure_AndRetried()
    {
        // Agent always "succeeds" per its own Success flag, but the exit gate rejects the first output.
        var callCount = 0;
        var agent = new ScriptedAgent(
            StageResult.Ok("bad output", outputs: new Dictionary<string, object> { ["quality"] = "low" }),
            StageResult.Ok("good output", outputs: new Dictionary<string, object> { ["quality"] = "high" }));

        var stage = StageBuilder.Make(
            "Gated",
            agent,
            maxRetries: 2,
            retryBaseDelay: TimeSpan.FromMilliseconds(1),
            exitGate: (_, result) =>
            {
                callCount++;
                return result.Outputs.TryGetValue("quality", out var q) && (string)q == "high";
            });

        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-exitgate", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Gated"]);
        Assert.Equal(2, agent.CallCount);
        Assert.Equal(2, callCount);
        Assert.Equal(1, report.Metrics.TotalRetries);
    }
}
