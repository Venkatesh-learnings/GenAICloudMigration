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
/// How a stage treats a dependency that finished without succeeding. Mirrors the
/// trigger-rule idea in mature workflow engines: some stages genuinely cannot run
/// without their upstream output, others only wanted it if it was relevant.
/// </summary>
public enum DependencyRule
{
    /// <summary>
    /// Every dependency must have Succeeded. A dependency that was Skipped (its own
    /// entry gate declined) cascades the skip to this stage, because the output it
    /// needed will never exist.
    /// </summary>
    AllSucceeded,

    /// <summary>
    /// Every dependency must have settled without failing — Succeeded or Skipped both
    /// count. Use this for a stage that should still run when an optional upstream
    /// stage was gated out (e.g. a release gate that waits for a conditional security
    /// review, but must not be blocked just because the review wasn't applicable).
    /// </summary>
    AllSettled
}

/// <summary>
/// A node in the SDLC dependency graph. Stages whose <see cref="DependsOn"/>
/// are all satisfied at the same scheduling tick are executed concurrently by
/// the engine, and any stage depending on more than one of them acts as the
/// synchronization point.
/// </summary>
public class StageDefinition
{
    private IReadOnlyList<string> _dependsOn = [];

    public required string Id { get; init; }

    public IReadOnlyList<string> DependsOn
    {
        get => _dependsOn;
        init => _dependsOn = value;
    }

    /// <summary>
    /// Used only by <see cref="WorkflowGraph.AddStages"/> when a runtime expansion makes an
    /// existing barrier wait for newly derived stages. Restricted to stages that have not
    /// started, so a stage's recorded dependencies always match what it actually waited for.
    /// </summary>
    internal void SetDependsOn(IReadOnlyList<string> dependsOn) => _dependsOn = dependsOn;

    public required IStageAgent Agent { get; init; }

    public DependencyRule DependencyRule { get; init; } = DependencyRule.AllSucceeded;

    public bool RequiresApproval { get; init; }

    /// <summary>
    /// True for stages that produce something intended to change the product (a design to
    /// build, code to apply) as opposed to read-only analysis or reporting. Policy rules use
    /// this to escalate only the actions that actually carry risk — without it, a single
    /// sensitive word in the requirement would demand a human sign-off on every stage in the
    /// run, including the ones that only read and summarize.
    /// </summary>
    public bool HighImpact { get; init; }
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Checked before execution; if false the stage is Skipped rather than run. Use it for
    /// work that is only sometimes applicable (a security review for a change that touches
    /// auth, a migration step only when the schema changed).
    /// </summary>
    public Func<WorkflowContext, bool> EntryGate { get; init; } = _ => true;

    /// <summary>
    /// Checked after execution; if false the result is treated as a failure (subject to
    /// retry). Use it to assert on the substance of a stage's output, not just that the
    /// agent returned without throwing.
    /// </summary>
    public Func<WorkflowContext, StageResult, bool> ExitGate { get; init; } = (_, result) => result.Success;

    /// <summary>True when this stage's dependencies are satisfied under its dependency rule.</summary>
    internal bool DependenciesSatisfiedBy(Func<string, StageStatus> statusOf) => DependencyRule switch
    {
        DependencyRule.AllSettled => DependsOn.All(dep => statusOf(dep) is StageStatus.Succeeded or StageStatus.Skipped),
        _ => DependsOn.All(dep => statusOf(dep) == StageStatus.Succeeded)
    };
}
