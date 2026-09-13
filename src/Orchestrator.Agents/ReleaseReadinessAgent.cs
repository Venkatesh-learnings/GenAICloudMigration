using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Deterministic release checklist — no LLM call. Not every stage in a
/// governed SDLC pipeline should be a model call; a go/no-go gate is exactly
/// the kind of decision that should be auditable, reproducible code. A
/// "NO-GO" is a valid business outcome, not a stage failure, so this agent
/// always succeeds as a stage and carries the decision in its outputs.
/// </summary>
public class ReleaseReadinessAgent : IStageAgent
{
    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var blockers = new List<string>();

        var testingResult = context.GetResult("Testing");
        if (testingResult is null || !testingResult.Success)
        {
            blockers.Add("Testing stage did not succeed.");
        }
        else if (testingResult.Outputs.TryGetValue("realTestsPassed", out var passed) && passed.ToString() == "false")
        {
            blockers.Add("Real automated test run reported failures.");
        }

        var docResult = context.GetResult("Documentation");
        if (docResult is null || !docResult.Success)
        {
            blockers.Add("Documentation stage did not succeed.");
        }

        var deniedApprovals = context.AuditLog.Count(a => a.EventType == AuditEventType.ApprovalDenied);
        if (deniedApprovals > 0)
        {
            blockers.Add($"{deniedApprovals} approval checkpoint(s) were denied during this run.");
        }

        var policyBlocks = context.AuditLog.Count(a => a.EventType == AuditEventType.PolicyBlocked);
        if (policyBlocks > 0)
        {
            blockers.Add($"{policyBlocks} policy guardrail(s) denied a stage during this run.");
        }

        var decision = blockers.Count == 0 ? "GO" : "NO-GO";
        var outputs = new Dictionary<string, object>
        {
            ["decision"] = decision,
            ["blockers"] = blockers.Count == 0 ? "(none)" : string.Join("; ", blockers),
        };

        var rationale = blockers.Count == 0
            ? "All gating stages succeeded, all approvals were granted, and no policy guardrail denied a stage."
            : $"Blocked by: {string.Join("; ", blockers)}";

        return Task.FromResult(StageResult.Ok($"Release readiness decision: {decision}.", rationale, outputs));
    }
}
