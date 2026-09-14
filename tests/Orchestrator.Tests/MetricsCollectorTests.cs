using Orchestrator.Core;

namespace Orchestrator.Tests;

public class MetricsCollectorTests
{
    [Fact]
    public void Snapshot_ComputesSuccessRate_AcrossStages()
    {
        var metrics = new MetricsCollector();

        metrics.RecordStageOutcome("A", true, TimeSpan.FromMilliseconds(10));
        metrics.RecordStageOutcome("B", true, TimeSpan.FromMilliseconds(20));
        metrics.RecordStageOutcome("C", false, TimeSpan.FromMilliseconds(5));

        var snapshot = metrics.Snapshot();

        Assert.Equal(3, snapshot.TotalStages);
        Assert.Equal(2, snapshot.SucceededStages);
        Assert.Equal(1, snapshot.FailedStages);
        Assert.Equal(2.0 / 3.0, snapshot.SuccessRate, precision: 5);
    }

    [Fact]
    public void Snapshot_WithNoStages_ReportsFullSuccessRate_AndZeroCounts()
    {
        var metrics = new MetricsCollector();

        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.TotalStages);
        Assert.Equal(1.0, snapshot.SuccessRate);
        Assert.Null(snapshot.Mttr);
    }

    [Fact]
    public void RecordRetry_And_RecordRollback_FeedTotals()
    {
        var metrics = new MetricsCollector();

        metrics.RecordRetry("A");
        metrics.RecordRetry("A");
        metrics.RecordRetry("B");
        metrics.RecordRollback("A");

        metrics.RecordStageOutcome("A", false, TimeSpan.FromMilliseconds(1));
        metrics.RecordStageOutcome("B", true, TimeSpan.FromMilliseconds(1));

        var snapshot = metrics.Snapshot();

        Assert.Equal(3, snapshot.TotalRetries);
        Assert.Equal(1, snapshot.TotalRollbacks);
    }

    [Fact]
    public void Mttr_IsNull_WhenNoStageEverFailedFirst()
    {
        var metrics = new MetricsCollector();

        metrics.RecordStageOutcome("A", true, TimeSpan.FromMilliseconds(1));

        var snapshot = metrics.Snapshot();

        Assert.Null(snapshot.Mttr);
    }

    [Fact]
    public void Mttr_IsNonNull_WhenAStageRetriedThenSucceeded()
    {
        var metrics = new MetricsCollector();

        metrics.RecordRetry("A"); // sets FirstFailureAt
        metrics.RecordStageOutcome("A", true, TimeSpan.FromMilliseconds(1)); // sets RecoveredAt

        var snapshot = metrics.Snapshot();

        Assert.NotNull(snapshot.Mttr);
        Assert.True(snapshot.Mttr!.Value >= TimeSpan.Zero);
    }

    [Fact]
    public void StageLatencies_ReflectsMostRecentRecordedLatencyPerStage()
    {
        var metrics = new MetricsCollector();

        metrics.RecordStageOutcome("A", true, TimeSpan.FromMilliseconds(42));

        var snapshot = metrics.Snapshot();

        Assert.Equal(TimeSpan.FromMilliseconds(42), snapshot.StageLatencies["A"]);
    }
}
