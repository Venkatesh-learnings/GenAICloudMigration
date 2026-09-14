using Orchestrator.Core;

namespace Orchestrator.Cli;

public static class ReportPrinter
{
    public static void Print(RunReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Run {report.RunId} ({report.ScenarioName}) ===");
        Console.WriteLine($"Overall result: {(report.Success ? "SUCCESS" : "FAILED")}");
        if (report.StopReason is not null)
        {
            Console.WriteLine($"Stop reason: {report.StopReason}");
        }

        Console.WriteLine();
        Console.WriteLine("-- Stage statuses --");
        foreach (var (stageId, status) in report.StageStatuses)
        {
            Console.WriteLine($"  {stageId,-20} {status}");
        }

        Console.WriteLine();
        Console.WriteLine("-- Decision lineage --");
        foreach (var decision in report.DecisionLineage)
        {
            Console.WriteLine($"  [{decision.Timestamp:HH:mm:ss}] {decision.StageId}: {decision.Summary}");
            Console.WriteLine($"      rationale: {decision.Rationale}");
        }

        Console.WriteLine();
        Console.WriteLine("-- Audit trail --");
        foreach (var entry in report.AuditLog)
        {
            Console.WriteLine($"  [{entry.Timestamp:HH:mm:ss}] {entry.StageId,-20} {entry.EventType,-24} {entry.Details}");
        }

        Console.WriteLine();
        Console.WriteLine("-- Reliability metrics --");
        var m = report.Metrics;
        Console.WriteLine($"  Success rate:     {m.SuccessRate:P0} ({m.SucceededStages}/{m.TotalStages} stages)");
        Console.WriteLine($"  Total retries:    {m.TotalRetries}");
        Console.WriteLine($"  Total rollbacks:  {m.TotalRollbacks}");
        Console.WriteLine($"  MTTR:             {(m.Mttr is null ? "n/a (no recoveries)" : $"{m.Mttr.Value.TotalSeconds:F2}s")}");
        Console.WriteLine($"  End-to-end time:  {m.TotalLatency.TotalSeconds:F2}s");
        Console.WriteLine();
    }
}
