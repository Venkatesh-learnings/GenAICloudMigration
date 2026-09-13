namespace Orchestrator.Core;

public class WorkflowGraph
{
    private readonly Dictionary<string, StageDefinition> _stages;
    private readonly object _gate = new();

    public WorkflowGraph(IEnumerable<StageDefinition> stages)
    {
        _stages = stages.ToDictionary(s => s.Id);
        Validate();
    }

    /// <summary>
    /// A snapshot, not a live view — the graph can grow at runtime when a stage
    /// decomposes its work into new stages, and the scheduler iterates this while
    /// that can happen.
    /// </summary>
    public IReadOnlyCollection<StageDefinition> Stages
    {
        get
        {
            lock (_gate)
            {
                return _stages.Values.ToList();
            }
        }
    }

    public StageDefinition Get(string stageId)
    {
        lock (_gate)
        {
            return _stages[stageId];
        }
    }

    public bool Contains(string stageId)
    {
        lock (_gate)
        {
            return _stages.ContainsKey(stageId);
        }
    }

    /// <summary>
    /// Adds stages discovered at runtime (e.g. one implementation stage per decomposed
    /// task), optionally making stages that already exist depend on them so a
    /// downstream barrier waits for the new work. Validated and committed atomically:
    /// if the additions would introduce an unknown dependency or a cycle, nothing is
    /// applied. Called from the scheduler between ticks, never concurrently with
    /// running stages.
    /// </summary>
    public void AddStages(
        IEnumerable<StageDefinition> newStages,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? additionalDependencies = null,
        Func<string, StageStatus>? statusOf = null)
    {
        var additions = newStages.ToList();
        if (additions.Count == 0 && additionalDependencies is null)
        {
            return;
        }

        lock (_gate)
        {
            var candidate = new Dictionary<string, StageDefinition>(_stages);

            foreach (var stage in additions)
            {
                if (!candidate.TryAdd(stage.Id, stage))
                {
                    throw new InvalidOperationException($"Stage '{stage.Id}' already exists in the graph.");
                }
            }

            var rewrites = new List<(StageDefinition Stage, IReadOnlyList<string> Original, IReadOnlyList<string> Updated)>();

            foreach (var (stageId, extraDeps) in additionalDependencies ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                if (!candidate.TryGetValue(stageId, out var target))
                {
                    throw new InvalidOperationException($"Cannot add dependencies to unknown stage '{stageId}'.");
                }

                // Rewriting the dependencies of a stage that already started would be a
                // lie about what it waited for, so only stages that haven't run yet.
                var status = statusOf?.Invoke(stageId) ?? StageStatus.Pending;
                if (status is not (StageStatus.Pending or StageStatus.Stale))
                {
                    throw new InvalidOperationException(
                        $"Cannot add dependencies to stage '{stageId}' because it is already {status}.");
                }

                rewrites.Add((target, target.DependsOn, [.. target.DependsOn, .. extraDeps.Except(target.DependsOn)]));
            }

            foreach (var (stage, _, updated) in rewrites)
            {
                stage.SetDependsOn(updated);
            }

            try
            {
                Validate(candidate);
            }
            catch
            {
                foreach (var (stage, original, _) in rewrites)
                {
                    stage.SetDependsOn(original);
                }
                throw;
            }

            foreach (var stage in additions)
            {
                _stages[stage.Id] = stage;
            }
        }
    }

    public IEnumerable<StageDefinition> GetDependents(string stageId)
    {
        lock (_gate)
        {
            return _stages.Values.Where(s => s.DependsOn.Contains(stageId)).ToList();
        }
    }

    /// <summary>All stages transitively downstream of <paramref name="stageId"/>, used for re-planning.</summary>
    public IReadOnlySet<string> GetDescendants(string stageId)
    {
        var result = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(stageId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dependent in GetDependents(current))
            {
                if (result.Add(dependent.Id))
                {
                    queue.Enqueue(dependent.Id);
                }
            }
        }

        return result;
    }

    private void Validate() => Validate(_stages);

    private static void Validate(IReadOnlyDictionary<string, StageDefinition> stages)
    {
        foreach (var stage in stages.Values)
        {
            foreach (var dep in stage.DependsOn)
            {
                if (!stages.ContainsKey(dep))
                {
                    throw new InvalidOperationException($"Stage '{stage.Id}' depends on unknown stage '{dep}'.");
                }
            }
        }

        DetectCycles(stages);
    }

    private static void DetectCycles(IReadOnlyDictionary<string, StageDefinition> stages)
    {
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new InvalidOperationException($"Cycle detected in workflow graph involving stage '{id}'.");
            }

            foreach (var dep in stages[id].DependsOn)
            {
                Visit(dep);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var id in stages.Keys)
        {
            Visit(id);
        }
    }
}
