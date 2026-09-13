namespace Orchestrator.Core;

public interface IStageAgent
{
    Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct);
}

/// <summary>
/// Optional capability: an agent that can undo the effects of a previously
/// succeeded execution when the engine needs to roll a stage back
/// (e.g. a downstream failure, or a safety-critical policy trip).
/// </summary>
public interface IRollbackable
{
    Task RollbackAsync(WorkflowContext context, StageDefinition stage, StageResult lastResult, CancellationToken ct);
}
