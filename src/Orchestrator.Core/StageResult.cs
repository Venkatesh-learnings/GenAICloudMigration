namespace Orchestrator.Core;

public class StageResult
{
    public required bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Rationale { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object> Outputs { get; init; } = new Dictionary<string, object>();
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    public Exception? Error { get; init; }

    public static StageResult Ok(string summary, string rationale = "", IDictionary<string, object>? outputs = null, IEnumerable<string>? artifacts = null) =>
        new()
        {
            Success = true,
            Summary = summary,
            Rationale = rationale,
            Outputs = outputs is null ? new Dictionary<string, object>() : new Dictionary<string, object>(outputs),
            Artifacts = artifacts?.ToList() ?? []
        };

    public static StageResult Fail(string summary, Exception? error = null) =>
        new() { Success = false, Summary = summary, Error = error };
}
