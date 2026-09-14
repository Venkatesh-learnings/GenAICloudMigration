namespace Orchestrator.Core;

public record RunReport(
    string RunId,
    string ScenarioName,
    bool Success,
    string? StopReason,
    IReadOnlyDictionary<string, StageStatus> StageStatuses,
    IReadOnlyList<DecisionRecord> DecisionLineage,
    IReadOnlyList<AuditEntry> AuditLog,
    RunMetrics Metrics);
