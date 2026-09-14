using System.Text;
using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// The synchronization barrier for the implementation phase. The decomposition expands the
/// graph with one stage per task and makes this stage depend on all of them, so everything
/// downstream (testing, documentation, release readiness) waits for the whole derived
/// fan-out. No LLM call — it aggregates what the task stages actually produced.
/// </summary>
public class ImplementationAggregatorAgent(ArtifactWriter artifacts) : IStageAgent
{
    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var taskStageIds = stage.DependsOn.Where(id => id.StartsWith($"{stage.Id}:", StringComparison.Ordinal)).ToList();

        var completed = taskStageIds
            .Select(id => (StageId: id, Result: context.GetResult(id)))
            .Where(x => x.Result is not null)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Implementation summary");
        sb.AppendLine();
        sb.AppendLine($"{completed.Count} task(s) implemented.");
        sb.AppendLine();

        foreach (var (stageId, result) in completed)
        {
            sb.AppendLine($"## {result!.Outputs.GetValueOrDefault("taskId", stageId)}: {result.Outputs.GetValueOrDefault("taskTitle", "")}");
            sb.AppendLine();
            sb.AppendLine($"- Artifact: `{result.Outputs.GetValueOrDefault("generatedArtifact", "(none)")}`");
            sb.AppendLine($"- Rationale: {result.Rationale}");
            sb.AppendLine();
        }

        var path = artifacts.WriteText(context.RunId, stage.Id, "implementation-summary.md", sb.ToString());

        var outputs = new Dictionary<string, object>
        {
            ["tasksImplemented"] = completed.Count,
            ["generatedArtifact"] = path,
            // Flattened so the destructive-operation guardrail can scan what the task
            // stages actually generated, not just this summary.
            ["taskSummaries"] = string.Join(" | ", completed.Select(c => c.Result!.Summary))
        };

        return Task.FromResult(StageResult.Ok(
            $"Implementation phase complete: {completed.Count} task(s) aggregated.",
            "Deterministic fan-in over the task stages the decomposition created.",
            outputs,
            [path]));
    }
}
