using System.Text;
using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Converts the normalized requirement into actionable tasks with explicit dependencies
/// and sequencing, then turns that task DAG into real graph stages. This is what makes
/// the executed workflow differ per requirement instead of every run walking an identical
/// hardcoded pipeline: the tasks this stage derives literally become the implementation
/// stages the engine schedules, with the concurrency the dependencies imply.
/// </summary>
public class TaskDecompositionAgent(
    ILlmClient llm,
    ArtifactWriter artifacts,
    Func<WorkTask, IReadOnlyList<string>, StageDefinition> taskStageFactory,
    string designStageId = "Design",
    string barrierStageId = "Implementation") : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a senior engineer breaking a requirement into implementable tasks.
        Respond with ONLY a JSON object (no prose, no code fences):
        {"tasks": [{"id": "T1", "title": "...", "dependsOn": [], "rationale": "..."}]}
        Rules: ids are T1, T2, ...; dependsOn lists other task ids that must complete first
        and must form a DAG (no cycles); prefer 3-6 tasks; tasks that could genuinely be
        done in parallel must NOT depend on each other. Make titles concrete and actionable
        (name endpoints, types, or files where you can).
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var problemStatement = context.GetResult("RequirementAnalysis")?.Outputs.GetValueOrDefault("problemStatement", "").ToString()
            ?? context.InitialInput.GetValueOrDefault("requirement", "").ToString()
            ?? "";

        var (text, usedLlm) = await CompleteWithFallbackAsync(SystemPrompt, $"Requirement:\n{problemStatement}", () => "", ct);

        var tasks = usedLlm ? ParseTasks(text) : [];
        var source = "LLM decomposition";

        var validationError = string.Empty;
        var planUsable = tasks.Count > 0 && WorkTaskPlan.TryValidate(tasks, out validationError);

        if (!planUsable)
        {
            // Never build a graph from an invalid plan — fall back to the deterministic
            // clause-based decomposition rather than trusting unvalidated generated structure.
            var reason = tasks.Count == 0
                ? (usedLlm ? "model returned no usable tasks" : "LLM unavailable")
                : $"generated plan rejected ({validationError})";
            tasks = DeriveTasksOffline(problemStatement);
            source = $"deterministic offline decomposition ({reason})";

            if (!WorkTaskPlan.TryValidate(tasks, out var fallbackError))
            {
                return StageResult.Fail($"Could not produce a valid task plan: {fallbackError}");
            }
        }

        var waves = WorkTaskPlan.Sequence(tasks);
        var planMarkdown = RenderPlan(tasks, waves, source);
        var planPath = artifacts.WriteText(context.RunId, stage.Id, "task-plan.md", planMarkdown);

        var stageIdFor = tasks.ToDictionary(t => t.Id, t => $"{barrierStageId}:{t.Id}");

        var newStages = tasks
            .Select(task => taskStageFactory(
                task,
                [designStageId, .. task.DependsOn.Select(dep => stageIdFor[dep])]))
            .ToList();

        // The pre-existing barrier stage now fans the derived work back in, so everything
        // downstream (testing, documentation, release) still waits for all of it.
        context.RequestStages(
            newStages,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [barrierStageId] = newStages.Select(s => s.Id).ToList()
            });

        var outputs = new Dictionary<string, object>
        {
            ["taskCount"] = tasks.Count,
            ["taskIds"] = string.Join(", ", tasks.Select(t => t.Id)),
            ["taskTitles"] = string.Join(" | ", tasks.Select(t => t.Title)),
            ["dependencies"] = string.Join("; ", tasks.Select(t => $"{t.Id}<-[{string.Join(",", t.DependsOn)}]")),
            ["sequencing"] = string.Join(" -> ", waves.Select(w => $"({string.Join(",", w.Select(t => t.Id))})")),
            ["taskPlanArtifact"] = planPath
        };

        return StageResult.Ok(
            $"Decomposed into {tasks.Count} task(s) across {waves.Count} execution wave(s); expanded the graph with {newStages.Count} implementation stage(s).",
            $"Task plan via {source}; validated as a DAG before being turned into stages.",
            outputs,
            [planPath]);
    }

    private static List<WorkTask> ParseTasks(string text)
    {
        var root = TryParseLenientJson(text)?.RootElement;
        if (root is null || !root.Value.TryGetProperty("tasks", out var taskArray) ||
            taskArray.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        return taskArray.EnumerateArray()
            .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.Object)
            .Select(e => new WorkTask(
                GetString(e, "id"),
                GetString(e, "title"),
                GetStringArray(e, "dependsOn"),
                GetString(e, "rationale")))
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Title))
            .ToList();
    }

    private static readonly string[] FoundationSignals =
        ["data model", "schema", "database", "table", "entity", "persist", "storage", "store"];

    /// <summary>
    /// Deterministic decomposition used when no LLM is available (or its plan failed
    /// validation). It is genuinely derived from the requirement text — different
    /// requirements yield different task counts and shapes — by splitting the requirement
    /// into clauses, promoting any clause that looks foundational so the rest can depend
    /// on it, and adding an integration task that fans the parallel work back in.
    /// </summary>
    private static List<WorkTask> DeriveTasksOffline(string requirement)
    {
        var clauses = SplitIntoClauses(requirement);

        if (clauses.Count == 0)
        {
            return [new WorkTask("T1", $"Implement: {Truncate(requirement, 80)}", [], "Requirement could not be split into clauses.")];
        }

        var foundationIndex = clauses.FindIndex(c =>
            FoundationSignals.Any(signal => c.Contains(signal, StringComparison.OrdinalIgnoreCase)));
        if (foundationIndex < 0)
        {
            foundationIndex = 0;
        }

        var tasks = new List<WorkTask>
        {
            new("T1", Titleize(clauses[foundationIndex]), [], "Foundational work the other tasks build on.")
        };

        var parallel = clauses
            .Where((_, i) => i != foundationIndex)
            .Select((clause, i) => new WorkTask(
                $"T{i + 2}",
                Titleize(clause),
                ["T1"],
                "Independent of the other tasks at this level, so it can run in parallel."))
            .ToList();

        tasks.AddRange(parallel);

        if (parallel.Count > 1)
        {
            tasks.Add(new WorkTask(
                $"T{tasks.Count + 1}",
                "Integrate and validate the delivered pieces end to end",
                parallel.Select(t => t.Id).ToList(),
                "Fan-in: cannot start until every parallel task is done."));
        }

        return tasks;
    }

    private static List<string> SplitIntoClauses(string requirement) =>
        requirement
            .Split(new[] { ';', '.', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(sentence => sentence.Split(new[] { ", and " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .SelectMany(part => part.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(c => c.Trim().TrimStart(':').Trim())
            .Where(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4)
            .Take(6)
            .ToList();

    private static string Titleize(string clause)
    {
        var trimmed = Truncate(clause, 100);
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "...";

    private static string RenderPlan(IReadOnlyList<WorkTask> tasks, IReadOnlyList<IReadOnlyList<WorkTask>> waves, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Task decomposition");
        sb.AppendLine();
        sb.AppendLine($"Source: {source}");
        sb.AppendLine();
        sb.AppendLine("| Task | Title | Depends on |");
        sb.AppendLine("|---|---|---|");
        foreach (var task in tasks)
        {
            sb.AppendLine($"| {task.Id} | {task.Title} | {(task.DependsOn.Count == 0 ? "—" : string.Join(", ", task.DependsOn))} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Sequencing");
        sb.AppendLine();
        for (var i = 0; i < waves.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {string.Join(" ∥ ", waves[i].Select(t => $"{t.Id} ({t.Title})"))}");
        }

        return sb.ToString();
    }
}
