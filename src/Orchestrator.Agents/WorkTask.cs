namespace Orchestrator.Agents;

/// <summary>A single actionable unit of work produced by decomposing a requirement.</summary>
public record WorkTask(string Id, string Title, IReadOnlyList<string> DependsOn, string Rationale);

public static class WorkTaskPlan
{
    /// <summary>
    /// Validates a decomposed task list as a DAG. Generated plans are not trusted: a task
    /// referencing a dependency that doesn't exist, or a cycle, means the decomposition is
    /// unusable and the caller should fall back rather than build a graph from it.
    /// </summary>
    public static bool TryValidate(IReadOnlyList<WorkTask> tasks, out string error)
    {
        error = string.Empty;

        if (tasks.Count == 0)
        {
            error = "decomposition produced no tasks";
            return false;
        }

        var byId = new Dictionary<string, WorkTask>();
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) || !byId.TryAdd(task.Id, task))
            {
                error = $"duplicate or empty task id '{task.Id}'";
                return false;
            }
        }

        foreach (var task in tasks)
        {
            foreach (var dep in task.DependsOn)
            {
                if (!byId.ContainsKey(dep))
                {
                    error = $"task '{task.Id}' depends on unknown task '{dep}'";
                    return false;
                }
            }
        }

        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        var cyclic = false;

        void Visit(string id)
        {
            if (cyclic || visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                cyclic = true;
                return;
            }

            foreach (var dep in byId[id].DependsOn)
            {
                Visit(dep);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var task in tasks)
        {
            Visit(task.Id);
            if (cyclic)
            {
                error = $"dependency cycle detected involving task '{task.Id}'";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Groups tasks into execution waves: everything in wave N can run concurrently once
    /// wave N-1 is done. This is the "sequencing" half of decomposition, and it's what the
    /// engine reproduces naturally once the tasks become graph stages.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<WorkTask>> Sequence(IReadOnlyList<WorkTask> tasks)
    {
        var remaining = tasks.ToDictionary(t => t.Id);
        var placed = new HashSet<string>();
        var waves = new List<IReadOnlyList<WorkTask>>();

        while (remaining.Count > 0)
        {
            var wave = remaining.Values
                .Where(t => t.DependsOn.All(placed.Contains))
                .OrderBy(t => t.Id, StringComparer.Ordinal)
                .ToList();

            if (wave.Count == 0)
            {
                break; // unreachable for a validated DAG; guards against an infinite loop
            }

            foreach (var task in wave)
            {
                placed.Add(task.Id);
                remaining.Remove(task.Id);
            }

            waves.Add(wave);
        }

        return waves;
    }
}
