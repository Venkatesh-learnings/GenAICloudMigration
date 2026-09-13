using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Brownfield codebase reasoning. Scans the real repository first, then reasons about which
/// modules, APIs and data flows the requirement actually touches. The facts it reasons over
/// are read off disk, so this stage's answer changes when the code changes — unlike a
/// hand-written context paragraph, which is just an assertion about the code.
/// </summary>
public class CodebaseAnalysisAgent(ILlmClient llm, ArtifactWriter artifacts, string solutionRoot)
    : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a senior engineer assessing the blast radius of a change in an existing
        codebase. You are given a requirement and a factual inventory of the repository
        (projects, their references, HTTP routes, entity sets, and the files whose contents
        most relate to the requirement). Respond with ONLY a JSON object (no prose, no fences):
        {"impactedModules": ["..."], "impactedApis": ["..."], "dataFlows": ["..."], "regressionRisks": ["..."]}
        Ground every entry in the inventory you were given — name real projects, routes and
        types. Do not invent files that were not listed.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var requirement = context.GetResult("RequirementAnalysis")?.Outputs.GetValueOrDefault("problemStatement", "").ToString()
            ?? context.InitialInput.GetValueOrDefault("requirement", "").ToString()
            ?? "";

        var facts = new CodebaseScanner(solutionRoot).Scan(requirement);
        var inventory = CodebaseScanner.Render(facts);
        var inventoryPath = artifacts.WriteText(context.RunId, stage.Id, "codebase-inventory.md",
            $"# Codebase inventory (scanned)\n\n```\n{inventory}\n```\n");

        if (facts.IsEmpty)
        {
            return StageResult.Ok(
                "No existing codebase found; nothing to analyze.",
                "Scanned the solution root and found no projects — the change is effectively greenfield.",
                new Dictionary<string, object>
                {
                    ["projectsScanned"] = 0,
                    ["impactedModules"] = "(none — greenfield)",
                    ["inventoryArtifact"] = inventoryPath
                },
                [inventoryPath]);
        }

        var (text, usedLlm) = await CompleteWithFallbackAsync(
            SystemPrompt,
            $"Requirement:\n{requirement}\n\nRepository inventory:\n{inventory}",
            () => "",
            ct);

        var root = usedLlm ? TryParseLenientJson(text)?.RootElement : null;

        // Offline (or on unparseable output) the impact assessment is derived directly from
        // the scan: the files that actually mention the requirement's terms, and the projects
        // that own them. Less insightful than the model's reading, but still grounded in the
        // real repository rather than invented.
        var impactedModules = root is null
            ? DeriveImpactedModules(facts)
            : GetStringArray(root.Value, "impactedModules");
        var impactedApis = root is null
            ? facts.Projects.SelectMany(p => p.HttpRoutes).Take(10).ToList()
            : GetStringArray(root.Value, "impactedApis");
        var dataFlows = root is null
            ? facts.Projects.SelectMany(p => p.EntitySets).Take(10).ToList()
            : GetStringArray(root.Value, "dataFlows");
        var regressionRisks = root is null
            ? DeriveRegressionRisks(facts)
            : GetStringArray(root.Value, "regressionRisks");

        var outputs = new Dictionary<string, object>
        {
            ["projectsScanned"] = facts.Projects.Count,
            ["impactedModules"] = Joined(impactedModules),
            ["impactedApis"] = Joined(impactedApis),
            ["dataFlows"] = Joined(dataFlows),
            ["regressionRisks"] = Joined(regressionRisks),
            ["relevantFiles"] = Joined(facts.RelevantFiles),
            ["inventoryArtifact"] = inventoryPath
        };

        var rationale = usedLlm
            ? $"Scanned {facts.Projects.Count} project(s) off disk, then reasoned over that inventory with the LLM."
            : $"Scanned {facts.Projects.Count} project(s) off disk; LLM unavailable, so impact was derived directly from the scan (keyword-relevant files and the projects owning them).";

        return StageResult.Ok(
            $"Codebase analyzed: {facts.Projects.Count} project(s), {facts.RelevantFiles.Count} file(s) related to this requirement.",
            rationale,
            outputs,
            [inventoryPath]);
    }

    private static List<string> DeriveImpactedModules(CodebaseFacts facts)
    {
        var owners = facts.RelevantFiles
            .Select(entry => entry.Split(' ')[0])
            .Select(path => path.Split(Path.DirectorySeparatorChar, '/').Skip(1).FirstOrDefault())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Distinct()
            .ToList();

        return owners.Count > 0
            ? owners!
            : facts.Projects.Select(p => p.Name).ToList();
    }

    private static List<string> DeriveRegressionRisks(CodebaseFacts facts)
    {
        var risks = new List<string>();

        var routeOwners = facts.Projects.Where(p => p.HttpRoutes.Count > 0).ToList();
        foreach (var project in routeOwners)
        {
            risks.Add($"{project.Name} exposes {project.HttpRoutes.Count} route(s); changing shared handlers risks breaking existing callers.");
        }

        var dataOwners = facts.Projects.Where(p => p.EntitySets.Count > 0).ToList();
        foreach (var project in dataOwners)
        {
            risks.Add($"{project.Name} owns persisted entity sets ({string.Join(", ", project.EntitySets)}); schema changes need a migration and a backfill plan.");
        }

        return risks;
    }
}
