using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Integrity;
using AgentEval.Results.Interop;
using AgentEval.Results.Runs;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests;

public sealed class EvalPortImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-evalport-import-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static byte[] Source(bool repeats = false, bool completed = true)
    {
        var root = JsonNode.Parse("""
          {"version":"1.0.0-rc.5","suite_id":"suite","suite_version":"v1","run_id":"run",
           "started_at":"2026-10-01T00:00:00Z","provider":{"model":"test"},"isolation":"fresh",
           "group":{"group_id":"sweep","sequence":9},"results":[{"test_case_id":"a","passed":false,
           "actual_output":"PRIVATE_OUTPUT","grader_results":[
           {"grader_id":"g/1","type":"llm","score":0.2,"passed":false,"reason":"PRIVATE_REASON"},
           {"grader_id":"g2","type":"code","score":null,"passed":false}]}]}
          """)!.AsObject();
        if (completed) root["completed_at"] = "2026-10-01T00:01:00Z";
        if (repeats)
        {
            var result = root["results"]![0]!; result["attempt"] = 1;
            var second = result.DeepClone(); second["attempt"] = 3;
            second["passed"] = true; second["grader_results"]![0]!["passed"] = true;
            root["results"]!.AsArray().Add(second);
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
    private static AefEvalPortImportOptions Options(AefContentCapture capture = AefContentCapture.On) =>
        new() { TargetMode = AefTargetMode.Replayed, ContentCapture = capture };

    [Fact]
    public void Import_ClosedComposite_WritesIntactRunAndVerifiedContext()
    {
        var result = new AefEvalPortImporter().Import(Source(), _root, Options());
        Assert.Equal(AefOutcome.Intact, result.Verification.Outcome);
        Assert.NotNull(result.Seal);
        Assert.Equal("fresh", new EvalPortRunContextReader().Read(_root).Context!.Isolation);
        var rows = File.ReadAllLines(Path.Combine(_root, "results.ndjson")).Select(x => JsonNode.Parse(x)!).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal("Own", (string?)rows[0]["aggregation"]!["strategy"]);
        Assert.Equal("failed", (string?)rows[0]["state"]);
        Assert.Equal("not_measured", (string?)rows[2]["state"]);
        Assert.Equal(0, (int)rows[1]["component"]!["weight"]!);
    }

    [Fact]
    public void Import_RepeatedAttempts_WritesObservedTrialsAndExplicitRollupTree()
    {
        var result = new AefEvalPortImporter().Import(Source(repeats: true), _root, Options());
        Assert.Equal(AefOutcome.Intact, result.Verification.Outcome);
        var rows = File.ReadAllLines(Path.Combine(_root, "results.ndjson")).Select(x => JsonNode.Parse(x)!).ToArray();
        Assert.Equal(9, rows.Length);
        Assert.Equal(new[] { 0, 2 }, rows.Where(x => x["parentResultId"] is null && x["trial"] is not null).Select(x => (int)x["trial"]!).ToArray());
        var rollup = Assert.Single(rows, x => x["parentResultId"] is null && x["trials"] is not null);
        Assert.Equal(2, (int)rollup["trials"]!["n"]!); Assert.Equal(1, (int)rollup["trials"]!["passed"]!);
        Assert.Equal("failed", (string?)rollup["state"]);
    }

    [Fact]
    public void Import_CaptureOff_DoesNotPersistAnswerOrReasoning()
    {
        new AefEvalPortImporter().Import(Source(), _root, Options(AefContentCapture.Off));
        foreach (var path in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_", File.ReadAllText(path));
    }

    [Fact]
    public void Import_RunningSource_DoesNotInventCompletionOrSeal()
    {
        var result = new AefEvalPortImporter().Import(Source(completed: false), _root, Options());
        Assert.Equal(AefOutcome.Unsealed, result.Verification.Outcome); Assert.Null(result.Seal);
        Assert.Equal("running", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "run.json")))!["status"]);
    }

    [Fact]
    public void Import_ExecutionError_RetainsStructuredDetailsAsCitedBlob()
    {
        var source = JsonNode.Parse(Source())!;
        source["results"]![0]!["error"] = new JsonObject
        { ["type"] = "provider_error", ["message"] = "PRIVATE_ERROR", ["code"] = "PRIVATE_CODE", ["retryable"] = true };
        var result = new AefEvalPortImporter().Import(Encoding.UTF8.GetBytes(source.ToJsonString()), _root, Options());
        Assert.Equal(AefOutcome.Intact, result.Verification.Outcome);
        var root = JsonNode.Parse(File.ReadLines(Path.Combine(_root, "results.ndjson")).First())!;
        Assert.Equal("error", (string?)root["state"]);
        Assert.Equal("provider_error", (string?)root["ext"]!["evalport"]!["error"]!["type"]);
        Assert.Equal(2, root["evidence"]!.AsArray().Count);
        var evidenceId = (string)root["ext"]!["evalport"]!["errorEvidence"]!;
        var evidence = File.ReadLines(Path.Combine(_root, "evidence.ndjson")).Select(x => JsonNode.Parse(x)!)
            .Single(x => (string?)x["evidenceId"] == evidenceId);
        var digest = ((string)evidence["link"]!["blob"]!)[7..];
        var blob = Path.Combine(_root, AefRunFolder.BlobPath(digest));
        var captured = JsonNode.Parse(File.ReadAllText(blob));
        Assert.True(JsonNode.DeepEquals(source["results"]![0]!["error"], captured));
    }

    [Fact]
    public void Import_CaptureOffError_RetainsClassificationWithoutSensitiveDetails()
    {
        var source = JsonNode.Parse(Source())!;
        source["results"]![0]!["error"] = new JsonObject
        { ["type"] = "timeout", ["message"] = "PRIVATE_ERROR", ["code"] = "PRIVATE_CODE", ["retryable"] = false };
        new AefEvalPortImporter().Import(Encoding.UTF8.GetBytes(source.ToJsonString()), _root, Options(AefContentCapture.Off));
        foreach (var path in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_", File.ReadAllText(path));
        var root = JsonNode.Parse(File.ReadLines(Path.Combine(_root, "results.ndjson")).First())!;
        Assert.Equal("timeout", (string?)root["ext"]!["evalport"]!["error"]!["type"]);
        Assert.True((bool)root["ext"]!["evalport"]!["error"]!["contentOmitted"]!);
    }

    [Fact]
    public void Import_MixedImplicitAndExplicitAttempts_PreservesOriginalDeclaration()
    {
        var source = JsonNode.Parse(Source(repeats: true))!;
        source["results"]![0]!.AsObject().Remove("attempt");
        new AefEvalPortImporter().Import(Encoding.UTF8.GetBytes(source.ToJsonString()), _root, Options());
        var roots = File.ReadLines(Path.Combine(_root, "results.ndjson")).Select(x => JsonNode.Parse(x)!)
            .Where(x => x["parentResultId"] is null && x["trial"] is not null).ToArray();
        Assert.False((bool)roots[0]["ext"]!["evalport"]!["attemptDeclared"]!);
        Assert.True((bool)roots[1]["ext"]!["evalport"]!["attemptDeclared"]!);
    }

    [Fact]
    public void Import_LateWriterRefusal_RestoresExistingEmptyDestination()
    {
        Directory.CreateDirectory(_root);
        var source = JsonNode.Parse(Source())!;
        source["results"]![0]!["test_case_id"] = new string('x', 257);
        Assert.Throws<AefInputException>(() => new AefEvalPortImporter().Import(
            Encoding.UTF8.GetBytes(source.ToJsonString()), _root, Options()));
        Assert.True(Directory.Exists(_root));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Import_PreCanceled_DoesNotCreateDestination()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new AefEvalPortImporter().Import(
            Source(), _root, Options(), cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Import_InvalidInputAndExistingOutput_PreservesExistingFiles()
    {
        Assert.Throws<AefInputException>(() => new AefEvalPortImporter().Import("{}"u8.ToArray(), _root, Options()));
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, "keep"), "original");
        Assert.ThrowsAny<ArgumentException>(() => new AefEvalPortImporter().Import(Source(), _root, Options()));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "keep")));
    }
}
