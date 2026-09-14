using Orchestrator.Core;

namespace Orchestrator.Tests;

public class WorkflowGraphTests
{
    [Fact]
    public void ValidGraph_Builds_And_ExposesStages()
    {
        var a = StageBuilder.Make("A", new NoOpAgent());
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);

        var graph = new WorkflowGraph([a, b]);

        Assert.Equal(2, graph.Stages.Count);
        Assert.Same(a, graph.Get("A"));
        Assert.Same(b, graph.Get("B"));
    }

    [Fact]
    public void UnknownDependency_Throws()
    {
        var a = StageBuilder.Make("A", new NoOpAgent(), dependsOn: ["DoesNotExist"]);

        var ex = Assert.Throws<InvalidOperationException>(() => new WorkflowGraph([a]));
        Assert.Contains("DoesNotExist", ex.Message);
    }

    [Fact]
    public void DirectCycle_Throws()
    {
        var a = StageBuilder.Make("A", new NoOpAgent(), dependsOn: ["B"]);
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);

        Assert.Throws<InvalidOperationException>(() => new WorkflowGraph([a, b]));
    }

    [Fact]
    public void IndirectCycle_Throws()
    {
        var a = StageBuilder.Make("A", new NoOpAgent(), dependsOn: ["C"]);
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["B"]);

        Assert.Throws<InvalidOperationException>(() => new WorkflowGraph([a, b, c]));
    }

    [Fact]
    public void GetDependents_ReturnsOnlyDirectDependents()
    {
        var a = StageBuilder.Make("A", new NoOpAgent());
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["B"]);

        var graph = new WorkflowGraph([a, b, c]);

        var dependentsOfA = graph.GetDependents("A").Select(s => s.Id).ToList();
        Assert.Equal(["B"], dependentsOfA);
    }

    [Fact]
    public void GetDescendants_ReturnsFullTransitiveClosure()
    {
        // A -> B -> C -> D (linear chain)
        var a = StageBuilder.Make("A", new NoOpAgent());
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["B"]);
        var d = StageBuilder.Make("D", new NoOpAgent(), dependsOn: ["C"]);

        var graph = new WorkflowGraph([a, b, c, d]);

        var descendantsOfA = graph.GetDescendants("A");

        Assert.Equal(new HashSet<string> { "B", "C", "D" }, descendantsOfA);
    }

    [Fact]
    public void GetDescendants_DiamondGraph_IncludesAllBranches()
    {
        // A -> B, A -> C, B -> D, C -> D
        var a = StageBuilder.Make("A", new NoOpAgent());
        var b = StageBuilder.Make("B", new NoOpAgent(), dependsOn: ["A"]);
        var c = StageBuilder.Make("C", new NoOpAgent(), dependsOn: ["A"]);
        var d = StageBuilder.Make("D", new NoOpAgent(), dependsOn: ["B", "C"]);

        var graph = new WorkflowGraph([a, b, c, d]);

        var descendantsOfA = graph.GetDescendants("A");

        Assert.Equal(new HashSet<string> { "B", "C", "D" }, descendantsOfA);
    }
}
