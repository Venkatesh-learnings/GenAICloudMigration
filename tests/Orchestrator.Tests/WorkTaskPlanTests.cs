using Orchestrator.Agents;

namespace Orchestrator.Tests;

/// <summary>
/// A decomposed plan is generated, not authored, so it is not trusted: these tests pin down
/// both the validation that rejects an unusable plan and the wave sequencing the engine later
/// reproduces as graph stages.
/// </summary>
public class WorkTaskPlanTests
{
    private static WorkTask Task(string id, params string[] dependsOn) =>
        new(id, $"Do {id}", dependsOn, $"because {id}");

    [Fact]
    public void TryValidate_AcceptsADiamondPlan()
    {
        var tasks = new[] { Task("T1"), Task("T2", "T1"), Task("T3", "T1"), Task("T4", "T2", "T3") };

        Assert.True(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryValidate_RejectsEmptyPlan()
    {
        Assert.False(WorkTaskPlan.TryValidate([], out var error));
        Assert.Contains("no tasks", error);
    }

    [Fact]
    public void TryValidate_RejectsDuplicateIds()
    {
        var tasks = new[] { Task("T1"), Task("T1") };

        Assert.False(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Contains("T1", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_RejectsEmptyId(string id)
    {
        var tasks = new[] { Task("T1"), Task(id) };

        Assert.False(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Contains("empty task id", error);
    }

    [Fact]
    public void TryValidate_RejectsDependencyOnUnknownTask()
    {
        var tasks = new[] { Task("T1"), Task("T2", "T99") };

        Assert.False(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Contains("T99", error);
        Assert.Contains("unknown", error);
    }

    [Fact]
    public void TryValidate_RejectsDirectCycle()
    {
        var tasks = new[] { Task("T1", "T2"), Task("T2", "T1") };

        Assert.False(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Contains("cycle", error);
    }

    [Fact]
    public void TryValidate_RejectsIndirectCycle()
    {
        var tasks = new[] { Task("T1", "T3"), Task("T2", "T1"), Task("T3", "T2") };

        Assert.False(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Contains("cycle", error);
    }

    [Fact]
    public void TryValidate_RejectsSelfDependency()
    {
        var tasks = new[] { Task("T1", "T1") };

        Assert.False(WorkTaskPlan.TryValidate(tasks, out var error));
        Assert.Contains("cycle", error);
    }

    [Fact]
    public void Sequence_DiamondPlan_ProducesThreeWaves_WithTheParallelPairInTheMiddle()
    {
        var tasks = new[] { Task("T1"), Task("T2", "T1"), Task("T3", "T1"), Task("T4", "T2", "T3") };

        var waves = WorkTaskPlan.Sequence(tasks);

        Assert.Equal(3, waves.Count);
        Assert.Equal(["T1"], waves[0].Select(t => t.Id));
        Assert.Equal(["T2", "T3"], waves[1].Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(["T4"], waves[2].Select(t => t.Id));
    }

    [Fact]
    public void Sequence_IndependentTasks_AllLandInOneWave()
    {
        var tasks = new[] { Task("A"), Task("B"), Task("C") };

        var wave = Assert.Single(WorkTaskPlan.Sequence(tasks));

        Assert.Equal(3, wave.Count);
    }

    [Fact]
    public void Sequence_LinearChain_ProducesOneTaskPerWave()
    {
        var tasks = new[] { Task("T1"), Task("T2", "T1"), Task("T3", "T2") };

        var waves = WorkTaskPlan.Sequence(tasks);

        Assert.Equal(3, waves.Count);
        Assert.All(waves, wave => Assert.Single(wave));
        Assert.Equal(["T1", "T2", "T3"], waves.Select(w => w[0].Id));
    }

    [Fact]
    public void Sequence_PlacesEveryTaskAfterAllOfItsDependencies()
    {
        var tasks = new[] { Task("T4", "T2", "T3"), Task("T3", "T1"), Task("T1"), Task("T2", "T1") };

        var waves = WorkTaskPlan.Sequence(tasks);

        var waveOf = waves
            .SelectMany((wave, index) => wave.Select(t => (t.Id, Index: index)))
            .ToDictionary(x => x.Id, x => x.Index);

        Assert.Equal(tasks.Length, waveOf.Count);
        foreach (var task in tasks)
        {
            foreach (var dep in task.DependsOn)
            {
                Assert.True(waveOf[dep] < waveOf[task.Id], $"'{task.Id}' must be sequenced after '{dep}'.");
            }
        }
    }
}
