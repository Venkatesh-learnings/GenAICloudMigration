using System.Collections.Concurrent;

namespace Orchestrator.Core;

public record RunMetrics(
    int TotalStages,
    int SucceededStages,
    int FailedStages,
    double SuccessRate,
    int TotalRetries,
    int TotalRollbacks,
    TimeSpan? Mttr,
    TimeSpan TotalLatency,
    IReadOnlyDictionary<string, TimeSpan> StageLatencies);

internal record StageMetric
{
    public int Retries;
    public int Rollbacks;
    public bool Succeeded;
    public TimeSpan Latency;
    public DateTimeOffset? FirstFailureAt;
    public DateTimeOffset? RecoveredAt;
}

/// <summary>
/// Tracks the reliability signals the assignment calls out explicitly:
/// success rate, retry/rollback frequency, MTTR, and end-to-end latency.
/// </summary>
public class MetricsCollector
{
    private readonly ConcurrentDictionary<string, StageMetric> _stageMetrics = new();
    private readonly System.Diagnostics.Stopwatch _runStopwatch = System.Diagnostics.Stopwatch.StartNew();

    private StageMetric Metric(string stageId) => _stageMetrics.GetOrAdd(stageId, _ => new StageMetric());

    public void RecordRetry(string stageId)
    {
        var m = Metric(stageId);
        Interlocked.Increment(ref m.Retries);
        m.FirstFailureAt ??= DateTimeOffset.UtcNow;
    }

    public void RecordRollback(string stageId) => Interlocked.Increment(ref Metric(stageId).Rollbacks);

    public void RecordStageOutcome(string stageId, bool succeeded, TimeSpan latency)
    {
        var m = Metric(stageId);
        m.Succeeded = succeeded;
        m.Latency = latency;
        if (succeeded && m.FirstFailureAt is not null && m.RecoveredAt is null)
        {
            m.RecoveredAt = DateTimeOffset.UtcNow;
        }
    }

    public RunMetrics Snapshot()
    {
        var metrics = _stageMetrics.ToArray();
        var succeeded = metrics.Count(kv => kv.Value.Succeeded);
        var total = metrics.Length;

        var recoveryDurations = metrics
            .Where(kv => kv.Value.FirstFailureAt is not null && kv.Value.RecoveredAt is not null)
            .Select(kv => kv.Value.RecoveredAt!.Value - kv.Value.FirstFailureAt!.Value)
            .ToList();

        return new RunMetrics(
            TotalStages: total,
            SucceededStages: succeeded,
            FailedStages: total - succeeded,
            SuccessRate: total == 0 ? 1.0 : (double)succeeded / total,
            TotalRetries: metrics.Sum(kv => kv.Value.Retries),
            TotalRollbacks: metrics.Sum(kv => kv.Value.Rollbacks),
            Mttr: recoveryDurations.Count > 0
                ? TimeSpan.FromTicks((long)recoveryDurations.Average(d => d.Ticks))
                : null,
            TotalLatency: _runStopwatch.Elapsed,
            StageLatencies: metrics.ToDictionary(kv => kv.Key, kv => kv.Value.Latency));
    }
}
