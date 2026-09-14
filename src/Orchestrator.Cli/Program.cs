using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestrator.Agents;
using Orchestrator.Cli;
using Orchestrator.Core;

var options = CliOptions.Parse(args);
if (options is null)
{
    PrintUsage();
    return 1;
}

var solutionRoot = FindSolutionRoot(AppContext.BaseDirectory)
    ?? throw new InvalidOperationException("Could not locate the solution root (no .sln found above the executable).");
var artifactsRoot = Path.Combine(solutionRoot, "artifacts", "runs");

var llm = new AnthropicLlmClient();
var artifactWriter = new ArtifactWriter(artifactsRoot);
var runId = $"{options.Scenario}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
var (requirement, codebaseContext) = ScenarioLibrary.Get(options.Scenario);

var initialInput = new Dictionary<string, object>
{
    ["requirement"] = requirement,
    ["codebaseContext"] = codebaseContext
};

var ctx = new WorkflowContext(runId, options.Scenario, initialInput);

IStageAgent Wrap(string stageId, IStageAgent agent) =>
    options.InjectFailureStage == stageId
        ? new FailingAgentDecorator(agent, options.InjectFailureCount)
        : agent;

IStageAgent requirementAgent = new RequirementAnalysisAgent(llm);
if (options.SimulateReplan)
{
    requirementAgent = new ChangingAgentDecorator(requirementAgent);
}

IStageAgent designAgent = new DesignAgent(llm);
if (options.InjectPolicyViolation)
{
    designAgent = new PolicyViolatingAgentDecorator(designAgent);
}

// Each decomposed task becomes one of these: an implementation stage that depends on Design
// plus whichever sibling tasks the decomposition said must come first. The approval gate sits
// here, on the stages that actually generate code, rather than on the barrier that aggregates
// work already done.
StageDefinition BuildTaskStage(WorkTask task, IReadOnlyList<string> dependsOn) => new()
{
    Id = $"Implementation:{task.Id}",
    DependsOn = dependsOn,
    Agent = new TaskImplementationAgent(llm, artifactWriter, task),
    MaxRetries = 2,
    // Generating a code change is the high-impact action in this pipeline: it always takes a
    // human checkpoint, and policy rules escalate further based on what the change touches.
    HighImpact = true,
    RequiresApproval = true
};

var stages = new List<StageDefinition>
{
    new()
    {
        Id = "RequirementAnalysis",
        Agent = Wrap("RequirementAnalysis", requirementAgent),
        MaxRetries = 2
    },
    new()
    {
        Id = "CodebaseAnalysis",
        DependsOn = ["RequirementAnalysis"],
        Agent = Wrap("CodebaseAnalysis", new CodebaseAnalysisAgent(llm, artifactWriter, solutionRoot)),
        MaxRetries = 2,
        // Entry gate: only scan and reason about existing code when this change is landing in
        // an existing system. A pure greenfield run records this as Skipped instead of
        // pretending to have analyzed a codebase that isn't there.
        EntryGate = c => !string.IsNullOrWhiteSpace(c.InitialInput.GetValueOrDefault("codebaseContext", "").ToString())
    },
    new()
    {
        Id = "TaskDecomposition",
        DependsOn = ["RequirementAnalysis"], // runs concurrently with CodebaseAnalysis
        Agent = Wrap("TaskDecomposition", new TaskDecompositionAgent(llm, artifactWriter, BuildTaskStage)),
        MaxRetries = 2,
        // Exit gate: a decomposition that produced no tasks is not a usable plan, whatever
        // the agent reported, so treat it as a failure and let the retry policy handle it.
        ExitGate = (_, result) => result.Success
            && result.Outputs.TryGetValue("taskCount", out var count)
            && int.TryParse(count.ToString(), out var parsed) && parsed > 0
    },
    new()
    {
        Id = "Design",
        DependsOn = ["RequirementAnalysis", "CodebaseAnalysis", "TaskDecomposition"],
        // AllSettled: design still proceeds when CodebaseAnalysis was gated out as
        // inapplicable — a skipped optional input must not block the pipeline.
        DependencyRule = DependencyRule.AllSettled,
        Agent = Wrap("Design", designAgent),
        MaxRetries = 2,
        // A design is a decision about what will be built, so guardrails treat it as
        // high-impact — for a security-sensitive change it needs sign-off before the
        // implementation stages are even derived from it.
        HighImpact = true
    },
    new()
    {
        Id = "Implementation",
        DependsOn = ["Design"], // TaskDecomposition adds each derived task stage to this list
        Agent = Wrap("Implementation", new ImplementationAggregatorAgent(artifactWriter)),
        MaxRetries = 1
    },
    new()
    {
        Id = "Testing",
        DependsOn = ["Implementation"],
        Agent = Wrap("Testing", new TestingAgent(llm, artifactWriter, solutionRoot, runRealTests: !options.SkipRealTests)),
        MaxRetries = 2,
        // Exit gate: if this stage claims it ran the real suite, the suite must have passed.
        // Guards against a stage reporting success while carrying a failing test result.
        ExitGate = (_, result) => result.Success
            && result.Outputs.ContainsKey("testPlanArtifact")
            && (result.Outputs.GetValueOrDefault("realTestsRun", "false").ToString() != "true"
                || result.Outputs.GetValueOrDefault("realTestsPassed", "false").ToString() == "true")
    },
    new()
    {
        Id = "Documentation",
        DependsOn = ["Implementation"], // runs in parallel with Testing; both only need Implementation's output
        Agent = Wrap("Documentation", new DocumentationAgent(llm, artifactWriter)),
        MaxRetries = 1
    },
    new()
    {
        Id = "SecurityReview",
        DependsOn = ["Implementation"], // third parallel branch, but only when applicable
        Agent = Wrap("SecurityReview", new SecurityReviewAgent(llm, artifactWriter)),
        MaxRetries = 1,
        EntryGate = SecurityReviewAgent.IsApplicable
    },
    new()
    {
        Id = "ReleaseReadiness",
        // Synchronization point for all three parallel branches. AllSettled so a security
        // review that was correctly skipped as inapplicable doesn't block the release gate.
        DependsOn = ["Testing", "Documentation", "SecurityReview"],
        DependencyRule = DependencyRule.AllSettled,
        Agent = new ReleaseReadinessAgent(),
        MaxRetries = 0
    }
};

