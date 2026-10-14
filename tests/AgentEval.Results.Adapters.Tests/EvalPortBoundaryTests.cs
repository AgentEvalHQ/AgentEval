using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Json;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests;

public sealed class EvalPortBoundaryTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("evalport-boundary");
    public void Dispose() => AefTestRuns.Delete(_root);
    private void Write(JsonObject? runExt = null, JsonObject? resultExt = null, string? output = null,
        string? pointer = null, string? supplement = null, string? endpoint = null, AefTraceLink? trace = null,
        AefState state = AefState.Passed, string? reason = null)
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var writer = AefRunWriter.Create(_root, new()
        {
            RunId = "native", Producer = new() { Name = "native", Version = "1" },
            Subject = new() { Ref = "agent:native", Kind = AefSubjectKind.Agent },
            Suite = new() { Ref = "suite:native", Version = "1" }, StartedAt = start,
            ContentCapture = AefContentCapture.On, Execution = new() { TargetMode = AefTargetMode.Mocked }, Ext = runExt,
            Deployment = endpoint is null ? null : new() { Ref = "deployment:test", Endpoint = endpoint },
        });
        if (output is not null) writer.AddEvidence(new() { EvidenceId = "E-output", Kind = AefEvidenceKind.Output,
            Link = AefEvidenceLink.ToBlob(writer.PutBlob(Encoding.UTF8.GetBytes(output))) });
        if (supplement is not null)
        {
            var blob = writer.PutBlob(Encoding.UTF8.GetBytes(supplement));
            writer.AddEvidence(new() { EvidenceId = "E-supplement", Kind = AefEvidenceKind.Document, Link = AefEvidenceLink.ToBlob(blob) });
            pointer = new JsonObject { ["schemaVersion"] = "1.0", ["blob"] = blob.Digest }.ToJsonString();
        }
        if (pointer is not null) writer.PutExtFile("evalport-supplement.json", Encoding.UTF8.GetBytes(pointer));
        writer.AddResult(new() { CaseId = "case", Path = "check", Evaluator = new("code:check"), State = state, Reason = reason, TraceLink = trace,
            Ext = resultExt, Evidence = output is null ? null : ["E-output"] });
        writer.Close(AefRunStatus.Completed, start.AddSeconds(1)); AefSealer.Seal(_root);
    }

    [Theory]
    [InlineData(AefState.NotMeasured)]
    [InlineData(AefState.Error)]
    public void Export_NativeEndpointTraceAndReason_PreservesAvailableFields(AefState state)
    {
        var trace = new AefTraceLink("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7");
        Write(endpoint: "https://example.test/api", trace: trace, state: state, reason: "Native explanation");
        var result = new AefEvalPortExporter().Export(_root).ResultSet.ToJson();
        Assert.Equal("https://example.test/api", (string?)result["provider"]!["api_base"]);
        var row = result["results"]![0]!;
        Assert.Equal(trace.TraceId, (string?)row["metadata"]!["openeval.trace_id"]);
        Assert.Equal(trace.SpanId, (string?)row["metadata"]!["aef"]!["traceLink"]!["spanId"]);
        Assert.Equal("Native explanation", (string?)row["grader_results"]![0]!["reason"]);
        if (state == AefState.Error) Assert.Equal("Native explanation", (string?)row["error"]!["message"]);
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("{\"suite_id\":42}", true)]
    [InlineData("42", false)]
    [InlineData("{\"attemptDeclared\":\"false\"}", false)]
    [InlineData("{\"grader_id\":[]}", false)]
    [InlineData("{\"error\":false}", false)]
    public void Export_SealedMalformedOrigin_ReturnsStructuredRefusal(string origin, bool onRun)
    {
        var ext = new JsonObject { ["evalport"] = JsonNode.Parse(origin) };
        Write(runExt: onRun ? ext : null, resultExt: onRun ? null : ext);
        var error = Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
        Assert.Contains("Expected:", error.Message); Assert.Contains("Suggestions:", error.Message);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"2.0\",\"blob\":\"invalid\"}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"blob\":42}")]
    [InlineData("{\"schemaVersion\":\"1.0\"}")]
    public void Export_MalformedSupplementPointer_Refuses(string pointer)
    {
        Write(pointer: pointer);
        Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
    }

    [Fact]
    public void Export_NonHexSupplementDigest_RefusesBeforePathResolution()
    {
        Write(pointer: new JsonObject { ["schemaVersion"] = "1.0", ["blob"] = "sha256:" + new string('/', 64) }.ToJsonString());
        var error = Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
        Assert.Contains("invalid blob reference", error.Message);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"1.0\",\"results\":[{\"test_case_id\":\"different\",\"graders\":[]}]}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"results\":[{\"test_case_id\":\"case\",\"graders\":[{\"grader_id\":\"different\",\"metadata\":{}}]}]}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"results\":[{\"test_case_id\":\"case\",\"graders\":[]},{\"test_case_id\":\"case\",\"graders\":[]}]}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"results\":null}")]
    public void Export_SupplementIdentityMismatchOrShape_Refuses(string supplement)
    {
        Write(supplement: supplement);
        Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
    }

    [Fact]
    public void Export_LargeRepresentableOutput_RemainsIntactAndBelowLimit()
    {
        var output = new string('x', AefLimits.MaxJsonBytes - 2048);
        Write(output: output);
        var export = new AefEvalPortExporter().Export(_root);
        Assert.Equal(output, (string?)export.ResultSet.ToJson()["results"]![0]!["actual_output"]);
        Assert.True(Encoding.UTF8.GetByteCount(export.ResultSet.ToJson().ToJsonString()) <= AefLimits.MaxJsonBytes);
    }

    [Fact]
    public void Export_OutputAtRawBudget_StillRefusesOversizedDocument()
    {
        Write(output: new string('x', AefLimits.MaxJsonBytes));
        Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
    }

    [Fact]
    public void Export_OutputOverRawBudget_RefusesBeforeDecoding()
    {
        Write(output: new string('x', AefLimits.MaxJsonBytes + 1));
        Assert.Throws<AefLimitException>(() => new AefEvalPortExporter().Export(_root));
    }

    [Fact]
    public void Export_EscapingExpansion_EnforcesSerializedLimit()
    {
        Write(output: new string('<', 750_000));
        Assert.Throws<AefInputException>(() => new AefEvalPortExporter().Export(_root));
    }
}
