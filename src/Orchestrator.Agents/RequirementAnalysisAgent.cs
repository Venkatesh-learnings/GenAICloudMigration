using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Interprets the raw requirement text, calls out ambiguity explicitly
/// instead of silently guessing, and normalizes everything into a concrete
/// engineering problem statement that downstream stages consume.
/// </summary>
public class RequirementAnalysisAgent(ILlmClient llm) : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a senior software engineer performing requirement analysis for an SDLC
        orchestration pipeline. Given a raw feature requirement, respond with ONLY a JSON
        object (no prose, no code fences) with this shape:
        {"problemStatement": "...", "assumptions": ["..."], "ambiguities": ["..."], "clarifyingQuestions": ["..."]}
        Be concrete. If the requirement is well-specified, ambiguities and clarifyingQuestions may be empty arrays.
        If it is vague (e.g. mentions scale, performance, or behavior without numbers or specifics), you MUST list
        the ambiguity and a clarifying question for it.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var requirement = context.InitialInput.GetValueOrDefault("requirement", "").ToString() ?? "";
        var codebaseContext = context.InitialInput.GetValueOrDefault("codebaseContext", "").ToString() ?? "";

        var userPrompt = string.IsNullOrWhiteSpace(codebaseContext)
            ? $"Requirement:\n{requirement}"
            : $"Requirement:\n{requirement}\n\nExisting codebase context (brownfield change):\n{codebaseContext}";

        var (text, usedLlm) = await CompleteWithFallbackAsync(SystemPrompt, userPrompt, () => OfflineFallback(requirement), ct);

        // Only trust JSON parsing for a genuine LLM response. The offline fallback text is
        // deliberately not run back through the parser — its own heuristics decide ambiguity.
        var root = usedLlm ? TryParseLenientJson(text)?.RootElement : null;

        var problemStatement = root is null ? requirement.Trim() : GetString(root.Value, "problemStatement", requirement.Trim());
        var assumptions = root is null ? [] : GetStringArray(root.Value, "assumptions");
        var ambiguities = root is null ? HeuristicAmbiguities(requirement) : GetStringArray(root.Value, "ambiguities");
        var questions = root is null ? HeuristicQuestions(requirement) : GetStringArray(root.Value, "clarifyingQuestions");

        var outputs = new Dictionary<string, object>
        {
            ["problemStatement"] = problemStatement,
            ["assumptions"] = Joined(assumptions),
            ["ambiguities"] = Joined(ambiguities),
            ["clarifyingQuestions"] = Joined(questions),
        };

        var rationale = usedLlm
            ? "Normalized via LLM requirement analysis."
            : "LLM unavailable; normalized via deterministic offline heuristics (keyword-based ambiguity detection).";

        return StageResult.Ok(
            summary: $"Requirement normalized. {ambiguities.Count} ambiguity(ies) identified.",
            rationale: rationale,
            outputs: outputs);
    }

    private static string OfflineFallback(string requirement) =>
        $"{{\"problemStatement\": {System.Text.Json.JsonSerializer.Serialize(requirement.Trim())}, " +
        "\"assumptions\": [], \"ambiguities\": [], \"clarifyingQuestions\": []}";

    private static readonly string[] VagueTerms = ["handle", "high traffic", "some", "etc", "scalable", "fast", "robust", "as needed"];

    private static List<string> HeuristicAmbiguities(string requirement)
    {
        var lower = requirement.ToLowerInvariant();
        return VagueTerms.Where(lower.Contains)
            .Select(term => $"Requirement uses vague term '{term}' without a measurable target.")
            .ToList();
    }

    private static List<string> HeuristicQuestions(string requirement)
    {
        var lower = requirement.ToLowerInvariant();
        return VagueTerms.Where(lower.Contains)
            .Select(term => $"What specific, measurable target should replace '{term}'?")
            .ToList();
    }
}
