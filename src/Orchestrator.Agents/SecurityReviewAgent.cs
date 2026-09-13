using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// A conditional security review. Only runs when the change actually touches a
/// security-sensitive surface — that decision is the stage's entry gate, not something
/// this agent does, so a run where it was not applicable records it as Skipped rather
/// than as a review that passed.
/// </summary>
public class SecurityReviewAgent(ILlmClient llm, ArtifactWriter artifacts) : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are an application security reviewer. Given a requirement, a design and an
        implementation summary, respond with ONLY a JSON object (no prose, no fences):
        {"findings": ["..."], "mitigations": ["..."], "verdict": "pass" or "changes-required"}
        Focus on authentication, authorization, secret handling, injection, and data exposure.
        """;

    /// <summary>
    /// The entry-gate predicate this stage is wired with: does anything in this run's
    /// requirement, design, or implementation touch a security-sensitive surface?
    /// </summary>
    public static bool IsApplicable(WorkflowContext context)
    {
        string[] surfaces = ["auth", "security", "password", "payment", "secret", "token", "encryption", "credential", "permission"];

        var haystack = string.Join(" ", new[]
            {
                string.Join(" ", context.InitialInput.Values.Select(v => v?.ToString() ?? "")),
                Flatten(context.GetResult("Design")),
                Flatten(context.GetResult("Implementation"))
            })
            .ToLowerInvariant();

        return surfaces.Any(haystack.Contains);
    }

    private static string Flatten(StageResult? result) =>
        result is null ? "" : string.Join(" ", result.Outputs.Values.Select(v => v?.ToString() ?? ""));

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var requirement = context.GetResult("RequirementAnalysis")?.Outputs.GetValueOrDefault("problemStatement", "").ToString() ?? "";
        var design = context.GetResult("Design")?.Outputs.GetValueOrDefault("designSummary", "").ToString() ?? "";
        var implementation = context.GetResult("Implementation")?.Summary ?? "";

        var (text, usedLlm) = await CompleteWithFallbackAsync(
            SystemPrompt,
            $"Requirement:\n{requirement}\n\nDesign:\n{design}\n\nImplementation:\n{implementation}",
            () => "",
            ct);

        var root = usedLlm ? TryParseLenientJson(text)?.RootElement : null;

        var findings = root is null ? DefaultChecklist() : GetStringArray(root.Value, "findings");
        var mitigations = root is null ? [] : GetStringArray(root.Value, "mitigations");
        var verdict = root is null ? "manual-review-required" : GetString(root.Value, "verdict", "manual-review-required");

        var path = artifacts.WriteText(context.RunId, stage.Id, "security-review.md",
            $"# Security review\n\n**Verdict:** {verdict}\n\n## Findings\n{Bullets(findings)}\n\n## Mitigations\n{Bullets(mitigations)}\n");

        var outputs = new Dictionary<string, object>
        {
            ["verdict"] = verdict,
            ["findings"] = Joined(findings),
            ["mitigations"] = Joined(mitigations),
            ["reviewArtifact"] = path
        };

        return StageResult.Ok(
            $"Security review complete: {verdict}.",
            usedLlm
                ? "Reviewed by the LLM against the requirement, design and implementation."
                : "LLM unavailable; emitted the standard manual security checklist for a human reviewer.",
            outputs,
            [path]);
    }

    private static List<string> DefaultChecklist() =>
    [
        "Confirm every new endpoint enforces authentication and authorization.",
        "Confirm no secret, token or credential is logged or persisted in plaintext.",
        "Confirm all user-supplied input is validated and parameterized before reaching storage.",
        "Confirm error responses do not leak internal state."
    ];

    private static string Bullets(IReadOnlyList<string> items) =>
        items.Count == 0 ? "- (none)" : string.Join("\n", items.Select(i => $"- {i}"));
}
