using System.Collections.Concurrent;
using Orchestrator.Core;

namespace Orchestrator.Tests;

/// <summary>Builds StageDefinitions with fast-test-friendly defaults (tiny retry delay).</summary>
public static class StageBuilder
{
    public static StageDefinition Make(
        string id,
        IStageAgent agent,
        IEnumerable<string>? dependsOn = null,
        bool requiresApproval = false,
        int maxRetries = 1,
        TimeSpan? retryBaseDelay = null,
        Func<WorkflowContext, bool>? entryGate = null,
        Func<WorkflowContext, StageResult, bool>? exitGate = null) =>
        new StageDefinition
        {
            Id = id,
            Agent = agent,
            DependsOn = dependsOn?.ToList() ?? [],
            RequiresApproval = requiresApproval,
            MaxRetries = maxRetries,
            RetryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(2),
            EntryGate = entryGate ?? (_ => true),
            ExitGate = exitGate ?? ((_, r) => r.Success)
        };
}

/// <summary>Always succeeds immediately. Counts invocations.</summary>
public class NoOpAgent : IStageAgent
{
    public int CallCount;

    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        Interlocked.Increment(ref CallCount);
        return Task.FromResult(StageResult.Ok($"{stage.Id} done"));
    }
}

/// <summary>Succeeds after sleeping for a fixed delay, recording its own start/end so tests can prove overlap.</summary>
public class DelayAgent(string id, TimeSpan delay, ConcurrentBag<(string Id, DateTimeOffset Start, DateTimeOffset End)> log) : IStageAgent
{
    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(delay, ct);
        var end = DateTimeOffset.UtcNow;
        log.Add((id, start, end));
        return StageResult.Ok($"{id} done");
    }
}

/// <summary>Records the order in which stages actually executed (thread-safe).</summary>
public class OrderTrackingAgent(string id, List<string> order) : IStageAgent
{
    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        lock (order)
        {
            order.Add(id);
        }
        return Task.FromResult(StageResult.Ok($"{id} done"));
    }
}

/// <summary>Returns a scripted sequence of results, one per call; repeats the last result once exhausted.</summary>
public class ScriptedAgent(params StageResult[] results) : IStageAgent
{
    private readonly Queue<StageResult> _queue = new(results);
    private readonly object _lock = new();
    public int CallCount;

    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        Interlocked.Increment(ref CallCount);
        StageResult next;
        lock (_lock)
        {
            next = _queue.Count > 1 ? _queue.Dequeue() : _queue.Peek();
        }
        return Task.FromResult(next);
    }
}

/// <summary>Always fails, and implements IRollbackable so rollback invocation can be asserted.</summary>
public class AlwaysFailAgent : IStageAgent, IRollbackable
{
    public int ExecuteCount;
    public int RollbackCount;

    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        Interlocked.Increment(ref ExecuteCount);
        return Task.FromResult(StageResult.Fail($"{stage.Id} always fails"));
    }

    public Task RollbackAsync(WorkflowContext context, StageDefinition stage, StageResult lastResult, CancellationToken ct)
    {
        Interlocked.Increment(ref RollbackCount);
        return Task.CompletedTask;
    }
}

/// <summary>Always fails and does NOT implement IRollbackable, to prove rollback is skipped gracefully.</summary>
public class AlwaysFailNoRollbackAgent : IStageAgent
{
    public int ExecuteCount;

    public Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        Interlocked.Increment(ref ExecuteCount);
        return Task.FromResult(StageResult.Fail($"{stage.Id} always fails"));
    }
}

/// <summary>Scriptable approval provider that never touches Console. Returns a fixed sequence of decisions.</summary>
public class ScriptedApprovalProvider(params ApprovalDecision[] decisions) : IApprovalProvider
{
    private readonly Queue<ApprovalDecision> _queue = new(decisions);
    public int CallCount;

    public Task<ApprovalDecision> RequestApprovalAsync(WorkflowContext context, StageDefinition stage, StageResult pendingResult, CancellationToken ct)
    {
        Interlocked.Increment(ref CallCount);
        var decision = _queue.Count > 0 ? _queue.Dequeue() : ApprovalDecision.Approved;
        return Task.FromResult(decision);
    }
}

/// <summary>A fake policy rule that always returns a fixed decision, for testing aggregation.</summary>
public class FakePolicyRule(string name, PolicyDecision decision, string reason = "fake reason") : IPolicyRule
{
    public string Name { get; } = name;

    public PolicyEvaluation Evaluate(WorkflowContext context, StageDefinition stage) => new(decision, reason);
}