var graph = new WorkflowGraph(stages);
IApprovalProvider approvals = options.AutoApprove ? new AutoApprovalProvider() : new ConsoleApprovalProvider();
IReadOnlyList<IPolicyRule> policies =
[
    new SecuritySensitiveChangeRule(),
    new DestructiveOperationRule(),
    new HardcodedCredentialRule()
];

Console.WriteLine($"=== Agentic SDLC Orchestrator — scenario '{options.Scenario}' (run {runId}) ===");
Console.WriteLine($"Requirement: {requirement}");
if (!string.IsNullOrWhiteSpace(codebaseContext))
{
    Console.WriteLine($"Codebase context: {codebaseContext}");
}
if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is null)
{
    Console.WriteLine("[info] ANTHROPIC_API_KEY not set — stages will run in deterministic offline fallback mode.");
}
Console.WriteLine();

var engine = new WorkflowEngine();
var report = await engine.RunAsync(graph, ctx, approvals, policies);

ReportPrinter.Print(report);
WriteReportToDisk(artifactsRoot, runId, report);

var overallSuccess = report.Success;

if (options.SimulateReplan && report.Success)
{
    Console.WriteLine();
    Console.WriteLine("=== Simulating an upstream requirement change: forcing RequirementAnalysis back to Stale ===");
    Console.WriteLine("(Same WorkflowContext carries forward — decision lineage and audit log accumulate across both runs.)");
    Console.WriteLine();

    var resumeFrom = new Dictionary<string, StageStatus>(report.StageStatuses)
    {
        ["RequirementAnalysis"] = StageStatus.Stale
    };

    var replanReport = await engine.RunAsync(graph, ctx, approvals, policies, resumeFrom: resumeFrom);
    ReportPrinter.Print(replanReport);
    WriteReportToDisk(artifactsRoot, $"{runId}-replan", replanReport);
    overallSuccess = overallSuccess && replanReport.Success;
}

Console.WriteLine($"Full run report and generated artifacts: {Path.Combine(artifactsRoot, runId)}");

return overallSuccess ? 0 : 1;

static string? FindSolutionRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (dir.GetFiles("*.sln").Length > 0)
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return null;
}

static void WriteReportToDisk(string artifactsRoot, string runId, RunReport report)
{
    var dir = Path.Combine(artifactsRoot, runId);
    Directory.CreateDirectory(dir);
    var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, options));
}

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run --project src/Orchestrator.Cli -- <scenario> [options]");
    Console.WriteLine();
    Console.WriteLine($"  scenario: one of {string.Join(", ", CliOptions.KnownScenarios)}");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --auto-approve              Approve every human checkpoint automatically (unattended/CI runs).");
    Console.WriteLine("  --skip-real-tests           Skip the real `dotnet test` run in the Testing stage (faster).");
    Console.WriteLine("  --inject-failure=Stage:N    Fail the given stage N times before letting it succeed, to");
    Console.WriteLine("                              demonstrate bounded retry (small N) or rollback (N exceeding");
    Console.WriteLine("                              the stage's MaxRetries).");
    Console.WriteLine("  --simulate-replan           After a successful run, force RequirementAnalysis stale and");
    Console.WriteLine("                              re-run on the same context to demonstrate dynamic re-planning");
    Console.WriteLine("                              cascading through every downstream stage.");
    Console.WriteLine("  --inject-policy-violation   Make the Design stage emit an embedded credential, so the");
    Console.WriteLine("                              HardcodedCredential guardrail denies the next stage and trips");
    Console.WriteLine("                              the engine's safe-stop.");
}
