namespace Orchestrator.Core;

public enum StageStatus
{
    Pending,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Blocked,
    Skipped,
    Stale,
    RolledBack
}

/// <summary>
/// A node in the SDLC dependency graph. Stages whose <see cref="DependsOn"/>
/// are all satisfied at the same scheduling tick are executed concurrently by
/// the engine, and any stage depending on more than one of them acts as the
/// synchronization point.
/// </summary>
public class StageDefinition
{
    public required string Id { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public required IStageAgent Agent { get; init; }

    public bool RequiresApproval { get; init; }
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Checked before execution; if false, the stage is blocked rather than run.</summary>
    public Func<WorkflowContext, bool> EntryGate { get; init; } = _ => true;

    /// <summary>Checked after execution; if false, the result is treated as a failure (subject to retry).</summary>
    public Func<WorkflowContext, StageResult, bool> ExitGate { get; init; } = (_, result) => result.Success;
}
