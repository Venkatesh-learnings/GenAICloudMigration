using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Turns the normalized problem statement (and, for brownfield changes, the
/// existing codebase context) into a concrete design: API/schema changes,
/// files to touch, and risks. Feeds the policy guardrails downstream, which
/// scan this stage's output for security-sensitive or destructive changes.
/// </summary>
public class DesignAgent(ILlmClient llm) : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a senior software architect. Given a normalized problem statement (and,
        optionally, context about an existing codebase this change must fit into), respond
        with ONLY a JSON object (no prose, no code fences):
        {"designSummary": "...", "apiChanges": ["..."], "dataModelChanges": ["..."], "filesToTouch": ["..."], "risks": ["..."]}
        Be specific about endpoints, fields, and file paths where possible.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var requirementResult = context.GetResult("RequirementAnalysis");
        var problemStatement = requirementResult?.Outputs.GetValueOrDefault("problemStatement", "").ToString() ?? "";
        var codebaseContext = context.InitialInput.GetValueOrDefault("codebaseContext", "").ToString() ?? "";

        var userPrompt = string.IsNullOrWhiteSpace(codebaseContext)
            ? $"Problem statement:\n{problemStatement}"
            : $"Problem statement:\n{problemStatement}\n\nExisting codebase context:\n{codebaseContext}";

        var (text, usedLlm) = await CompleteWithFallbackAsync(SystemPrompt, userPrompt, () => OfflineFallback(problemStatement), ct);

        var root = usedLlm ? TryParseLenientJson(text)?.RootElement : null;

        var designSummary = root is null ? $"Design for: {problemStatement}" : GetString(root.Value, "designSummary");
        var apiChanges = root is null ? [] : GetStringArray(root.Value, "apiChanges");
        var dataModelChanges = root is null ? [] : GetStringArray(root.Value, "dataModelChanges");
        var filesToTouch = root is null ? [] : GetStringArray(root.Value, "filesToTouch");
        var risks = root is null ? [] : GetStringArray(root.Value, "risks");

        var outputs = new Dictionary<string, object>
        {
            ["designSummary"] = designSummary,
            ["apiChanges"] = Joined(apiChanges),
            ["dataModelChanges"] = Joined(dataModelChanges),
            ["filesToTouch"] = Joined(filesToTouch),
            ["risks"] = Joined(risks),
        };

        var rationale = usedLlm
            ? "Design produced via LLM given the normalized requirement and codebase context."
            : "LLM unavailable; produced a minimal placeholder design offline.";

        return StageResult.Ok($"Design complete: {designSummary}", rationale, outputs);
    }

    private static string OfflineFallback(string problemStatement) =>
        $"{{\"designSummary\": {System.Text.Json.JsonSerializer.Serialize($"Straightforward implementation of: {problemStatement}")}, " +
        "\"apiChanges\": [], \"dataModelChanges\": [], \"filesToTouch\": [], \"risks\": []}";
}
