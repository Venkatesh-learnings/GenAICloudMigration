using System.Diagnostics;
using Orchestrator.Core;

namespace Orchestrator.Agents;

/// <summary>
/// Generates a test plan for the change, and — the important part for
/// "validation and risk control" — actually runs the real solution's test
/// suite via <c>dotnet test</c> rather than trusting the LLM's word for it.
/// A real failure here is a real stage failure, which the engine will retry
/// and, if retries are exhausted, roll back: this is the one stage in the
/// pipeline whose success/failure is grounded in a verifiable external fact
/// instead of a model's self-report.
/// </summary>
public class TestingAgent(ILlmClient llm, ArtifactWriter artifacts, string solutionRoot, bool runRealTests = true)
    : AgentBase(llm), IStageAgent
{
    private const string SystemPrompt = """
        You are a QA engineer. Given a design and implementation summary, produce a concise
        Markdown test plan: list unit and integration test cases (as bullet points) that would
        give confidence this change is correct, including edge cases and failure scenarios.
        """;

    public async Task<StageResult> ExecuteAsync(WorkflowContext context, StageDefinition stage, CancellationToken ct)
    {
        var designSummary = context.GetResult("Design")?.Outputs.GetValueOrDefault("designSummary", "").ToString() ?? "";
        var implSummary = context.GetResult("Implementation")?.Summary ?? "";

        var userPrompt = $"Design:\n{designSummary}\n\nImplementation:\n{implSummary}";
        var (planText, usedLlm) = await CompleteWithFallbackAsync(SystemPrompt, userPrompt, () => OfflineFallback(designSummary), ct);
        var planPath = artifacts.WriteText(context.RunId, stage.Id, "test-plan.md", planText);

        if (!runRealTests)
        {
            var skippedOutputs = new Dictionary<string, object> { ["testPlanArtifact"] = planPath, ["realTestsRun"] = "false" };
            return StageResult.Ok("Test plan generated (real test execution skipped by configuration).",
                usedLlm ? "LLM-generated test plan; real dotnet test run skipped." : "Offline test plan; real dotnet test run skipped.",
                skippedOutputs, [planPath]);
        }

        var (testsPassed, testOutput) = await RunRealTestsAsync(ct);
        var resultPath = artifacts.WriteText(context.RunId, stage.Id, "test-run-output.txt", testOutput);

        var outputs = new Dictionary<string, object>
        {
            ["testPlanArtifact"] = planPath,
            ["realTestsRun"] = "true",
            ["realTestsPassed"] = testsPassed.ToString(),
        };

        var rationale = $"{(usedLlm ? "LLM-generated" : "Offline")} test plan, backed by an actual `dotnet test` run against the solution " +
                         $"(not simulated) — see {resultPath}.";

        return testsPassed
            ? StageResult.Ok("Real test suite passed.", rationale, outputs, [planPath, resultPath])
            : StageResult.Fail($"Real test suite failed. See {resultPath} for output.");
    }

    private async Task<(bool Passed, string Output)> RunRealTestsAsync(CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "test --no-restore --nologo -v quiet",
                WorkingDirectory = solutionRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (false, "dotnet test timed out after 3 minutes.");
        }

        var output = await stdoutTask + "\n" + await stderrTask;
        return (process.ExitCode == 0, output);
    }

    private static string OfflineFallback(string designSummary) =>
        $"# Test Plan (offline fallback)\n\nLLM unavailable. Minimum recommended coverage for:\n\n{designSummary}\n\n" +
        "- Happy path\n- Invalid input\n- Not-found / edge cases\n";
}
