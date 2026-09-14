namespace Orchestrator.Cli;

public class CliOptions
{
    public required string Scenario { get; init; }
    public bool AutoApprove { get; set; }
    public bool SkipRealTests { get; set; }
    public string? InjectFailureStage { get; set; }
    public int InjectFailureCount { get; set; } = 1;
    public bool SimulateReplan { get; set; }
    public bool InjectPolicyViolation { get; set; }

    public static readonly string[] KnownScenarios = ["greenfield", "brownfield", "ambiguous", "security"];

    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0 || !KnownScenarios.Contains(args[0]))
        {
            return null;
        }

        var options = new CliOptions { Scenario = args[0] };

        foreach (var arg in args.Skip(1))
        {
            if (arg == "--auto-approve")
            {
                options.AutoApprove = true;
            }
            else if (arg == "--skip-real-tests")
            {
                options.SkipRealTests = true;
            }
            else if (arg == "--simulate-replan")
            {
                options.SimulateReplan = true;
            }
            else if (arg == "--inject-policy-violation")
            {
                options.InjectPolicyViolation = true;
            }
            else if (arg.StartsWith("--inject-failure="))
            {
                var value = arg["--inject-failure=".Length..];
                var parts = value.Split(':', 2);
                options.InjectFailureStage = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[1], out var count))
                {
                    options.InjectFailureCount = count;
                }
            }
        }

        return options;
    }
}
