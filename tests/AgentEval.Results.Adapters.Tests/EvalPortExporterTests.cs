using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Signatures;
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
        "isolation":"custom","group":{"group_id":"sweep","parent_group_id":"campaign","role":"custom-role","label":"display only","sequence":9007199254740993},
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
    public void Export_CapturedSupplement_RestoresSourceDeclarationsAndAttemptMetadata()
    {
        var source = Source();
        source["provider"]!["api_base"] = "https://example.test/api?secret=PRIVATE";
        source["provider"]!["temperature"] = 0.75;
        source["provider"]!["max_tokens"] = 123;
        source["provider"]!["extra"] = new JsonObject { ["token"] = "PRIVATE" };
        source["summary"] = new JsonObject { ["total"] = 999, ["passed"] = 998 };
        source["metadata"] = new JsonObject { ["nested"] = new JsonObject { ["exact"] = JsonNode.Parse("9007199254740993") }, ["aef"] = "source claim" };
        var first = source["results"]![0]!;
        first["metadata"] = new JsonObject { ["label"] = "first" };
        first["grader_results"]![0]!["metadata"] = new JsonObject { ["private"] = "PRIVATE", ["nullable"] = null };
        var second = first.DeepClone(); second["attempt"] = 3; second["metadata"]!["label"] = "third";
        source["results"]!.AsArray().Add(second);
        Import(source);
        var export = new AefEvalPortExporter().Export(_root); var result = export.ResultSet.ToJson();
        foreach (var field in new[] { "provider", "summary" }) Assert.True(JsonNode.DeepEquals(source[field], result[field]), field);
        foreach (var entry in source["metadata"]!.AsObject()) Assert.True(JsonNode.DeepEquals(entry.Value, result["metadata"]![entry.Key]));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal((string?)source["results"]![i]!["metadata"]!["label"], (string?)result["results"]![i]!["metadata"]!["label"]);
            Assert.Equal("PRIVATE", (string?)result["results"]![i]!["grader_results"]![0]!["metadata"]!["private"]);
            Assert.True(result["results"]![i]!["grader_results"]![0]!["metadata"]!.AsObject().ContainsKey("nullable"));
        }
        Assert.Contains(export.Notes, n => n.Contains("source declarations", StringComparison.Ordinal));
        Assert.Contains(export.Notes, n => n.Contains("collisions", StringComparison.Ordinal));
        var nativeSummary = File.ReadAllText(Path.Combine(_root, "summary.json"));
        Assert.DoesNotContain("999", nativeSummary);
    }

    [Fact]
    public void Export_CaptureOff_DoesNotStoreSupplementContentOrDigest()
    {
        var source = Source();
        source["provider"]!["extra"] = new JsonObject { ["secret"] = "PRIVATE-SUPPLEMENT" };
        source["metadata"] = new JsonObject { ["secret"] = "PRIVATE-SUPPLEMENT" };
        Import(source, AefContentCapture.Off);
        Assert.False(File.Exists(Path.Combine(_root, "ext", "evalport-supplement.json")));
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE-SUPPLEMENT", File.ReadAllText(file));
        var result = new AefEvalPortExporter().Export(_root).ResultSet.ToJson();
        Assert.Null(result["provider"]!["extra"]); Assert.Null(result["metadata"]!["secret"]);
    }

    [Fact]
    public void Export_WithheldCorpus_RequiresAuthorityAndExplicitBaseSelection()
    {
        var fixture = Path.Combine(AefTestRuns.RepoRoot, "contracts", "aef", "1", "conformance", "runs", "withheld-blob");
        var run = Path.Combine(fixture, "run");
        var policy = TrustPolicy.Parse(File.ReadAllBytes(Path.Combine(fixture, "policy.json")));
        var exporter = new AefEvalPortExporter();
        Assert.Throws<AefInputException>(() => exporter.Export(run, new() { IgnoreOverlays = true }));
        Assert.Throws<AefInputException>(() => exporter.Export(run, new() { Policy = policy }));
        var export = exporter.Export(run, new() { Policy = policy, IgnoreOverlays = true });
        Assert.Contains(export.Notes, n => n.Contains("withheld", StringComparison.Ordinal));
        Assert.Contains(export.Notes, n => n.Contains("base evidence", StringComparison.Ordinal));
        var graders = export.ResultSet.ToJson()["results"]!.AsArray().SelectMany(r => r!["grader_results"]!.AsArray());
        var helpfulness = Assert.Single(graders, g => (string?)g!["grader_id"] == "triage/helpfulness");
        Assert.Null(helpfulness!["reason"]);
    }

    [Fact]
    public void Export_CancelledBeforeCapture_DoesNotReadMissingDirectory()
    {
        Assert.Throws<OperationCanceledException>(() => new AefEvalPortExporter().Export(_root, cancellationToken: new(true)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Export_TraceIdentity_RoundtripsThroughNativeFieldWithCaptureOff()
    {
        const string trace = "4bf92f3577b34da6a3ce929d0e0e4736";
        var source = Source(); source["results"]![0]!["metadata"] = new JsonObject { ["openeval.trace_id"] = trace };
        Import(source, AefContentCapture.Off);
        Assert.Equal(trace, (string?)AefTestRuns.Results(_root)[0]["traceLink"]!["traceId"]);
        Assert.Equal(trace, (string?)new AefEvalPortExporter().Export(_root).ResultSet.ToJson()["results"]![0]!["metadata"]!["openeval.trace_id"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"short\"")]
    [InlineData("\"4BF92F3577B34DA6A3CE929D0E0E4736\"")]
    public void Import_InvalidDeclaredTrace_RefusesBeforeWriting(string value)
    {
        var source = Source(); source["results"]![0]!["metadata"] = new JsonObject { ["openeval.trace_id"] = JsonNode.Parse(value) };
        Assert.Throws<AefInputException>(() => Import(source));
        Assert.False(Directory.Exists(_root));
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
