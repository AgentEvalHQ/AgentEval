using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests;

public sealed class EvalPortAggregationTests
{
    private static JsonObject Result(string? policy, bool passed, params (double? Score, bool Passed)[] grades) => new()
    {
        ["test_case_id"] = "case", ["passed"] = passed,
        ["metadata"] = policy is null ? new JsonObject() : new JsonObject { ["openeval.aggregation"] = JsonNode.Parse(policy) },
        ["grader_results"] = new JsonArray(grades.Select((g, i) => (JsonNode)new JsonObject
        { ["grader_id"] = "g" + i, ["type"] = "code", ["score"] = g.Score, ["passed"] = g.Passed }).ToArray()),
    };

    [Theory]
    [InlineData("all", false)]
    [InlineData("any", true)]
    [InlineData("majority", false)]
    public void Assess_DeclaredPolicy_ExcludesNullScoresAndRejectsOppositeVerdict(string strategy, bool expected)
    {
        var row = Result("{\"strategy\":\"" + strategy + "\"}", expected, (0.9, true), (0.2, false), (null, false));
        var report = EvalPortAggregation.Assess(row, null);
        Assert.Equal("verified", (string?)report["status"]); Assert.Equal(2, (int)report["measured"]!);
        row["passed"] = !expected;
        Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(row, null));
    }

    [Fact]
    public void Assess_MissingSuiteContext_ReportsUnverifiableAndExplicitContextChecksDefault()
    {
        var row = Result(null, true, (0.9, true), (0.2, false));
        Assert.Equal("unverifiable", (string?)EvalPortAggregation.Assess(row, null)["status"]);
        Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(row, new()));
        Assert.Equal("verified", (string?)EvalPortAggregation.Assess(row, new() { Strategy = "any" })["status"]);
        row["metadata"]!["openeval.aggregation"] = new JsonObject { ["strategy"] = "any" };
        Assert.Equal("verified", (string?)EvalPortAggregation.Assess(row, new() { Strategy = "all" })["status"]);
    }

    [Fact]
    public void Assess_Weighted_RequiresCompleteMeasuredWeightsAndAvoidsOverflow()
    {
        var row = Result("{\"strategy\":\"weighted\",\"threshold\":0.5}", true, (0.75, false), (0.25, false), (null, false));
        Assert.Equal("unverifiable", (string?)EvalPortAggregation.Assess(row, null)["status"]);
        Assert.Equal("unverifiable", (string?)EvalPortAggregation.Assess(row, new() { GraderWeights = new Dictionary<string, double> { ["g0"] = 1 } })["status"]);
        var context = new EvalPortAggregationContext { GraderWeights = new Dictionary<string, double> { ["g0"] = double.MaxValue, ["g1"] = double.MaxValue } };
        var report = EvalPortAggregation.Assess(row, context);
        Assert.Equal("verified", (string?)report["status"]); Assert.Equal(0.5, (double)report["score"]!);
        Assert.Equal(2, report["callerSuppliedWeights"]!.AsObject().Count);
        row["passed"] = false; Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(row, context));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"strategy\":\"weighted\"}")]
    [InlineData("{\"strategy\":\"unknown\"}")]
    [InlineData("{\"strategy\":\"majority\",\"threshold\":2}")]
    public void Assess_MalformedPolicy_Refuses(string policy) =>
        Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(Result(policy, false, (0.2, false)), null));

    [Fact]
    public void Assess_ExtendedPolicyAndExecutionError_DoNotCertifyIncompleteInterpretation()
    {
        var row = Result("{\"strategy\":\"all\",\"custom\":true}", true, (0.2, false));
        Assert.Equal("unverifiable", (string?)EvalPortAggregation.Assess(row, null)["status"]);
        row["metadata"]!["openeval.aggregation"]!.AsObject().Remove("custom"); row["error"] = new JsonObject();
        Assert.Equal("unverifiable", (string?)EvalPortAggregation.Assess(row, null)["status"]);
    }

    [Fact]
    public void Assess_MajorityCutoff_UsesStrictComparisonAndAllNullNeverPasses()
    {
        var row = Result("{\"strategy\":\"majority\",\"threshold\":0.75}", false,
            (1, true), (1, true), (1, true), (0, false), (null, false));
        Assert.Equal("verified", (string?)EvalPortAggregation.Assess(row, null)["status"]);
        row["passed"] = true; Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(row, null));
        var unscored = Result("{\"strategy\":\"all\"}", false, (null, false));
        Assert.Equal("verified", (string?)EvalPortAggregation.Assess(unscored, null)["status"]);
        unscored["passed"] = true; Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(unscored, null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void Assess_InvalidWeight_Refuses(double weight)
    {
        var row = Result("{\"strategy\":\"weighted\",\"threshold\":0.5}", true, (0.75, true));
        Assert.Throws<AefInputException>(() => EvalPortAggregation.Assess(row,
            new() { GraderWeights = new Dictionary<string, double> { ["g0"] = weight } }));
    }

    [Fact]
    public void Assess_ZeroTotalWeight_DoesNotInventVerdict()
    {
        var row = Result("{\"strategy\":\"weighted\",\"threshold\":0.5}", true, (0.75, true));
        Assert.Equal("unverifiable", (string?)EvalPortAggregation.Assess(row,
            new() { GraderWeights = new Dictionary<string, double> { ["g0"] = 0 } })["status"]);
    }

    [Fact]
    public void Import_AggregationChecks_AreStoredWithoutContentAndContradictionsDoNotPublish()
    {
        var path = AefTestRuns.TempPath("aggregation");
        var row = Result("{\"strategy\":\"any\"}", true, (0.2, false), (0.9, true));
        var source = new JsonObject { ["version"] = "1.0.0-rc.5", ["run_id"] = "run", ["suite_id"] = "suite", ["suite_version"] = "1",
            ["provider"] = new JsonObject { ["model"] = "model" }, ["started_at"] = "2026-10-01T00:00:00Z", ["completed_at"] = "2026-10-01T00:00:01Z",
            ["results"] = new JsonArray(row) };
        var options = new AefEvalPortImportOptions { TargetMode = AefTargetMode.Replayed, ContentCapture = AefContentCapture.Off, AggregationContext = new() };
        try
        {
            var conversion = new AefEvalPortImporter().Import(Encoding.UTF8.GetBytes(source.ToJsonString()), path, options);
            Assert.Contains("evalport.aggregationContext", conversion.Asserted);
            Assert.Equal("verified", (string?)AefTestRuns.Results(path)[0]["ext"]!["evalport"]!["aggregationCheck"]!["status"]);
            row["passed"] = false;
            Assert.Throws<AefInputException>(() => new AefEvalPortImporter().Import(Encoding.UTF8.GetBytes(source.ToJsonString()), path + "-invalid", options));
            Assert.False(Directory.Exists(path + "-invalid"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
