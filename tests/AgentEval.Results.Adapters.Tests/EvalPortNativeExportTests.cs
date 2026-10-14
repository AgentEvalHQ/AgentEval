using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests;

public sealed class EvalPortNativeExportTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("evalport-native");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_root);
    private void Write(params AefResult[] results)
    {
        var writer = AefRunWriter.Create(_root, new()
        {
            RunId = "native", Producer = new() { Name = "native", Version = "1" },
            Subject = new() { Ref = "agent:native", Kind = AefSubjectKind.Agent },
            Suite = new() { Ref = "suite:native", Version = "1" }, StartedAt = Start,
            ContentCapture = AefContentCapture.Off, Execution = new() { TargetMode = AefTargetMode.Mocked },
        });
        writer.SetMetrics([new() { Id = "score", Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Unbounded }]);
        foreach (var result in results) writer.AddResult(result);
        writer.Close(AefRunStatus.Completed, Start.AddSeconds(1)); AefSealer.Seal(_root);
    }
    private static AefResult Result(string path, AefState state, double? value = null) => new()
    {
        CaseId = "case", Path = path, Evaluator = new("code:check"), State = state,
        Reason = state == AefState.NotMeasured ? "not observed" : null,
        Scores = value is null ? null : [new() { Metric = "score", Value = value.Value }],
    };

    [Theory]
    [InlineData(AefState.Passed, 1)]
    [InlineData(AefState.Failed, 0)]
    public void Export_ScorelessAssertion_LabelsBooleanProjection(AefState state, int expected)
    {
        Write(Result("assert", state)); var export = new AefEvalPortExporter().Export(_root);
        var row = export.ResultSet.ToJson()["results"]![0]!; var grader = row["grader_results"]![0]!;
        Assert.Equal(expected, (int)grader["score"]!);
        Assert.Equal("verdict", (string?)grader["metadata"]!["aef"]!["scoreProjection"]);
        Assert.Equal(state == AefState.Passed, (bool)row["passed"]!);
    }

    [Fact]
    public void Export_MultipleRoots_PreservesEachObservationWithoutInventingAttempts()
    {
        Write(Result("first", AefState.Passed, 1), Result("second", AefState.Failed, 0));
        var exporter = new AefEvalPortExporter(); var rows = exporter.Export(_root).ResultSet.ToJson()["results"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual((string?)rows[0]!["test_case_id"], (string?)rows[1]!["test_case_id"]);
        Assert.All(rows, row => { Assert.Null(row!["attempt"]); Assert.Equal("case", (string?)row["metadata"]!["aef"]!["caseId"]); });
        Assert.Equal(rows.ToJsonString(), exporter.Export(_root).ResultSet.ToJson()["results"]!.ToJsonString());
    }

    [Fact]
    public void Export_NonFiniteTampering_RefusesBeforeProjection()
    {
        Write(Result("exceptional", AefState.Scored, 0.5));
        var path = Path.Combine(_root, "results.ndjson");
        var row = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        row["scores"]![0]!["value"] = "NaN";
        File.WriteAllText(path, row.ToJsonString() + "\n");
        Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
    }

    [Fact]
    public void Export_UnnormalizedOutOfRangeScore_RefusesExplicitly()
    {
        Write(Result("score", AefState.Passed, 70));
        Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
    }

    [Fact]
    public void Export_TypedAbsence_PreservesStateInMetadata()
    {
        Write(Result("missing", AefState.NotMeasured));
        var row = new AefEvalPortExporter().Export(_root).ResultSet.ToJson()["results"]![0]!;
        Assert.False((bool)row["passed"]!); Assert.Null(row["grader_results"]![0]!["score"]);
        Assert.Equal("not_measured", (string?)row["grader_results"]![0]!["metadata"]!["aef"]!["state"]);
    }
}
