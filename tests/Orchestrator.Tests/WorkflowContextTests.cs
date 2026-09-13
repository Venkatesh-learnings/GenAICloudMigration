using Orchestrator.Core;

namespace Orchestrator.Tests;

public class WorkflowContextTests
{
    [Fact]
    public void RecordOutputHash_FirstCallForStage_ReturnsFalse()
    {
        var ctx = new WorkflowContext("run1", "scenario");
        var result = StageResult.Ok("summary", "rationale",
            new Dictionary<string, object> { ["a"] = "1", ["b"] = "2" });

        var changed = ctx.RecordOutputHash("Stage1", result);

        Assert.False(changed);
        Assert.NotNull(ctx.GetLastOutputHash("Stage1"));
    }

    [Fact]
    public void RecordOutputHash_SecondCallWithDifferentOutputs_ReturnsTrue()
    {
        var ctx = new WorkflowContext("run1", "scenario");
        var first = StageResult.Ok("s", "r", new Dictionary<string, object> { ["a"] = "1" });
        var second = StageResult.Ok("s", "r", new Dictionary<string, object> { ["a"] = "999" });

        ctx.RecordOutputHash("Stage1", first);
        var changed = ctx.RecordOutputHash("Stage1", second);

        Assert.True(changed);
    }

    [Fact]
    public void RecordOutputHash_SecondCallWithIdenticalOutputs_ReturnsFalse_RegardlessOfKeyOrder()
    {
        var ctx = new WorkflowContext("run1", "scenario");
        var first = StageResult.Ok("s", "r", new Dictionary<string, object> { ["a"] = "1", ["b"] = "2" });
        // Same content, different insertion order -> hash must be order-independent.
        var second = StageResult.Ok("s", "r", new Dictionary<string, object> { ["b"] = "2", ["a"] = "1" });

        ctx.RecordOutputHash("Stage1", first);
        var changed = ctx.RecordOutputHash("Stage1", second);

        Assert.False(changed);
        Assert.Equal(ctx.GetLastOutputHash("Stage1"), ctx.GetLastOutputHash("Stage1"));
    }

    [Fact]
    public void RecordOutputHash_AppendsToDecisionLineage()
    {
        var ctx = new WorkflowContext("run1", "scenario");
        var result = StageResult.Ok("did the thing", "because reasons", new Dictionary<string, object> { ["x"] = "y" });

        ctx.RecordOutputHash("Stage1", result);

        var record = Assert.Single(ctx.DecisionLineage);
        Assert.Equal("Stage1", record.StageId);
        Assert.Equal("did the thing", record.Summary);
        Assert.Equal("because reasons", record.Rationale);
    }

    [Fact]
    public void Audit_AppendsEntry_WithGivenFields()
    {
        var ctx = new WorkflowContext("run1", "scenario");

        ctx.Audit("StageX", AuditEventType.StageStarted, "kicked off");

        var entry = Assert.Single(ctx.AuditLog);
        Assert.Equal("StageX", entry.StageId);
        Assert.Equal(AuditEventType.StageStarted, entry.EventType);
        Assert.Equal("kicked off", entry.Details);
    }

    [Fact]
    public void RequestSafeStop_SetsFlagAndReason()
    {
        var ctx = new WorkflowContext("run1", "scenario");

        Assert.False(ctx.SafeStopRequested);

        ctx.RequestSafeStop("something bad happened");

        Assert.True(ctx.SafeStopRequested);
        Assert.Equal("something bad happened", ctx.SafeStopReason);
    }

    [Fact]
    public void GetResult_ReturnsNull_WhenStageHasNoResultYet()
    {
        var ctx = new WorkflowContext("run1", "scenario");

        Assert.Null(ctx.GetResult("Unknown"));
    }

    [Fact]
    public void SetResult_ThenGetResult_RoundTrips()
    {
        var ctx = new WorkflowContext("run1", "scenario");
        var result = StageResult.Ok("done");

        ctx.SetResult("Stage1", result);

        Assert.Same(result, ctx.GetResult("Stage1"));
    }
}
