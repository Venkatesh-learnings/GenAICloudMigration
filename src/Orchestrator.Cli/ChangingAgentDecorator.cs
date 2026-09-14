using Orchestrator.Core;

namespace Orchestrator.Cli;

/// <summary>
/// Demo-only wrapper that returns a genuinely different result on its second
/// and later invocations, so a --simulate-replan run can prove dynamic
/// re-planning against a real hash change instead of asserting it happened.
/// </summary>
public class ChangingAgentDecorator(IStageAgent inner) : IStageAgent
{
    private int _invocations;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var invocation = Interlocked.Increment(ref _invocations);
        var result = await inner.ExecuteAsync(context, stage, ct);

        if (invocation == 1 || !result.Success)
        {
            return result;
        }

        var outputs = new Dictionary<string, object>(result.Outputs)
        {
            ["problemStatement"] = result.Outputs.GetValueOrDefault("problemStatement", "") + " [REVISED: scope now explicitly includes rate limiting]"
        };

        return StageResult.Ok(
            $"{result.Summary} (revised on re-analysis, invocation #{invocation})",
            "Upstream requirement was revised after the first pass; re-analyzed with the new scope.",
            outputs);
    }
}
