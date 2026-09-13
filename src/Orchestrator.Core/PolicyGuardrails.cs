namespace Orchestrator.Core;

public enum PolicyDecision
{
    Allow,
    RequireApproval,
    Deny
}

public record PolicyEvaluation(PolicyDecision Decision, string Reason);

public interface IPolicyRule
{
    string Name { get; }
    PolicyEvaluation Evaluate(WorkflowContext context, StageDefinition stage);
}

/// <summary>
/// Runs every registered rule for a stage and aggregates the strictest
/// outcome (Deny beats RequireApproval beats Allow), so any single rule can
/// veto or gate a stage regardless of what the others say.
/// </summary>
public class PolicyGuardrailEngine(IEnumerable<IPolicyRule> rules)
{
    private readonly IReadOnlyList<IPolicyRule> _rules = rules.ToList();

    public (PolicyDecision Decision, IReadOnlyList<string> Reasons) Evaluate(WorkflowContext context, StageDefinition stage)
    {
        var evaluations = _rules.Select(r => (r.Name, Eval: r.Evaluate(context, stage))).ToList();

        var decision = PolicyDecision.Allow;
        var reasons = new List<string>();

        foreach (var (name, eval) in evaluations)
        {
            if (eval.Decision == PolicyDecision.Allow)
            {
                continue;
            }

            reasons.Add($"{name}: {eval.Reason}");

            if (eval.Decision == PolicyDecision.Deny)
            {
                decision = PolicyDecision.Deny;
            }
            else if (eval.Decision == PolicyDecision.RequireApproval && decision != PolicyDecision.Deny)
            {
                decision = PolicyDecision.RequireApproval;
            }
        }

        return (decision, reasons);
    }
}

/// <summary>Anything touching auth/security/payment surfaces needs a human sign-off before it proceeds.</summary>
public class SecuritySensitiveChangeRule : IPolicyRule
{
    private static readonly string[] SensitiveKeywords = ["auth", "security", "password", "payment", "secret", "token", "encryption"];

    public string Name => "SecuritySensitiveChange";

    public PolicyEvaluation Evaluate(WorkflowContext context, StageDefinition stage)
    {
        var haystack = string.Join(" ", context.InitialInput.Values.Select(v => v?.ToString() ?? "")).ToLowerInvariant();
        var hit = SensitiveKeywords.FirstOrDefault(haystack.Contains);

        return hit is not null
            ? new PolicyEvaluation(PolicyDecision.RequireApproval, $"requirement text mentions '{hit}'")
            : new PolicyEvaluation(PolicyDecision.Allow, "no sensitive keywords detected");
    }
}

/// <summary>Schema drops, destructive migrations, or data deletion always require sign-off.</summary>
public class DestructiveOperationRule : IPolicyRule
{
    private static readonly string[] DestructiveKeywords = ["drop table", "delete from", "truncate", "drop column", "force-push", "rm -rf"];

    public string Name => "DestructiveOperation";

    public PolicyEvaluation Evaluate(WorkflowContext context, StageDefinition stage)
    {
        var designResult = context.GetResult("Design");
        var implResult = context.GetResult("Implementation");
        var haystack = string.Join(" ", new[] { designResult, implResult }
                .Where(r => r is not null)
                .SelectMany(r => r!.Outputs.Values.Select(v => v?.ToString() ?? "")))
            .ToLowerInvariant();

        var hit = DestructiveKeywords.FirstOrDefault(haystack.Contains);

        return hit is not null
            ? new PolicyEvaluation(PolicyDecision.RequireApproval, $"planned change contains destructive operation '{hit}'")
            : new PolicyEvaluation(PolicyDecision.Allow, "no destructive operations detected");
    }
}
