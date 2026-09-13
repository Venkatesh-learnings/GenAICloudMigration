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
    StageBlocked
}

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

    public volatile bool SafeStopRequested;
    public string? SafeStopReason;

    public IReadOnlyList<DecisionRecord> DecisionLineage => [.. _decisionLineage];
    public IReadOnlyList<AuditEntry> AuditLog => [.. _auditLog];

    public StageResult? GetResult(string stageId) => _stageResults.GetValueOrDefault(stageId);

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
