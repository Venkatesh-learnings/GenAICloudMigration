using Orchestrator.Core;

namespace Orchestrator.Tests;

public class WorkflowEngineApprovalAndPolicyTests
{
    [Fact]
    public async Task RequiresApproval_WithApprovingProvider_Succeeds_AndAuditsRequestedThenGranted()
    {
        var agent = new NoOpAgent();
        var stage = StageBuilder.Make("Sensitive", agent, requiresApproval: true);
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-approve", "scenario");
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider);

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Sensitive"]);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, agent.CallCount);

        var stageEvents = ctx.AuditLog.Where(e => e.StageId == "Sensitive").Select(e => e.EventType).ToList();
        var requestedIdx = stageEvents.IndexOf(AuditEventType.ApprovalRequested);
        var grantedIdx = stageEvents.IndexOf(AuditEventType.ApprovalGranted);

        Assert.True(requestedIdx >= 0, "Expected an ApprovalRequested audit entry.");
        Assert.True(grantedIdx >= 0, "Expected an ApprovalGranted audit entry.");
        Assert.True(requestedIdx < grantedIdx, "ApprovalRequested must precede ApprovalGranted.");
    }

    [Fact]
    public async Task RequiresApproval_WithRejectingProvider_EndsBlocked_AndAgentNeverInvoked()
    {
        var agent = new NoOpAgent();
        var stage = StageBuilder.Make("Sensitive", agent, requiresApproval: true);
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-reject", "scenario");
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Rejected);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider);

        Assert.Equal(StageStatus.Blocked, report.StageStatuses["Sensitive"]);
        Assert.Equal(0, agent.CallCount);
        Assert.False(report.Success);

        Assert.Contains(ctx.AuditLog, e => e.StageId == "Sensitive" && e.EventType == AuditEventType.ApprovalDenied);
    }

    [Fact]
    public async Task SecuritySensitiveChangeRule_TriggersApproval_WhenInitialInputMentionsPassword()
    {
        var agent = new NoOpAgent();
        // The rule only escalates stages that actually change the product, so the stage under
        // test has to declare itself high-impact for the keyword match to matter.
        var stage = StageBuilder.Make("Implementation", agent, highImpact: true);
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-policy-hit", "scenario",
            new Dictionary<string, object> { ["requirement"] = "Reset the user password securely" });
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [new SecuritySensitiveChangeRule()]);

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Implementation"]);
        Assert.Equal(1, provider.CallCount);

        var requested = ctx.AuditLog.FirstOrDefault(e => e.StageId == "Implementation" && e.EventType == AuditEventType.ApprovalRequested);
        Assert.NotNull(requested);
        Assert.Contains("password", requested!.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecuritySensitiveChangeRule_DoesNotTriggerApproval_WhenNoSensitiveKeywords()
    {
        var agent = new NoOpAgent();
        var stage = StageBuilder.Make("Implementation", agent, highImpact: true);
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-policy-miss", "scenario",
            new Dictionary<string, object> { ["requirement"] = "Add a dark mode toggle" });
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [new SecuritySensitiveChangeRule()]);

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Implementation"]);
        Assert.Equal(0, provider.CallCount);
        Assert.DoesNotContain(ctx.AuditLog, e => e.EventType == AuditEventType.ApprovalRequested);
    }

    [Fact]
    public async Task PolicyDeny_FailsStage_TriggersSafeStop_AndStopReasonMentionsStage()
    {
        var agent = new NoOpAgent();
        var stage = StageBuilder.Make("Deployment", agent);
        var downstream = StageBuilder.Make("Release", new NoOpAgent(), dependsOn: ["Deployment"]);
        var graph = new WorkflowGraph([stage, downstream]);
        var ctx = new WorkflowContext("run-deny", "scenario");
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);
        var denyRule = new FakePolicyRule("BlockEverything", PolicyDecision.Deny, "this is never allowed");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [denyRule]);

        Assert.Equal(StageStatus.Failed, report.StageStatuses["Deployment"]);
        Assert.True(ctx.SafeStopRequested);
        Assert.False(report.Success);
        Assert.NotNull(report.StopReason);
        Assert.Contains("Deployment", report.StopReason);
        Assert.Equal(0, agent.CallCount); // denied before execution
    }

    [Fact]
    public async Task ConflictingPolicyRules_AllowAndRequireApproval_StillAsksForApproval()
    {
        var agent = new NoOpAgent();
        var stage = StageBuilder.Make("Implementation", agent);
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext("run-policy-conflict", "scenario");
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var allowRule = new FakePolicyRule("AlwaysAllow", PolicyDecision.Allow);
        var requireApprovalRule = new FakePolicyRule("AlwaysRequireApproval", PolicyDecision.RequireApproval, "needs a human look");

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [allowRule, requireApprovalRule]);

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Implementation"]);
        Assert.Equal(1, provider.CallCount);
        Assert.Contains(ctx.AuditLog, e => e.EventType == AuditEventType.ApprovalRequested && e.Details.Contains("needs a human look"));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task SecuritySensitiveChangeRule_IsScopedToHighImpactStages(bool highImpact, int expectedApprovalCount)
    {
        // Identical sensitive requirement text both times — the only difference is whether the
        // stage claims to change the product. A read-only analysis stage must not demand a
        // sign-off just because the requirement mentions auth.
        var agent = new NoOpAgent();
        var stage = StageBuilder.Make("Analysis", agent, highImpact: highImpact);
        var graph = new WorkflowGraph([stage]);
        var ctx = new WorkflowContext($"run-impact-{highImpact}", "scenario",
            new Dictionary<string, object> { ["requirement"] = "Rotate the payment provider secret token" });
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [new SecuritySensitiveChangeRule()]);

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Analysis"]);
        Assert.Equal(1, agent.CallCount);
        Assert.Equal(expectedApprovalCount, provider.CallCount);
        Assert.Equal(expectedApprovalCount, ctx.AuditLog.Count(e => e.EventType == AuditEventType.ApprovalRequested));
    }

    [Fact]
    public async Task DestructiveOperationRule_SeesWorkFromAnyStage_NotOnlyDesignOrImplementation()
    {
        // The producing stage has a name the rule knows nothing about (it could just as easily
        // have been created at runtime by a graph expansion); the rule still catches its output
        // because it scans every recorded result.
        var producer = new ProducingAgent("Task-7", "migration", "ALTER TABLE urls; DROP TABLE legacy_clicks;");
        var applyAgent = new NoOpAgent();
        var producerStage = StageBuilder.Make("Task-7", producer);
        var applyStage = StageBuilder.Make("Apply", applyAgent, dependsOn: ["Task-7"], highImpact: true);

        var graph = new WorkflowGraph([producerStage, applyStage]);
        var ctx = new WorkflowContext("run-destructive", "scenario");
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [new DestructiveOperationRule()]);

        Assert.True(report.Success);
        Assert.Equal(1, provider.CallCount);

        var requested = Assert.Single(ctx.AuditLog, e => e.EventType == AuditEventType.ApprovalRequested);
        Assert.Equal("Apply", requested.StageId);
        Assert.Contains("drop table", requested.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DestructiveOperationRule_IsAlsoScopedToHighImpactStages()
    {
        var producerStage = StageBuilder.Make("Task-7", new ProducingAgent("Task-7", "migration", "DROP TABLE legacy_clicks;"));
        var reportStage = StageBuilder.Make("Report", new NoOpAgent(), dependsOn: ["Task-7"]);

        var graph = new WorkflowGraph([producerStage, reportStage]);
        var ctx = new WorkflowContext("run-destructive-low-impact", "scenario");
        var provider = new ScriptedApprovalProvider(ApprovalDecision.Approved);

        var report = await new WorkflowEngine().RunAsync(graph, ctx, provider, policies: [new DestructiveOperationRule()]);

        Assert.True(report.Success);
        Assert.Equal(0, provider.CallCount);
        Assert.DoesNotContain(ctx.AuditLog, e => e.EventType == AuditEventType.ApprovalRequested);
    }

    [Fact]
    public async Task HardcodedCredentialRule_DeniesTheConsumingStage_AndTripsSafeStop()
    {
        // The producer is evaluated before it has produced anything, so it runs; the next stage
        // is evaluated against the credential now sitting in the run's results and is denied.
        var producerStage = StageBuilder.Make("Implementation",
            new ProducingAgent("Implementation", "code", "var password = \"hunter2\"; // TODO"));
        var reviewAgent = new NoOpAgent();
        var reviewStage = StageBuilder.Make("Review", reviewAgent, dependsOn: ["Implementation"]);

        var graph = new WorkflowGraph([producerStage, reviewStage]);
        var ctx = new WorkflowContext("run-credential", "scenario");

        var report = await new WorkflowEngine().RunAsync(
            graph, ctx, new AutoApprovalProvider(), policies: [new HardcodedCredentialRule()]);

        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Implementation"]);
        Assert.Equal(StageStatus.Failed, report.StageStatuses["Review"]);
        Assert.Equal(0, reviewAgent.CallCount);

        Assert.True(ctx.SafeStopRequested);
        Assert.False(report.Success);
        Assert.NotNull(report.StopReason);
        Assert.Contains("Review", report.StopReason);
        Assert.Contains("credential", report.StopReason, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(ctx.AuditLog, e => e.StageId == "Review" && e.EventType == AuditEventType.PolicyBlocked);
        Assert.Contains(ctx.AuditLog, e => e.EventType == AuditEventType.SafeStopTriggered);
    }

    [Fact]
    public async Task HardcodedCredentialRule_AllowsCleanWork()
    {
        var producerStage = StageBuilder.Make("Implementation",
            new ProducingAgent("Implementation", "code", "var password = configuration[\"Db:Password\"];"));
        var reviewStage = StageBuilder.Make("Review", new NoOpAgent(), dependsOn: ["Implementation"], highImpact: true);

        var graph = new WorkflowGraph([producerStage, reviewStage]);
        var ctx = new WorkflowContext("run-credential-clean", "scenario");

        var report = await new WorkflowEngine().RunAsync(
            graph, ctx, new AutoApprovalProvider(), policies: [new HardcodedCredentialRule()]);

        Assert.True(report.Success);
        Assert.False(ctx.SafeStopRequested);
        Assert.Equal(StageStatus.Succeeded, report.StageStatuses["Review"]);
    }
}
