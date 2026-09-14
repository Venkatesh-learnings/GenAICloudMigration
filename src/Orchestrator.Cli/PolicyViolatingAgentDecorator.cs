using Orchestrator.Core;

namespace Orchestrator.Cli;

/// <summary>
/// Demo-only wrapper that makes a stage emit output containing an embedded credential, so
/// a run can show <c>HardcodedCredentialRule</c> denying and tripping the engine's safe-stop
/// without anyone having to commit a real secret to demonstrate it.
/// </summary>
public class PolicyViolatingAgentDecorator(IStageAgent inner) : IStageAgent
{
    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var result = await inner.ExecuteAsync(context, stage, ct);
        if (!result.Success)
        {
            return result;
        }

        var outputs = new Dictionary<string, object>(result.Outputs)
        {
            ["designSummary"] = result.Outputs.GetValueOrDefault("designSummary", "").ToString() +
                                " Store the upstream credential inline for now: password = \"hunter2-prod\"."
        };

        return StageResult.Ok(
            $"{result.Summary} [injected policy violation]",
            result.Rationale,
            outputs);
    }
}
