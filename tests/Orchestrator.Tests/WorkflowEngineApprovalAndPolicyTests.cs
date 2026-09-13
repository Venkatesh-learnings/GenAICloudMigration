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
        var stage = StageBuilder.Make("Implementation", agent);
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
        var stage = StageBuilder.Make("Implementation", agent);
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
}
