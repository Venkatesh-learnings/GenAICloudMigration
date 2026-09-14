namespace Orchestrator.Core;

public enum ApprovalDecision
{
    Approved,
    Rejected
}

public interface IApprovalProvider
{
    Task<ApprovalDecision> RequestApprovalAsync(WorkflowContext context, StageDefinition stage, StageResult pendingResult, CancellationToken ct);
}

/// <summary>Approves everything automatically. Useful for unattended/CI runs and tests, never for production.</summary>
public class AutoApprovalProvider : IApprovalProvider
{
    public Task<ApprovalDecision> RequestApprovalAsync(WorkflowContext context, StageDefinition stage, StageResult pendingResult, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Approved);
}

/// <summary>Prompts a human on the console — the controlled-autonomy checkpoint for interactive runs.</summary>
public class ConsoleApprovalProvider : IApprovalProvider
{
    public Task<ApprovalDecision> RequestApprovalAsync(WorkflowContext context, StageDefinition stage, StageResult pendingResult, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"[APPROVAL REQUIRED] Stage '{stage.Id}' — {pendingResult.Summary}");
        Console.WriteLine($"  Rationale: {pendingResult.Rationale}");
        Console.Write("  Approve? [y/N]: ");
        var input = Console.ReadLine();
        var approved = string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected);
    }
}
