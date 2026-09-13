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
        Id = "Design",
        DependsOn = ["RequirementAnalysis"],
        Agent = Wrap("Design", new DesignAgent(llm)),
        MaxRetries = 2
    },
    new()
    {
        Id = "Implementation",
        DependsOn = ["Design"],
        Agent = Wrap("Implementation", new ImplementationAgent(llm, artifactWriter)),
        MaxRetries = 2,
        RequiresApproval = true // high-impact action: generating a code change always needs a human checkpoint
    },
    new()
    {
        Id = "Testing",
        DependsOn = ["Implementation"],
        Agent = Wrap("Testing", new TestingAgent(llm, artifactWriter, solutionRoot, runRealTests: !options.SkipRealTests)),
        MaxRetries = 2
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
        Id = "ReleaseReadiness",
        DependsOn = ["Testing", "Documentation"], // synchronization point for the two parallel branches
        Agent = new ReleaseReadinessAgent(),
        MaxRetries = 0
    }
};

var graph = new WorkflowGraph(stages);
IApprovalProvider approvals = options.AutoApprove ? new AutoApprovalProvider() : new ConsoleApprovalProvider();
IReadOnlyList<IPolicyRule> policies = [new SecuritySensitiveChangeRule(), new DestructiveOperationRule()];

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
}
