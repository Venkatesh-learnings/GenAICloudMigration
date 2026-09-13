using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Generates the actual engineering output (code) for the design and writes
/// it to the run's artifact directory. Deliberately does NOT mutate the real
/// product source tree directly — see docs/architecture.md for why: an
/// autonomous agent editing a live codebase without a human merge gate is
/// exactly the kind of high-impact action this system's approval checkpoints
/// exist to control. In this prototype the generated diff is the artifact a
/// human reviews and applies; a production version would open it as a PR.
/// </summary>
public class ImplementationAgent(ILlmClient llm, ArtifactWriter artifacts) : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a senior software engineer implementing a previously agreed design.
        Given the design, produce the actual code change as a unified-diff-style or
        full-file code listing in Markdown (```csharp fenced blocks), followed by a short
        plain-text rationale. This is not JSON - write it as a normal engineering patch note.
        Be concrete and complete for the files you choose to show.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var designResult = context.GetResult("Design");
        var designSummary = designResult?.Outputs.GetValueOrDefault("designSummary", "").ToString() ?? "";
        var filesToTouch = designResult?.Outputs.GetValueOrDefault("filesToTouch", "").ToString() ?? "";

        var userPrompt = $"Design summary:\n{designSummary}\n\nFiles to touch:\n{filesToTouch}";

        var (text, usedLlm) = await CompleteWithFallbackAsync(SystemPrompt, userPrompt, () => OfflineFallback(designSummary), ct);

        var path = artifacts.WriteText(context.RunId, stage.Id, "implementation.md", text);

        var outputs = new Dictionary<string, object>
        {
            ["designSummary"] = designSummary,
            ["generatedArtifact"] = path,
        };

        var rationale = usedLlm
            ? "Code change generated via LLM from the approved design; written to the run's artifact directory for human review, not applied directly to the product source tree."
            : "LLM unavailable; wrote a placeholder implementation note offline.";

        return StageResult.Ok($"Implementation artifact generated at {path}", rationale, outputs, artifacts: [path]);
    }

    private static string OfflineFallback(string designSummary) =>
        $"# Implementation (offline fallback)\n\nLLM was unavailable. Manual implementation required for:\n\n{designSummary}\n";
}
