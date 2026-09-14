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
        // Only escalate stages that actually produce a change. Gating the read-only
        // analysis stages too would demand a sign-off on every step of the run and train
        // reviewers to click through approvals without reading them.
        if (!stage.HighImpact)
        {
            return new PolicyEvaluation(PolicyDecision.Allow, "stage is not a high-impact action");
        }

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
        if (!stage.HighImpact)
        {
            return new PolicyEvaluation(PolicyDecision.Allow, "stage is not a high-impact action");
        }

        var hit = DestructiveKeywords.FirstOrDefault(PolicyScanning.ProducedWork(context).Contains);

        return hit is not null
            ? new PolicyEvaluation(PolicyDecision.RequireApproval, $"planned change contains destructive operation '{hit}'")
            : new PolicyEvaluation(PolicyDecision.Allow, "no destructive operations detected");
    }
}

/// <summary>
/// The one rule that denies outright instead of asking a human. Shipping a credential in
/// source is not a trade-off someone should be able to approve their way past in a hurry,
/// so this trips the engine's safe-stop and halts the run for investigation.
/// </summary>
public class HardcodedCredentialRule : IPolicyRule
{
    private static readonly string[] CredentialPatterns =
    [
        "password = \"", "password=\"", "apikey = \"", "api_key = \"", "api key = \"",
        "secret = \"", "connectionstring = \"user id", "aws_secret_access_key",
        "begin rsa private key", "begin private key", "bearer sk-", "akia"
    ];

    public string Name => "HardcodedCredential";

    public PolicyEvaluation Evaluate(WorkflowContext context, StageDefinition stage)
    {
        var hit = CredentialPatterns.FirstOrDefault(PolicyScanning.ProducedWork(context).Contains);

        return hit is not null
            ? new PolicyEvaluation(PolicyDecision.Deny, $"generated work appears to embed a credential (matched '{hit.Trim()}')")
            : new PolicyEvaluation(PolicyDecision.Allow, "no embedded credentials detected");
    }
}

internal static class PolicyScanning
{
    /// <summary>
    /// Everything the run has produced so far, lowercased for matching. Scanning all
    /// results rather than named stages means rules still cover stages created at runtime.
    /// </summary>
    public static string ProducedWork(WorkflowContext context) =>
        string.Join(" ", context.AllResults.Values
                .SelectMany(r => r.Outputs.Values.Select(v => v?.ToString() ?? "").Append(r.Summary)))
            .ToLowerInvariant();
}
