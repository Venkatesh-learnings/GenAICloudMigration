using Orchestrator.Core;

namespace Orchestrator.Tests;

/// <summary>
/// Covers <see cref="DependencyRule"/>. The default (<see cref="DependencyRule.AllSucceeded"/>)
/// cascade-on-skip behaviour is already covered by <see cref="WorkflowEngineDeadlockTests"/>;
/// these tests pin down what <see cref="DependencyRule.AllSettled"/> relaxes — a Skipped
/// dependency counts as satisfied and does NOT cascade — and, just as importantly, what it does
/// not relax: a dependency that ended Failed, Blocked or RolledBack still blocks.
/// </summary>
public class WorkflowEngineDependencyRuleTests
{
    [Fact]
    public async Task AllSettled_DependentOfSkippedStage_StillRuns_AndRunSucceeds()
    {
        var optional = StageBuilder.Make("OptionalReview", new NoOpAgent(), entryGate: _ => false);
        var gateAgent = new NoOpAgent();
        var releaseGate = StageBuilder.Make("ReleaseGate", gateAgent,
            dependsOn: ["OptionalReview"], dependencyRule: DependencyRule.AllSettled);

        var graph = new WorkflowGraph([optional, releaseGate]);
        var ctx = new WorkflowContext("run-all-settled-skip", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["OptionalReview"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["ReleaseGate"]);
        Assert.Equal(1, gateAgent.CallCount);
        Assert.Null(report.StopReason);
        Assert.True(report.Success);

        // The skip must not have been cascaded into the AllSettled stage.
        Assert.DoesNotContain(ctx.AuditLog,
            e => e.StageId == "ReleaseGate" && e.Details.Contains("cascading skip"));
    }

    [Fact]
    public async Task SiblingsOfTheSameSkippedStage_DivergeByDependencyRule()
    {
        // One upstream skip, two dependents, different rules: the contrast in a single graph is
        // the whole point of the feature.
        var optional = StageBuilder.Make("OptionalReview", new NoOpAgent(), entryGate: _ => false);
        var strictAgent = new NoOpAgent();
        var relaxedAgent = new NoOpAgent();
        var strict = StageBuilder.Make("StrictDependent", strictAgent, dependsOn: ["OptionalReview"]);
        var relaxed = StageBuilder.Make("RelaxedDependent", relaxedAgent,
            dependsOn: ["OptionalReview"], dependencyRule: DependencyRule.AllSettled);

        var graph = new WorkflowGraph([optional, strict, relaxed]);
        var ctx = new WorkflowContext("run-rule-contrast", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["StrictDependent"]);
        Assert.Equal(0, strictAgent.CallCount);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["RelaxedDependent"]);
        Assert.Equal(1, relaxedAgent.CallCount);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task AllSettled_WithOneSkippedAndOneSucceededDependency_StillRuns()
    {
        var skipped = StageBuilder.Make("SkippedDep", new NoOpAgent(), entryGate: _ => false);
        var succeeded = StageBuilder.Make("SucceededDep", new NoOpAgent());
        var gateAgent = new NoOpAgent();
        var gate = StageBuilder.Make("Gate", gateAgent,
            dependsOn: ["SkippedDep", "SucceededDep"], dependencyRule: DependencyRule.AllSettled);

        var graph = new WorkflowGraph([skipped, succeeded, gate]);
        var ctx = new WorkflowContext("run-all-settled-mixed", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.Skipped, report.StageStatuses["SkippedDep"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["SucceededDep"]);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Gate"]);
        Assert.Equal(1, gateAgent.CallCount);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task AllSettled_DependentOfRolledBackStage_IsStillBlocked()
    {
        // AllSettled relaxes "was deliberately skipped", not "tried and failed".
        var failing = new AlwaysFailAgent();
        var upstream = StageBuilder.Make("Upstream", failing, maxRetries: 0);
        var gateAgent = new NoOpAgent();
        var gate = StageBuilder.Make("Gate", gateAgent,
            dependsOn: ["Upstream"], dependencyRule: DependencyRule.AllSettled);

        var graph = new WorkflowGraph([upstream, gate]);
        var ctx = new WorkflowContext("run-all-settled-failure", "scenario");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, new AutoApprovalProvider());

        Assert.Equal(StageStatus.RolledBack, report.StageStatuses["Upstream"]);
        Assert.Equal(StageStatus.Blocked, report.StageStatuses["Gate"]);
        Assert.Equal(0, gateAgent.CallCount);
        Assert.False(report.Success);

        Assert.Contains(ctx.AuditLog,
            e => e.StageId == "Gate" && e.EventType == AuditEventType.StageBlocked && e.Details.Contains("Upstream"));
    }

    [Fact]
    public async Task AllSettled_DependentOfStageBlockedByRejectedApproval_IsStillBlocked()
    {
        var upstream = StageBuilder.Make("Upstream", new NoOpAgent(), requiresApproval: true);
        var gateAgent = new NoOpAgent();
        var gate = StageBuilder.Make("Gate", gateAgent,
            dependsOn: ["Upstream"], dependencyRule: DependencyRule.AllSettled);

        var graph = new WorkflowGraph([upstream, gate]);
        var ctx = new WorkflowContext("run-all-settled-blocked", "scenario");

        var report = await new WorkflowEngine().RunAsync(
            graph, ctx, new ScriptedApprovalProvider(ApprovalDecision.Rejected));

        Assert.Equal(StageStatus.Blocked, report.StageStatuses["Upstream"]);
        Assert.Equal(StageStatus.Blocked, report.StageStatuses["Gate"]);
        Assert.Equal(0, gateAgent.CallCount);
        Assert.False(report.Success);
    }
}
