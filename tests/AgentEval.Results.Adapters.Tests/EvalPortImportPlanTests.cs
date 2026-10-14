using System.Text;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests;

public class EvalPortImportPlanTests
{
    private static EvalPortResultSet Source(string additional = "") => EvalPortResultSet.Parse(Encoding.UTF8.GetBytes("""
        {"version":"1.0.0-rc.5","suite_id":"suite","run_id":"run with spaces","started_at":"2026-10-01T02:00:00.123456789+02:00",
        "provider":{"model":"test model"},"results":[{"test_case_id":"case","passed":false,
        "grader_results":[{"grader_id":"g","type":"code","score":0,"passed":false}]}]
        """ + additional + "}"));
    private static AefEvalPortImportOptions Options() => new() { TargetMode = AefTargetMode.Replayed, SuiteVersion = "v1", Seal = false };

    [Fact]
    public void Create_MissingCompletion_KeepsRunningAndExplicitAssertions()
    {
        var plan = EvalPortImportPlan.Create(Source(), Options());
        Assert.Equal(AefRunStatus.Running, plan.Status); Assert.Null(plan.EndedAt);
        Assert.Equal("2026-10-01T00:00:00.123456789Z", plan.Header.StartedAt.ToString());
        Assert.Equal("model:test%20model", plan.Header.Subject.Ref);
        Assert.StartsWith("evalport-", plan.Header.RunId);
        Assert.Equal("run with spaces", (string?)plan.Header.Ext!["evalport"]!["run_id"]);
        Assert.Contains("suite.version", plan.Header.Imported!.Asserted);
        Assert.Contains("execution.targetMode", plan.Header.Imported.Asserted);
    }

    [Fact]
    public void Create_CompletedPartial_ReportsAbortedWithoutInventingEndTime()
    {
        var plan = EvalPortImportPlan.Create(Source(",\"completed_at\":\"2026-10-01T00:01:00Z\",\"metadata\":{\"openeval.partial\":true}"), Options());
        Assert.Equal(AefRunStatus.Aborted, plan.Status); Assert.NotNull(plan.AbortReason);
        Assert.Equal("2026-10-01T00:01:00Z", plan.EndedAt!.Value.ToString());
    }

    [Fact]
    public void Create_MissingOrConflictingSuiteVersion_Refuses()
    {
        Assert.Throws<AefInputException>(() => EvalPortImportPlan.Create(Source(), Options() with { SuiteVersion = null }));
        Assert.Throws<AefInputException>(() => EvalPortImportPlan.Create(Source(",\"suite_version\":\"v2\""), Options()));
        Assert.Throws<AefInputException>(() => EvalPortImportPlan.Create(Source(",\"completed_at\":\"2025-10-01T00:00:00Z\""), Options()));
    }

    [Theory]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00-00:00")]
    [InlineData("2026-01-01T00:00:00.1234567890Z")]
    [InlineData("2026-01-01T00:00:00+24:00")]
    [InlineData("2026-01-01T00:00:00")]
    public void Parse_InvalidOrAmbiguousTimestamp_Refuses(string timestamp) =>
        Assert.Throws<AefInputException>(() => EvalPortTimestamp.Parse(timestamp));

    [Fact]
    public void Parse_NanosecondOffset_PreservesExactInstant()
    {
        Assert.Equal("2026-01-01T00:30:00.000000001Z", EvalPortTimestamp.Parse("2025-12-31t23:00:00.000000001-01:30").ToString());
    }
}
