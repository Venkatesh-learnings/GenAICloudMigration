using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orchestrator.Core;

public record DecisionRecord(
    string StageId,
    DateTimeOffset Timestamp,
    string Summary,
    string Rationale,
    string OutputsHash);

public enum AuditEventType
{
    StageStarted,
    StageSucceeded,
    StageFailed,
    RetryAttempted,
    RolledBack,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalDenied,
    PolicyBlocked,
    PolicyRequiredApproval,
    StageReplanned,
    SafeStopTriggered,
    StageBlocked,
    GraphExpanded
}

/// <summary>
/// New stages a running stage wants added to the graph, plus any existing stages that
/// should now wait for them (typically a downstream barrier that fans the new work back in).
/// </summary>
public record StageExpansion(
    IReadOnlyList<StageDefinition> NewStages,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AdditionalDependencies);

public record AuditEntry(
    DateTimeOffset Timestamp,
    string StageId,
    AuditEventType EventType,
    string Details);

/// <summary>
/// Shared, mutable run state threaded through every stage. Carries
/// cross-stage outputs, the append-only decision lineage (why each stage
/// decided what it decided) and the audit trail (what the engine did and
/// when) that make the run inspectable after the fact.
/// </summary>
public class WorkflowContext(string runId, string scenarioName, IReadOnlyDictionary<string, object>? initialInput = null)
{
    public string RunId { get; } = runId;
    public string ScenarioName { get; } = scenarioName;
    public IReadOnlyDictionary<string, object> InitialInput { get; } = initialInput ?? new Dictionary<string, object>();

    private readonly ConcurrentDictionary<string, StageResult> _stageResults = new();
    private readonly ConcurrentDictionary<string, string> _outputHashes = new();
    private readonly ConcurrentQueue<DecisionRecord> _decisionLineage = new();
    private readonly ConcurrentQueue<AuditEntry> _auditLog = new();
    private readonly ConcurrentQueue<StageExpansion> _requestedExpansions = new();

    public volatile bool SafeStopRequested;
    public string? SafeStopReason;

    public IReadOnlyList<DecisionRecord> DecisionLineage => [.. _decisionLineage];
    public IReadOnlyList<AuditEntry> AuditLog => [.. _auditLog];

    public StageResult? GetResult(string stageId) => _stageResults.GetValueOrDefault(stageId);

    /// <summary>
    /// Every stage result recorded so far. Policy rules scan this rather than naming
    /// individual stages, so a guardrail still sees work produced by stages that only
    /// came into existence at runtime (e.g. per-task implementation stages).
    /// </summary>
    public IReadOnlyDictionary<string, StageResult> AllResults => _stageResults.ToDictionary(kv => kv.Key, kv => kv.Value);

    public void SetResult(string stageId, StageResult result)
    {
        _stageResults[stageId] = result;
    }

    public string? GetLastOutputHash(string stageId) => _outputHashes.GetValueOrDefault(stageId);

    /// <returns>true if this stage's output changed since the last time it ran (used to trigger re-planning).</returns>
    public bool RecordOutputHash(string stageId, StageResult result)
    {
        var hash = HashOutputs(result.Outputs);
        var previous = _outputHashes.GetValueOrDefault(stageId);
        _outputHashes[stageId] = hash;

        _decisionLineage.Enqueue(new DecisionRecord(stageId, DateTimeOffset.UtcNow, result.Summary, result.Rationale, hash));

        return previous is not null && previous != hash;
    }

    /// <summary>
    /// Asks the engine to extend the graph with stages this stage just derived — e.g. one
    /// implementation stage per decomposed task. The request is queued rather than applied
    /// directly: the engine commits it between scheduling ticks, so the graph is never
    /// mutated while stages are executing concurrently.
    /// </summary>
    public void RequestStages(
        IReadOnlyList<StageDefinition> newStages,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? additionalDependencies = null) =>
        _requestedExpansions.Enqueue(new StageExpansion(
            newStages,
            additionalDependencies ?? new Dictionary<string, IReadOnlyList<string>>()));

    internal IReadOnlyList<StageExpansion> DrainRequestedExpansions()
    {
        var drained = new List<StageExpansion>();
        while (_requestedExpansions.TryDequeue(out var expansion))
        {
            drained.Add(expansion);
        }
        return drained;
    }

    public void Audit(string stageId, AuditEventType eventType, string details) =>
        _auditLog.Enqueue(new AuditEntry(DateTimeOffset.UtcNow, stageId, eventType, details));

    public void RequestSafeStop(string reason)
    {
        SafeStopRequested = true;
        SafeStopReason = reason;
    }

    private static string HashOutputs(IReadOnlyDictionary<string, object> outputs)
    {
        var json = JsonSerializer.Serialize(outputs.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16];
    }
}
