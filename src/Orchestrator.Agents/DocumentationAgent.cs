using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>Summarizes the full run (requirement through testing) into a human-readable change doc.</summary>
public class DocumentationAgent(ILlmClient llm, ArtifactWriter artifacts) : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a technical writer. Given a summary of a completed engineering change
        (requirement, design, implementation, and test results), write a concise Markdown
        change document with sections: Summary, What Changed, Risks & Trade-offs, How It Was Tested.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var problemStatement = context.GetResult("RequirementAnalysis")?.Outputs.GetValueOrDefault("problemStatement", "").ToString() ?? "";
        var designSummary = context.GetResult("Design")?.Outputs.GetValueOrDefault("designSummary", "").ToString() ?? "";
        var risks = context.GetResult("Design")?.Outputs.GetValueOrDefault("risks", "").ToString() ?? "";
        var testSummary = context.GetResult("Testing")?.Summary ?? "";

        var userPrompt = $"Requirement: {problemStatement}\nDesign: {designSummary}\nRisks: {risks}\nTest result: {testSummary}";
        var (text, usedLlm) = await CompleteWithFallbackAsync(SystemPrompt, userPrompt, () => OfflineFallback(problemStatement, designSummary, testSummary), ct);

        var path = artifacts.WriteText(context.RunId, stage.Id, "CHANGE.md", text);
        var outputs = new Dictionary<string, object> { ["docArtifact"] = path };
        var rationale = usedLlm ? "Change documentation generated via LLM." : "LLM unavailable; wrote a templated change doc offline.";

        return StageResult.Ok($"Documentation written to {path}", rationale, outputs, [path]);
    }

    private static string OfflineFallback(string problemStatement, string designSummary, string testSummary) =>
        $"# Change Summary (offline fallback)\n\n## Summary\n{problemStatement}\n\n## What Changed\n{designSummary}\n\n" +
        $"## How It Was Tested\n{testSummary}\n";
}
