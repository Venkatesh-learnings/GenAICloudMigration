namespace Orchestrator.Core;

public class WorkflowGraph
{
    private readonly Dictionary<string, StageDefinition> _stages;

    public WorkflowGraph(IEnumerable<StageDefinition> stages)
    {
        _stages = stages.ToDictionary(s => s.Id);
        Validate();
    }

    public IReadOnlyCollection<StageDefinition> Stages => _stages.Values;

    public StageDefinition Get(string stageId) => _stages[stageId];

    public IEnumerable<StageDefinition> GetDependents(string stageId) =>
        _stages.Values.Where(s => s.DependsOn.Contains(stageId));

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

    private void Validate()
    {
        foreach (var stage in _stages.Values)
        {
            foreach (var dep in stage.DependsOn)
            {
                if (!_stages.ContainsKey(dep))
                {
                    throw new InvalidOperationException($"Stage '{stage.Id}' depends on unknown stage '{dep}'.");
                }
            }
        }

        DetectCycles();
    }

    private void DetectCycles()
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

            foreach (var dep in _stages[id].DependsOn)
            {
                Visit(dep);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var id in _stages.Keys)
        {
            Visit(id);
        }
    }
}
