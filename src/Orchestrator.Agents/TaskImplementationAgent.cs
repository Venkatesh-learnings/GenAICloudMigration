using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Implements one decomposed task. One of these exists per task the decomposition
/// produced, so the implementation phase is as wide (and as parallel) as the requirement
/// actually warrants. Output goes to the run's artifact directory for human review rather
/// than into the product source tree.
/// </summary>
public class TaskImplementationAgent(ILlmClient llm, ArtifactWriter artifacts, WorkTask task)
    : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a senior software engineer implementing ONE specific task from an agreed
        design. Produce the code change for just that task as a Markdown patch note with
        ```csharp fenced code blocks, followed by a short rationale. Stay strictly within
        the scope of the task you are given; do not implement the other tasks.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var designSummary = context.GetResult("Design")?.Outputs.GetValueOrDefault("designSummary", "").ToString() ?? "";
        var codebaseFacts = context.GetResult("CodebaseAnalysis")?.Outputs.GetValueOrDefault("impactedModules", "").ToString() ?? "";

        var userPrompt = $"""
            Task {task.Id}: {task.Title}
            Why this task: {task.Rationale}

            Agreed design:
            {designSummary}

            Existing code this must fit into:
            {(string.IsNullOrWhiteSpace(codebaseFacts) ? "(greenfield — no existing code)" : codebaseFacts)}
            """;

        var (text, usedLlm) = await CompleteWithFallbackAsync(
            SystemPrompt, userPrompt, () => OfflineFallback(designSummary), ct);

        var path = artifacts.WriteText(context.RunId, stage.Id, $"{task.Id}-implementation.md", text);

        var outputs = new Dictionary<string, object>
        {
            ["taskId"] = task.Id,
            ["taskTitle"] = task.Title,
            ["generatedArtifact"] = path
        };

        var rationale = usedLlm
            ? $"Implemented task {task.Id} in isolation from the agreed design; written to the artifact directory for human review."
            : $"LLM unavailable; wrote a scoped implementation skeleton for task {task.Id} offline.";

        return StageResult.Ok($"Task {task.Id} implemented: {task.Title}", rationale, outputs, [path]);
    }

    private string OfflineFallback(string designSummary) => $"""
        # {task.Id}: {task.Title}

        _Generated without an LLM (offline fallback) — this is a scoped work order for a
        human implementer, not finished code._

        ## Scope
        {task.Title}

        ## Why
        {task.Rationale}

        ## Depends on
        {(task.DependsOn.Count == 0 ? "Nothing — this task can start immediately." : string.Join(", ", task.DependsOn))}

        ## Design context
        {(string.IsNullOrWhiteSpace(designSummary) ? "(none recorded)" : designSummary)}

        ## Definition of done
        - [ ] Change implemented within this task's scope only
        - [ ] Unit tests covering the happy path and the failure paths
        - [ ] No regression in the existing suite (`dotnet test`)
        """;
}
