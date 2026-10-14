using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests;

public sealed class EvalPortExporterTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("evalport-export");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static JsonObject Source() => JsonNode.Parse("""
        {"version":"1.0.0-rc.5","suite_id":"suite","suite_version":"v1","run_id":"run",
        "runner":{"name":"source-runner","version":"3"},"provider":{"model":"model-name"},
        "started_at":"2026-10-01T00:00:00Z","completed_at":"2026-10-01T00:01:00Z",
        "isolation":"custom","group":{"group_id":"sweep","sequence":9007199254740993},
        "results":[{"test_case_id":"case","passed":false,"actual_output":"answer",
        "error":{"type":"provider_error","message":"error-detail","code":"E1","retryable":true},
        "grader_results":[{"grader_id":"judge","type":"llm","score":0.25,"passed":false,"reason":"reason"}]}]}
        """)!.AsObject();
    private void Import(JsonObject source, AefContentCapture capture = AefContentCapture.On) =>
        new AefEvalPortImporter().Import(Encoding.UTF8.GetBytes(source.ToJsonString()), _root,
            new() { TargetMode = AefTargetMode.Replayed, ContentCapture = capture });

    [Fact]
    public void Export_ImportedRun_RestoresCapturedFieldsAndExactContext()
    {
        var source = Source(); Import(source);
        var export = new AefEvalPortExporter().Export(_root); var result = export.ResultSet.ToJson();
        foreach (var field in new[] { "run_id", "suite_id", "suite_version", "provider", "runner", "isolation", "group" })
            Assert.True(JsonNode.DeepEquals(source[field], result[field]), field);
        var before = source["results"]![0]!; var after = result["results"]![0]!;
        Assert.True(JsonNode.DeepEquals(before["error"], after["error"]));
        Assert.Equal("answer", (string?)after["actual_output"]);
        Assert.Equal("reason", (string?)after["grader_results"]![0]!["reason"]);
        Assert.Equal("judge", (string?)after["grader_results"]![0]!["grader_id"]);
        Assert.Equal(0.25, (double)after["grader_results"]![0]!["score"]!);
        Assert.False(after.AsObject().ContainsKey("attempt"));
        var reference = AefTestRuns.ReferenceVerify(_root);
        if (reference is not null) Assert.Equal("intact", (string?)reference["outcome"]);
    }

    [Fact]
    public void Export_RepeatedAttempts_OmitsRollupsAndPreservesAttemptPresence()
    {
        var source = Source(); var second = source["results"]![0]!.DeepClone(); second["attempt"] = 3;
        source["results"]!.AsArray().Add(second); Import(source);
        var export = new AefEvalPortExporter().Export(_root); var rows = export.ResultSet.ToJson()["results"]!.AsArray();
        Assert.Equal(2, rows.Count); Assert.False(rows[0]!.AsObject().ContainsKey("attempt"));
        Assert.Equal(3, (int)rows[1]!["attempt"]!);
        Assert.Contains(export.Notes, n => n.Contains("rollups", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_CaptureOff_OmitsContentAndReportsErrorLoss()
    {
        Import(Source(), AefContentCapture.Off);
        var export = new AefEvalPortExporter().Export(_root); var row = export.ResultSet.ToJson()["results"]![0]!;
        Assert.Null(row["actual_output"]); Assert.Null(row["error"]!["message"]);
        Assert.Equal("provider_error", (string?)row["error"]!["type"]);
        Assert.Null(row["grader_results"]![0]!["reason"]);
        Assert.Contains(export.Notes, n => n.Contains("message/code", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_ChangedEvidenceOrWrongPin_Refuses()
    {
        Import(Source()); var exporter = new AefEvalPortExporter();
        Assert.Throws<AefInputException>(() => exporter.Export(_root, new() { ExpectedRunHash = new string('0', 64) }));
        File.AppendAllText(Path.Combine(_root, "run.json"), " ");
        Assert.Throws<AefInputException>(() => exporter.Export(_root));
    }
}
