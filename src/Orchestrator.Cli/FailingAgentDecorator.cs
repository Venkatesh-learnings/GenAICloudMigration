using Orchestrator.Core;

namespace Orchestrator.Cli;

/// <summary>
/// Demo-only wrapper that fails the first N executions of a wrapped agent
/// before delegating for real, so a scenario run can exercise the engine's
/// bounded-retry and rollback behavior against an otherwise-real pipeline
/// instead of requiring a genuinely flaky dependency.
/// </summary>
public class FailingAgentDecorator(IStageAgent inner, int failuresBeforeSuccess) : IStageAgent, IRollbackable
{
    private int _attempts;

    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        if (attempt <= failuresBeforeSuccess)
        {
            return Task.FromResult(StageResult.Fail(
                $"[injected failure] attempt {attempt}/{failuresBeforeSuccess} for stage '{stage.Id}'",
                new InvalidOperationException("Injected failure for demonstration purposes.")));
        }

        return inner.ExecuteAsync(context, stage, ct);
    }

    public Task RollbackAsync(WorkflowContext context, StageDefinition stage, StageResult lastResult, CancellationToken ct)
    {
        context.Audit(stage.Id, AuditEventType.RolledBack, "[injected failure] rollback executed after exhausting retries");
        return Task.CompletedTask;
    }
}
