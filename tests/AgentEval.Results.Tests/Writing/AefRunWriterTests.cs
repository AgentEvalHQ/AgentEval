using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Writing;

/// <summary>
/// The run writer (spec 03): round trips through the run verifier, the encoding of what it writes (spec 02), and each
/// refusal (the writer never writes what a verifier would report).
/// </summary>
public class AefRunWriterTests
{
    // ------------------------------------------------------------------ round trips

    [Fact]
    public void ARunJustCreated_IsAValidOpenRun()
    {
        using var run = new WriterRun();
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header());

        Assert.Equal(AefRunStatus.Running, writer.Status);
        Assert.Equal(["metrics.json", "results.ndjson", "run.json"], AefRunFolder.Open(run.Dir).Files);
        Assert.Equal("running", run.Json("run.json")["status"]!.GetValue<string>());
        Assert.False(run.Json("run.json").ContainsKey("endedAt"));
        var verification = run.Verify();
        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Empty(verification.Problems);
    }

    [Fact]
    public void Write_Verify_Seal_Verify_Sign_Verify()
    {
        using var unsigned = new WriterRun();
        var closed = unsigned.WriteSmall();
        Assert.Equal(AefOutcome.Unsealed, closed.Outcome);
        Assert.Empty(closed.Problems);

        AefSealer.Seal(unsigned.Dir, WriterRun.Options());
        Assert.Equal(AefOutcome.Intact, unsigned.Verify().Outcome);
        Assert.Empty(unsigned.Verify().Problems);

        using var signed = new WriterRun();
        signed.WriteSmall();
        using var alice = EcdsaP256Signer.Generate();
        var sealedRun = AefSealer.Seal(signed.Dir, WriterRun.Options(alice));

        var verification = signed.Verify(WriterRun.Policy(alice));
        Assert.True(sealedRun.Signed);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        Assert.Equal([WriterRun.Alice], verification.SignedBy);
        Assert.Equal(sealedRun.RunHash, verification.RunHash!.Value.Value);
    }

    [Fact]
    public void Close_ReturnsTheVerifiersReport_AndWritesTheClosedRun()
    {
        using var run = new WriterRun();
        var verification = run.WriteSmall();

        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Equal(WriterRun.RunId, verification.RunId);
        var header = run.Json("run.json");
        Assert.Equal("completed", header["status"]!.GetValue<string>());
        Assert.Equal("2026-10-01T10:05:00Z", header["endedAt"]!.GetValue<string>());
        Assert.Equal(["metrics.json", "results.ndjson", "run.json", "summary.json"], AefRunFolder.Open(run.Dir).Files);
    }

    [Fact]
    public void AnAbortedRun_HasItsReason_AndAnEmptyResultsFileIsValid()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        var verification = writer.Close(AefRunStatus.Aborted, WriterRun.Start.AddMinutes(1), "out of budget");

        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Empty(run.Read("results.ndjson"));
        Assert.Equal("out of budget", run.Json("run.json")["abortReason"]!.GetValue<string>());
    }

    [Fact]
    public void EveryFile_IsUtf8WithoutABom_LfOnly_WithIntegersInPlainDigits()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", m: 2.0));
        writer.AddResult(new AefResult
        {
            CaseId = "kü", Path = "q/ü", Evaluator = new AefEvaluator("code:q"), State = AefState.Passed,
            Scores = [new AefScore { Metric = "m", Value = 1 }], DurationMs = 1500.0, Turns = 2,
        });
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])] });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        foreach (var (path, bytes) in run.Snapshot())
        {
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), $"{path} has a byte-order mark");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.True(bytes.Length == 0 || bytes[^1] == (byte)'\n', $"{path} does not end in LF");
        }

        var text = Encoding.UTF8.GetString(run.Read("results.ndjson"));
        Assert.Contains("{\"metric\":\"m\",\"value\":2}", text, StringComparison.Ordinal);   // 2.0 written as 2
        Assert.Contains("\"durationMs\":1500,", text, StringComparison.Ordinal);
        Assert.Contains("\"caseId\":\"kü\"", text, StringComparison.Ordinal);   // UTF-8, not \u escapes
        Assert.Contains("\"N\": 1,\n", Encoding.UTF8.GetString(run.Read("summary.json")), StringComparison.Ordinal);   // indented by two spaces, LF
    }

    [Fact]
    public void AnOwnComposite_IsWrittenAndVerifies_AndAReaderTakesAStrategyItDoesNotKnow()
    {
        // [RES-6] (W5b-3): Own, the node's own verdict with its children recorded beside it at weight 0. The strategy is
        // descriptive: a reader takes any string there (an open value), a writer only the ones its schema lists.
        using var run = new WriterRun();
        var writer = run.Create();
        var scenario = writer.AddResult(WriterRun.Leaf("k1", "scenario", AefState.Failed, m: 0.6) with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Own, RulePath = AefRulePath.Threshold, Threshold = 0.8, Score = 0.6, Measured = 2, Total = 2 },
        });
        scenario.AddChild(WriterRun.Leaf("k1", "scenario/assertions/1", m: null) with { Component = new AefComponent(0, false) });
        scenario.AddChild(WriterRun.Leaf("k1", "scenario/assertions/2", AefState.Failed, m: null) with { Component = new AefComponent(0, false) });

        var verification = writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        Assert.Empty(verification.Problems);
        var line = run.Lines("results.ndjson")[0];
        Assert.Equal("Own", line["aggregation"]!["strategy"]!.GetValue<string>());
        line["aggregation"]!["strategy"] = "SomeLaterStrategy";
        Assert.True(AefSchemas.Reader.IsValid("result", line));
        Assert.False(AefSchemas.Writer.IsValid("result", line));
    }

    [Fact]
    public void ResultIds_AreRes4s_AndChildrenNameTheirParent()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var parent = writer.AddResult(WriterRun.Leaf("k1") with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1, Decisive = [writer.ResultIdOf("k1", "q/a")] },
        });
        var child = parent.AddChild(WriterRun.Leaf("k1", "q/a") with { Component = new AefComponent(1, true) });
        var trial = writer.AddResult(WriterRun.Leaf("k2", trial: 0));

        Assert.Equal(AefResultId.Compute(WriterRun.RunId, "k1", "q"), parent.ResultId);
        Assert.Equal(AefResultId.Compute(WriterRun.RunId, "k1", "q/a"), child.ResultId);
        Assert.Equal(parent.ResultId, child.ParentResultId);
        Assert.Equal(AefResultId.Compute(WriterRun.RunId, "k2", "q", 0), trial.ResultId);
        var lines = run.Lines("results.ndjson");
        Assert.Equal(parent.ResultId, lines[1]["parentResultId"]!.GetValue<string>());
        Assert.False(lines[0].ContainsKey("parentResultId"));   // a root has none (never null)
    }

    [Fact]
    public void TheSummary_IsComputedFromTheLines_AsTheCalculatorDefinesIt()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.SetMetrics([WriterRun.Metric(), WriterRun.Metric("pass", AefMetricKind.Rate), WriterRun.Metric("count", AefMetricKind.Count)]);
        writer.AddResult(WriterRun.Leaf("k1", m: 0.4, state: AefState.Failed));
        writer.AddResult(WriterRun.Leaf("k2", m: 0.9));
        writer.AddResult(WriterRun.Leaf("k3", m: null, state: AefState.Skipped));
        writer.AddResult(WriterRun.Leaf("k4", m: null, state: AefState.NotApplicable));
        writer.AddResult(WriterRun.Leaf("k5", m: 0.1, trial: 0));   // a trial line is not counted
        writer.AddResult(WriterRun.Leaf("k5", m: 0.7) with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true) });
        var seen = new List<AefSummaryFigures>();
        writer.SetSummary(new AefSummary
        {
            Lanes =
            [
                new AefSummaryLane("main",
                [
                    new AefSummaryEntry { Metric = "m", Path = "q", Decide = f => { seen.Add(f); return new AefSummaryDecision(AefSummaryVerdict.Failed); } },
                    new AefSummaryEntry { Metric = "pass", Path = "q" },
                    new AefSummaryEntry { Metric = "m", Path = "nowhere", Decide = _ => throw new InvalidOperationException("not called when n is 0") },
                ]),
            ],
        });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        var entries = run.Json("summary.json")["lanes"]![0]!["metrics"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal((4, 3, 1), (Int(entries[0], "N"), Int(entries[0], "n"), Int(entries[0], "notMeasured")));   // k1, k2, k5 rollup; k3 skipped; k4 left out
        Assert.Equal((0.4 + 0.9 + 0.7) / 3, entries[0]["value"]!.GetValue<double>(), 12);
        Assert.Equal("failed", entries[0]["verdict"]!.GetValue<string>());
        Assert.Single(seen);
        Assert.Equal(2.0 / 3, entries[1]["value"]!.GetValue<double>(), 12);    // rate: passed k2 and k5 of k1, k2, k5
        Assert.Equal("scored", entries[1]["verdict"]!.GetValue<string>());     // no rule applied ([SUM-6])
        Assert.Equal(0, Int(entries[2], "N"));
        Assert.Null(entries[2]["value"]);
        Assert.Equal("not_measured", entries[2]["verdict"]!.GetValue<string>());
        Assert.Empty(run.Problems());

        static long Int(JsonObject o, string name) => o[name]!.GetValue<long>();
    }

    [Fact]
    public void AProducersAggregate_TakesItsValueFromTheProducer_AndAnAefOneIsComputed()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", m: 0.2) with { Lane = "a" });
        writer.AddResult(WriterRun.Leaf("k2", m: 0.8) with { Lane = "a" });
        writer.AddResult(WriterRun.Leaf("k3", m: 0.6) with { Lane = "a" });
        writer.AddResult(WriterRun.Leaf("k4", m: 0.5) with { Lane = "b" });
        writer.AddResult(WriterRun.Leaf("k5", m: 0.5));   // two lanes: a line without a lane belongs to neither ([SUM-3])
        writer.SetSummary(new AefSummary
        {
            Lanes =
            [
                new AefSummaryLane("a", [new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("median") }]),
                new AefSummaryLane("b", [new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("f1"), Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Passed, Value: 0.77) }]),
            ],
        });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        var lanes = run.Json("summary.json")["lanes"]!.AsArray();
        Assert.Equal(0.6, lanes[0]!["metrics"]![0]!["value"]!.GetValue<double>());
        Assert.Equal(3, lanes[0]!["metrics"]![0]!["N"]!.GetValue<long>());
        Assert.Equal(0.77, lanes[1]!["metrics"]![0]!["value"]!.GetValue<double>());
        Assert.Equal(1, lanes[1]!["metrics"]![0]!["N"]!.GetValue<long>());
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void AProducersAggregate_WithoutAValue_IsRefusedAtClose_AndTheRunStaysOpen()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", m: 0.2));
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("a", [new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("f1") }])] });

        Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Equal(AefRunStatus.Running, writer.Status);
        Assert.Equal("running", run.Json("run.json")["status"]!.GetValue<string>());
        Assert.False(File.Exists(run.Full("summary.json")));

        writer.SetSummary(new AefSummary
        {
            Lanes = [new AefSummaryLane("a", [new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("f1"), Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Scored, Value: 1) }])],
        });
        Assert.Equal(AefOutcome.Unsealed, writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)).Outcome);
    }

    [Fact]
    public void AnAefAggregate_WithAValueFromTheProducer_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1"));
        writer.SetSummary(new AefSummary
        {
            Lanes = [new AefSummaryLane("a", [new AefSummaryEntry { Metric = "m", Path = "q", Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Passed, Value: 1) }])],
        });

        Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
    }

    [Fact]
    public void APendingLine_IsRefusedAtClose_UntilItIsUpdated()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var line = writer.AddResult(WriterRun.Leaf("k1", m: null, state: AefState.Pending));
        Assert.Empty(run.Problems());   // pending is fine in an open run

        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("[RES-3]", refused.Message, StringComparison.Ordinal);

        writer.UpdateResult(line, WriterRun.Leaf("k1", m: 0.5, state: AefState.Failed));
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Equal("failed", run.Lines("results.ndjson").Single()["state"]!.GetValue<string>());
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void UpdateResult_KeepsTheLinesId_AndRefusesAnotherCasePathOrTrial()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var line = writer.AddResult(WriterRun.Leaf("k1"));
        writer.AddResult(WriterRun.Leaf("k2"));

        Assert.Throws<ArgumentException>(() => writer.UpdateResult(line, WriterRun.Leaf("k3")));
        Assert.Throws<ArgumentException>(() => writer.UpdateResult(line, WriterRun.Leaf("k1", trial: 0)));
        writer.UpdateResult(line, WriterRun.Leaf("k1", m: 0.1, state: AefState.Failed));

        var lines = run.Lines("results.ndjson");
        Assert.Equal(2, lines.Count);
        Assert.Equal(line.ResultId, lines[0]["resultId"]!.GetValue<string>());
        Assert.Equal("failed", lines[0]["state"]!.GetValue<string>());
    }

    [Fact]
    public void AfterClose_NothingChanges()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var line = writer.AddResult(WriterRun.Leaf("k1"));
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        var before = run.Snapshot();

        Assert.Throws<InvalidOperationException>(() => writer.AddResult(WriterRun.Leaf("k2")));
        Assert.Throws<InvalidOperationException>(() => writer.UpdateResult(line, WriterRun.Leaf("k1", m: 0.1)));
        Assert.Throws<InvalidOperationException>(() => writer.PutBlob("x"u8));
        Assert.Throws<InvalidOperationException>(() => writer.SetMetrics([WriterRun.Metric()]));
        Assert.Throws<InvalidOperationException>(() => writer.SetSummary(new AefSummary { Lanes = [] }));
        Assert.Throws<InvalidOperationException>(() => writer.PutExtFile("a", "x"u8));
        Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(2)));

        var after = run.Snapshot();
        Assert.Equal(before.Keys, after.Keys);
        Assert.All(before, f => Assert.Equal(f.Value, after[f.Key]));
    }

    [Fact]
    public void Blobs_AreContentAddressed_AndPutOnce()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var bytes = Encoding.UTF8.GetBytes("reasoning: ünïcødé\r\n");

        var blob = writer.PutBlob(bytes);
        var again = writer.PutBlob(new MemoryStream(bytes));

        Assert.Equal(blob, again);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), blob.Sha256);
        Assert.Equal($"blobs/sha256/{blob.Sha256[..2]}/{blob.Sha256}", blob.Path);
        Assert.Equal(bytes, run.Read(blob.Path));
        writer.AddResult(WriterRun.Leaf("k1", state: AefState.Failed, m: 0.1) with { Reasoning = blob });
        writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.JudgeReasoning, Link = AefEvidenceLink.ToBlob(blob) });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        var line = run.Lines("results.ndjson").Single();
        Assert.Equal(blob.Digest, line["reasoning"]!["blob"]!.GetValue<string>());
        Assert.Equal(bytes.Length, line["reasoning"]!["bytes"]!.GetValue<long>());
        Assert.Equal(blob.Digest, run.Lines("evidence.ndjson").Single()["digest"]!.GetValue<string>());   // [EVD-2]
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void TracesAndLogs_AreWritten_AndTraceLinksResolveAgainstThem()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddTraces(Traces(("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7")));
        writer.AddLogs(Logs(body: true));
        writer.AddEvidence(new AefEvidence { EvidenceId = "E-span", Kind = AefEvidenceKind.Span, Link = AefEvidenceLink.ToSpan("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7") });
        writer.AddResult(WriterRun.Leaf("k1") with { TraceLink = new AefTraceLink("4bf92f3577b34da6a3ce929d0e0e4736"), Evidence = ["E-span"] });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        Assert.Empty(run.Problems());
    }

    // ------------------------------------------------------------------ refusals: the folder, the header, the close

    [Fact]
    public void Create_RefusesAFolderThatIsNotEmpty()
    {
        using var run = new WriterRun();
        Directory.CreateDirectory(run.Dir);
        File.WriteAllText(Path.Combine(run.Dir, "notes.txt"), "x");

        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, WriterRun.Header()));
    }

    [Theory]
    [InlineData("run id with spaces")]
    [InlineData("")]
    public void Create_RefusesAHeaderTheWriterSchemaRefuses(string runId)
    {
        using var run = new WriterRun();

        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, WriterRun.Header(runId: runId)));
        Assert.False(Directory.Exists(run.Dir));
    }

    [Fact]
    public void Create_RefusesAnEndpointWithCredentials_Run10()
    {
        using var run = new WriterRun();
        var header = WriterRun.Header() with { Deployment = new AefDeployment { Ref = "deployment:d", Endpoint = "https://example.com/x?api-key=secret" } };

        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, header));
        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, header with { Deployment = new AefDeployment { Ref = "deployment:d", Endpoint = "https://user:pw@example.com/x" } }));
    }

    [Theory]
    [InlineData(5, 3, -1)]     // dangerous errors above n
    [InlineData(1, 3, 1)]      // measured after the run started
    public void Create_RefusesACalibrationSection39Refuses(int dangerous, int n, int hoursAfterStart)
    {
        using var run = new WriterRun();
        var header = WithJudge(new AefCalibration { LabelSet = "labels:x", N = n, DangerousErrors = dangerous, MeasuredAt = WriterRun.Start.AddHours(hoursAfterStart) });

        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, header));
    }

    [Fact]
    public void Create_RefusesRequirePassesAboveTrialsPerCase()
    {
        using var run = new WriterRun();
        var header = WriterRun.Header() with
        {
            Suite = new AefSuite { Ref = "suite:s", Version = "1", ExecutionPolicy = new AefExecutionPolicy { TrialsPerCase = 2, RequirePasses = 3 } },
        };

        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, header));
    }

    [Fact]
    public void Close_RefusesAnEndBeforeTheStart_AndAnAbortReasonOutOfPlace()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddSeconds(-1)));
        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Aborted, WriterRun.Start.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1), "why"));
        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Running, WriterRun.Start.AddMinutes(1)));
        Assert.Equal(AefRunStatus.Running, writer.Status);
    }

    // ------------------------------------------------------------------ refusals: content under contentCapture off

    [Fact]
    public void ContentOff_RefusesReasoning()
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);
        var blob = writer.PutBlob("a compliance artifact, not content"u8);

        var refused = Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Reasoning = blob }));
        Assert.Contains("[RUN-11]", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentOff_RefusesAPromptHash()
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with
        {
            Annotator = new AefAnnotator { Kind = AefAnnotatorKind.Llm, PromptHash = "sha256:" + new string('a', 64) },
        }));
        writer.AddResult(WriterRun.Leaf("k1") with { Annotator = new AefAnnotator { Kind = AefAnnotatorKind.Llm, RubricDigest = "sha256:" + new string('a', 64) } });
    }

    [Theory]
    [InlineData(AefEvidenceKind.JudgeReasoning)]
    [InlineData(AefEvidenceKind.ToolCall)]
    [InlineData(AefEvidenceKind.Document)]
    [InlineData(AefEvidenceKind.Input)]
    [InlineData(AefEvidenceKind.Expected)]
    [InlineData(AefEvidenceKind.Output)]
    [InlineData(AefEvidenceKind.Transcript)]
    public void ContentOff_RefusesContentEvidenceKinds(AefEvidenceKind kind)
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);

        Assert.Throws<ArgumentException>(() => writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = kind, Link = AefEvidenceLink.ToUri("https://example.com/x") }));
    }

    [Theory]
    [InlineData(AefEvidenceKind.Span)]
    [InlineData(AefEvidenceKind.ComplianceArtifact)]
    [InlineData(AefEvidenceKind.Other)]
    public void ContentOff_AcceptsOtherEvidenceKinds(AefEvidenceKind kind)
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);

        writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = kind, Link = AefEvidenceLink.ToUri("https://example.com/x") });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
    }

    [Theory]
    [InlineData("gen_ai.input.messages", false)]
    [InlineData("gen_ai.output.messages", false)]
    [InlineData("gen_ai.system_instructions", false)]
    [InlineData("gen_ai.tool.call.arguments", false)]
    [InlineData("gen_ai.tool.call.result", true)]
    [InlineData("gen_ai.evaluation.explanation", true)]
    [InlineData("gen_ai.prompt", false)]
    [InlineData("gen_ai.completion", true)]
    public void ContentOff_RefusesSpansOrSpanEventsWithContentAttributes(string attribute, bool onAnEvent)
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);
        var traces = Traces(("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"));
        var span = traces["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!.AsObject();
        var holder = onAnEvent ? new JsonObject { ["name"] = "e" } : span;
        holder["attributes"] = new JsonArray(new JsonObject { ["key"] = attribute, ["value"] = new JsonObject { ["stringValue"] = "x" } });
        if (onAnEvent)
        {
            span["events"] = new JsonArray(holder);
        }

        Assert.Throws<ArgumentException>(() => writer.AddTraces(traces));
        Assert.False(File.Exists(run.Full("traces.otlp.jsonl")));
    }

    [Fact]
    public void ContentOff_RefusesALogRecordWithABody_OrAContentAttribute()
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);

        Assert.Throws<ArgumentException>(() => writer.AddLogs(Logs(body: true)));
        var attribute = Logs(body: false);
        attribute["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!["attributes"] =
            new JsonArray(new JsonObject { ["key"] = "gen_ai.evaluation.explanation", ["value"] = new JsonObject { ["stringValue"] = "x" } });
        Assert.Throws<ArgumentException>(() => writer.AddLogs(attribute));
        writer.AddLogs(Logs(body: false));
    }

    [Fact]
    public void ContentOn_AcceptsAllOfIt()
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.On);
        var traces = Traces(("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"));
        traces["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!["attributes"] =
            new JsonArray(new JsonObject { ["key"] = "gen_ai.input.messages", ["value"] = new JsonObject { ["stringValue"] = "hi" } });

        writer.AddTraces(traces);
        writer.AddLogs(Logs(body: true));
        writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Transcript, Link = AefEvidenceLink.ToBlob(writer.PutBlob("t"u8)) });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void Traces_PreOtlp1Names_AndUpperCaseIds_AreRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var old = new JsonObject { ["resourceSpans"] = new JsonArray(new JsonObject { ["instrumentationLibrarySpans"] = new JsonArray() }) };

        Assert.Throws<ArgumentException>(() => writer.AddTraces(old));
        Assert.Throws<ArgumentException>(() => writer.AddTraces(Traces(("4BF92F3577B34DA6A3CE929D0E0E4736", "00f067aa0ba902b7"))));
        Assert.Throws<ArgumentException>(() => writer.AddLogs(new JsonObject { ["resourceLogs"] = new JsonArray(new JsonObject { ["instrumentationLibraryLogs"] = new JsonArray() }) }));
    }

    // ------------------------------------------------------------------ refusals: paths

    [Theory]
    [InlineData("../escape")]
    [InlineData(".DS_Store")]
    [InlineData("a/.hidden")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("data/aux.tar.gz")]
    [InlineData("with space")]
    [InlineData("tilde~")]
    [InlineData("ünïcode")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("back\\slash")]
    public void PutExtFile_RefusesAPathRun3Refuses(string path)
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.PutExtFile(path, "x"u8));
        Assert.False(Directory.Exists(run.Full("ext")) && Directory.EnumerateFiles(run.Full("ext"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void PutExtFile_RefusesAPathLongerThan255Bytes()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        writer.PutExtFile(new string('a', 251), "x"u8);   // ext/ + 251 = 255 bytes
        Assert.Throws<ArgumentException>(() => writer.PutExtFile(new string('b', 252), "x"u8));
    }

    [Theory]
    [InlineData("Data/x", "data/y")]   // folders that differ only in case
    [InlineData("Notes.txt", "notes.txt")]
    public void PutExtFile_RefusesACaseClash(string first, string second)
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.PutExtFile(first, "x"u8);

        Assert.Throws<ArgumentException>(() => writer.PutExtFile(second, "y"u8));
        writer.PutExtFile(first, "rewritten while running"u8);
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Empty(run.Problems());
    }

    // ------------------------------------------------------------------ refusals: numbers, limits, ids

    [Fact]
    public void NaNAndInfinity_AreRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1", m: double.NaN)));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { DurationMs = double.PositiveInfinity }));
        Assert.Throws<ArgumentException>(() => writer.SetMetrics([new AefMetric { Id = "m", Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Between(0, double.NaN) }]));
        Assert.Empty(run.Read("results.ndjson"));

        writer.AddResult(WriterRun.Leaf("k1"));
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("a", [new AefSummaryEntry { Metric = "m", Path = "q", Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Passed, Stderr: double.NaN) }])] });
        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Equal(AefRunStatus.Running, writer.Status);
    }

    [Fact]
    public void ALineAbove4MiB_OrNestedDeeperThan64_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var deep = new JsonObject();
        var inner = deep;
        for (var i = 0; i < 64; i++)
        {
            var next = new JsonObject();
            inner["d"] = next;
            inner = next;
        }

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Ext = new JsonObject { ["x"] = new string('a', AefLimits.MaxJsonBytes) } }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Ext = deep }));
        Assert.Empty(run.Read("results.ndjson"));
    }

    [Fact]
    public void ADuplicateCasePathAndTrial_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1"));
        writer.AddResult(WriterRun.Leaf("k1", trial: 0));
        writer.AddResult(WriterRun.Leaf("k1", "q/a"));

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1", m: 0.1)));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1", trial: 0)));
        Assert.Equal(3, run.Lines("results.ndjson").Count);
    }

    [Fact]
    public void AControlCharacterInACaseOrPath_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k\u001f1")));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1", "q\n")));
    }

    [Fact]
    public void AParentThatIsNoLineOfTheRun_IsRefused()
    {
        using var run = new WriterRun();
        using var other = new WriterRun();
        var writer = run.Create();
        var foreign = other.Create().AddResult(WriterRun.Leaf("k1"));

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1", "q/a") with { Component = new AefComponent(1, true) }, foreign));
        Assert.Throws<ArgumentException>(() => writer.UpdateResult(foreign, WriterRun.Leaf("k1")));
    }

    [Fact]
    public void AChildWithoutAComponent_IsRefused_Res5()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var parent = writer.AddResult(WriterRun.Leaf("k1"));

        Assert.Throws<ArgumentException>(() => parent.AddChild(WriterRun.Leaf("k1", "q/a")));
    }

    [Theory]
    [InlineData(3, 2, null)]       // measured above total
    [InlineData(1, 3, 1L)]         // counts add up to 1, not 2
    [InlineData(1, 2, null)]       // no counts: an absent unmeasured is 0, not total − measured
    public void AggregationCountsThatDoNotAddUp_AreRefused(long measured, long total, long? notMeasured)
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with
        {
            Aggregation = new AefAggregation
            {
                Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = measured, Total = total,
                Unmeasured = notMeasured is null ? null : new AefUnmeasured { NotMeasured = notMeasured },
            },
        }));
    }

    [Fact]
    public void OtherOneLineRulesOfSection39_AreRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Annotator = new AefAnnotator { Kind = AefAnnotatorKind.Hybrid, Panel = new AefPanel(4, 3) } }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Trials = new AefTrials(2, 3, AefTrialAggregation.MajorityVote, true) }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Attack = new AefAttack { Technique = "x", Success = true } }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { StartedAt = WriterRun.Start.AddSeconds(2), EndedAt = WriterRun.Start.AddSeconds(1) }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Usage = [new AefUsage { Role = AefUsageRole.Agent }, new AefUsage { Role = AefUsageRole.Agent }] }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Uncertainty = new AefUncertainty(Ci: new AefInterval(0.6, 0.4, 0.95)) }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Scores = [new AefScore { Metric = "m", Value = 1 }, new AefScore { Metric = "m", Value = 0 }] }));
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1", m: 0.5, state: AefState.Skipped)));   // a typed absence carries no scores ([RES-2])
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Trial = 0, Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true) }));
        Assert.Empty(run.Read("results.ndjson"));
    }

    [Fact]
    public void AResultLinesUsage_NamesARoleAndModelOnce_SoAPanelLineCarriesTwoJudgeModels()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        // [RES-10] after R3-9: no role and model twice (an absent model a value of its own).
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Usage = [new AefUsage { Role = AefUsageRole.Judge, Model = "a" }, new AefUsage { Role = AefUsageRole.Judge, Model = "a" }] }));
        _ = writer.AddResult(WriterRun.Leaf("k1") with { Usage = [new AefUsage { Role = AefUsageRole.Judge, Model = "a" }, new AefUsage { Role = AefUsageRole.Judge, Model = "b" }, new AefUsage { Role = AefUsageRole.Judge }] });

        var written = System.Text.Encoding.UTF8.GetString(run.Read("results.ndjson"));
        Assert.Equal(1, written.Count(c => c == '\n'));
        Assert.Contains("\"model\":\"b\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlobThatIsNotOfThisRun_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var foreign = new AefBlob(new string('a', 64), 3);

        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Reasoning = foreign }));
        Assert.Throws<ArgumentException>(() => writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Other, Link = AefEvidenceLink.ToBlob(foreign) }));
        var blob = writer.PutBlob("abc"u8);
        Assert.Throws<ArgumentException>(() => writer.AddResult(WriterRun.Leaf("k1") with { Reasoning = blob with { Size = 4 } }));
    }

    [Fact]
    public void ADuplicateEvidenceId_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Other, Link = AefEvidenceLink.ToUri("https://example.com/a") });

        Assert.Throws<ArgumentException>(() => writer.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Other, Link = AefEvidenceLink.ToUri("https://example.com/b") }));
        Assert.Throws<ArgumentException>(() => writer.AddEvidence(new AefEvidence { EvidenceId = "not-E", Kind = AefEvidenceKind.Other, Link = AefEvidenceLink.ToUri("https://example.com/b") }));
    }

    [Fact]
    public void AShipOnAnIncomparableComparison_IsRefused_Gate2()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.AddGate(new AefGateDecision
        {
            GateId = "gate:ci", DecisionId = "d-1", Rule = new AefGateRule("baseline"), Inputs = new AefGateInputs(),
            Comparability = AefComparability.Incomparable, Outcome = AefGateOutcome.Ship, DecidedAt = WriterRun.Start,
        }));
    }

    [Fact]
    public void Metrics_DeclaredTwice_OrWithAnInvertedScale_AreRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();

        Assert.Throws<ArgumentException>(() => writer.SetMetrics([WriterRun.Metric(), WriterRun.Metric()]));
        Assert.Throws<ArgumentException>(() => writer.SetMetrics([new AefMetric { Id = "m", Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Between(1, 0) }]));
    }

    [Fact]
    public void ASummary_WithADuplicateLaneMetricAndPath_ALaneTwice_OrAUsageTwice_IsRefused()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var entry = new AefSummaryEntry { Metric = "m", Path = "q" };

        Assert.Throws<ArgumentException>(() => writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("a", [entry, new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("median") }])] }));
        Assert.Throws<ArgumentException>(() => writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("a", [entry]), new AefSummaryLane("a", [])] }));
        Assert.Throws<ArgumentException>(() => writer.SetSummary(new AefSummary
        {
            Lanes = [],
            Usage = [new AefUsage { Role = AefUsageRole.Judge, Model = "j" }, new AefUsage { Role = AefUsageRole.Judge, Model = "j" }],
        }));
        Assert.Throws<ArgumentException>(() => writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("lane with spaces", [])] }));
        Assert.Throws<ArgumentException>(() => writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("a", [new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("Not A Method") }])] }));

        // Two entries for one metric and path in different lanes, and two usage entries of one role with and without a model, are fine.
        writer.SetSummary(new AefSummary
        {
            Lanes = [new AefSummaryLane("a", [entry]), new AefSummaryLane("b", [entry])],
            Usage = [new AefUsage { Role = AefUsageRole.Judge, Model = "j" }, new AefUsage { Role = AefUsageRole.Judge }],
        });
    }

    // ------------------------------------------------------------------ refusals at close: the whole run

    [Fact]
    public void Close_RefusesWhatNeedsTheWholeRun_AndLeavesTheRunOpen()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var parent = writer.AddResult(WriterRun.Leaf("k1") with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1, Decisive = [writer.ResultIdOf("k9", "q")] },
            Evidence = ["E-missing"],
        });
        parent.AddChild(WriterRun.Leaf("k1", "q/a") with { Component = new AefComponent(1, true), Scores = [new AefScore { Metric = "undeclared", Value = 1 }] });
        var childless = writer.AddResult(WriterRun.Leaf("k2"));
        childless.AddChild(WriterRun.Leaf("k2", "q/a") with { Component = new AefComponent(1, true) });   // k2/q has a child and no aggregation
        writer.AddResult(WriterRun.Leaf("k3", trial: 0));                                                    // trials without a rollup
        writer.AddGate(new AefGateDecision
        {
            GateId = "gate:ci", DecisionId = "d-1", Rule = new AefGateRule("fail-on"), Inputs = new AefGateInputs { Results = [writer.ResultIdOf("k8", "q")] },
            Comparability = AefComparability.NotApplicable, Outcome = AefGateOutcome.NoShip, DecidedAt = WriterRun.Start,
        });
        writer.AddTraces(Traces(("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7")));
        writer.AddResult(WriterRun.Leaf("k4") with { TraceLink = new AefTraceLink("4bf92f3577b34da6a3ce929d0e0e4737") });
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("a", [new AefSummaryEntry { Metric = "undeclared-too", Path = "q" }])] });
        var before = run.Snapshot();

        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));

        foreach (var rule in new[] { "[RES-5]", "[RES-6]", "[RES-8]", "§3.9 evidence", "undeclared,", "§3.9 gate", "§3.9 trace-link", "undeclared-too" })
        {
            Assert.Contains(rule, refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(AefRunStatus.Running, writer.Status);
        var after = run.Snapshot();
        Assert.Equal(before.Keys, after.Keys);
        Assert.All(before, f => Assert.Equal(f.Value, after[f.Key]));
    }

    [Fact]
    public void ATotalThatIsNotTheNumberOfChildren_IsRefusedAtClose_Res6()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var parent = writer.AddResult(WriterRun.Leaf("k1") with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 2, Total = 2 },
        });
        parent.AddChild(WriterRun.Leaf("k1", "q/a") with { Component = new AefComponent(1, true) });

        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("total is 2, and it has 1 children", refused.Message, StringComparison.Ordinal);

        parent.AddChild(WriterRun.Leaf("k1", "q/b") with { Component = new AefComponent(1, true) });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void ARollupWhoseCountsAreNotItsTrialLines_IsRefusedAtClose_Res8()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", trial: 0));
        writer.AddResult(WriterRun.Leaf("k1", m: 0.1, state: AefState.Failed, trial: 1));
        var rollup = writer.AddResult(WriterRun.Leaf("k1") with { Trials = new AefTrials(2, 2, AefTrialAggregation.AllPass, true) });

        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("[RES-8]", refused.Message, StringComparison.Ordinal);

        writer.UpdateResult(rollup, WriterRun.Leaf("k1", m: 0.5, state: AefState.Failed) with { Trials = new AefTrials(2, 1, AefTrialAggregation.AllPass, false) });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void ALineUnderATrial_CarriesItsTrial_Res8()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var trial = writer.AddResult(WriterRun.Leaf("k1", trial: 0) with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1 },
        });

        Assert.Throws<ArgumentException>(() => trial.AddChild(WriterRun.Leaf("k1", "q/a") with { Component = new AefComponent(1, true) }));
        Assert.Throws<ArgumentException>(() => trial.AddChild(WriterRun.Leaf("k1", "q/a", trial: 1) with { Component = new AefComponent(1, true) }));
        trial.AddChild(WriterRun.Leaf("k1", "q/a", trial: 0) with { Component = new AefComponent(1, true) });
        var rollup = writer.AddResult(WriterRun.Leaf("k1") with
        {
            Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true),
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1 },
        });

        // The child is a trial line at q/a: its case and path have a rollup too (both verifiers read RES-8 so; W5a-23),
        // and that rollup is a child of the case's rollup at q: the rollups form the case's own tree (round 4).
        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("at q/a and has no rollup line", refused.Message, StringComparison.Ordinal);
        rollup.AddChild(WriterRun.Leaf("k1", "q/a") with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true), Component = new AefComponent(1, true) });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void ARollupsAgree_IsWhatItsTrialLinesGive_Res8()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", trial: 0));
        writer.AddResult(WriterRun.Leaf("k1", m: 0.1, state: AefState.Failed, trial: 1));
        var rollup = writer.AddResult(WriterRun.Leaf("k1", m: 0.5, state: AefState.Failed) with { Trials = new AefTrials(2, 1, AefTrialAggregation.AllPass, true) });

        // Two states: the trials disagreed (round 4).
        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("agree is true exactly when they are all in one", refused.Message, StringComparison.Ordinal);

        writer.UpdateResult(rollup, WriterRun.Leaf("k1", m: 0.5, state: AefState.Failed) with { Trials = new AefTrials(2, 1, AefTrialAggregation.AllPass, false) });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void ARollupAtAChildPath_IsAChildOfItsCasesRollupAtTheParentPath_Res8()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var trial = writer.AddResult(WriterRun.Leaf("k1", trial: 0) with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1 },
        });
        trial.AddChild(WriterRun.Leaf("k1", "q/a", trial: 0) with { Component = new AefComponent(1, true) });
        writer.AddResult(WriterRun.Leaf("k1") with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true) });
        writer.AddResult(WriterRun.Leaf("k1", "q/a") with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true) });   // a root

        // §3.9 trials (round 4, W5a-23; round 5: q/a's trial lines have their parents at q): refused before anything is written.
        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("its rollup at q/a is not a child of its rollup at q", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaseWithRootsAtQAndQx_RunInTrials_HasBothRollupsAsRoots_Res8()
    {
        // [RES-8] (round 5): the rollups form the case's tree as its trial lines do, and a path's spelling decides nothing:
        // the case's root at q/x has root trial lines, so its rollup there is a root, beside the rollup at q. Round 4's
        // writer refused this run (it read the tree from the paths).
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", trial: 0));
        writer.AddResult(WriterRun.Leaf("k1", "q/x", AefState.Failed, m: 0.2, trial: 0));
        writer.AddResult(WriterRun.Leaf("k1") with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true) });
        writer.AddResult(WriterRun.Leaf("k1", "q/x", AefState.Failed, m: 0.2) with { Trials = new AefTrials(1, 0, AefTrialAggregation.AllPass, true) });

        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));

        Assert.Empty(run.Problems());
        Assert.All(run.Lines("results.ndjson"), l => Assert.Null(l["parentResultId"]));
    }

    [Fact]
    public void ARollupThatIsAChild_WhereItsTrialLinesAreRoots_IsRefusedAtClose_Res8()
    {
        // [RES-8] (round 5): a rollup is a root when its trial lines are roots.
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1", trial: 0));
        writer.AddResult(WriterRun.Leaf("k1", "q/x", trial: 0));
        var rollup = writer.AddResult(WriterRun.Leaf("k1") with
        {
            Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true),
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1 },
        });
        rollup.AddChild(WriterRun.Leaf("k1", "q/x") with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true), Component = new AefComponent(1, true) });

        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("its trial lines at q/x are roots, and its rollup there is not", refused.Message, StringComparison.Ordinal);
        Assert.Equal(AefRunStatus.Running, writer.Status);
    }

    [Fact]
    public void TrialLinesAtOnePath_WhoseParentsAreRootsAndNot_AreRefusedAtClose_Res8()
    {
        // [RES-8] (round 5): trial lines at one path whose parents are not all at one path, or not all roots, are trials.
        using var run = new WriterRun();
        var writer = run.Create();
        var first = writer.AddResult(WriterRun.Leaf("k1", trial: 0) with
        {
            Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.Min, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1 },
        });
        first.AddChild(WriterRun.Leaf("k1", "q/x", trial: 0) with { Component = new AefComponent(1, true) });
        writer.AddResult(WriterRun.Leaf("k1", "q/x", trial: 1));   // a root
        writer.AddResult(WriterRun.Leaf("k1") with { Trials = new AefTrials(1, 1, AefTrialAggregation.AllPass, true) });
        writer.AddResult(WriterRun.Leaf("k1", "q/x") with { Trials = new AefTrials(2, 2, AefTrialAggregation.AllPass, true) });

        var refused = Assert.Throws<InvalidOperationException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains("its trial lines at q/x have parents at several paths, or some are roots and some not", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TimesGivenWithNineFractionDigits_AreWrittenExactly_N2d()
    {
        // [ENC-8] allows nine fraction digits; the writer's model holds AefTime, so a time is written at the precision
        // given (n2-d): run.json's startedAt and endedAt, a calibration's measuredAt, a result's times, a gate's decidedAt.
        var started = AefTime.Parse("2026-10-01T10:00:00.123456789Z");
        var measured = AefTime.Parse("2026-09-30T23:59:59.000000001Z");
        using var run = new WriterRun();
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header() with
        {
            StartedAt = started,
            Judges = [new AefJudge { Model = "m-1", Calibration = new AefCalibration { LabelSet = "labels:x", N = 10, MeasuredAt = measured } }],
        });
        writer.SetMetrics([WriterRun.Metric()]);
        writer.AddResult(WriterRun.Leaf("k1") with { StartedAt = AefTime.Parse("2026-10-01T10:00:01.5Z"), EndedAt = AefTime.Parse("2026-10-01T10:00:01.500000002Z") });

        writer.Close(AefRunStatus.Completed, AefTime.Parse("2026-10-01T10:05:00.999999999Z"));

        var header = run.Json("run.json");
        Assert.Equal("2026-10-01T10:00:00.123456789Z", (string)header["startedAt"]!);
        Assert.Equal("2026-10-01T10:05:00.999999999Z", (string)header["endedAt"]!);
        Assert.Equal("2026-09-30T23:59:59.000000001Z", (string)header["judges"]![0]!["calibration"]!["measuredAt"]!);
        var line = run.Lines("results.ndjson").Single();
        Assert.Equal("2026-10-01T10:00:01.5Z", (string)line["startedAt"]!);
        Assert.Equal("2026-10-01T10:00:01.500000002Z", (string)line["endedAt"]!);
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void AnEndOneNanosecondBeforeTheStart_IsRefused_AndADateTimeOffsetStillConverts_N2d()
    {
        using var run = new WriterRun();
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header() with { StartedAt = AefTime.Parse("2026-10-01T10:00:00.000000002Z") });
        writer.SetMetrics([WriterRun.Metric()]);

        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Completed, AefTime.Parse("2026-10-01T10:00:00.000000001Z")));
        Assert.Throws<ArgumentException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start));   // 10:00:00Z, a DateTimeOffset
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddTicks(1));                               // 10:00:00.0000001Z

        Assert.Equal("2026-10-01T10:00:00.0000001Z", (string)run.Json("run.json")["endedAt"]!);
    }

    [Fact]
    public void ATimeThatIsNotAnAefTime_IsRefused_N2d()
    {
        using var run = new WriterRun();
        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, WriterRun.Header() with { StartedAt = new AefTime(0, 1_000_000_000) }));
        Assert.Throws<ArgumentException>(() => AefRunWriter.Create(run.Dir, WriterRun.Header() with { StartedAt = new AefTime(-62_135_596_801, 0) }));   // the year 0000
        Assert.False(Directory.Exists(run.Dir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContentOff_RefusesContentInAResourceOrScope_Sec6(bool resource)
    {
        using var run = new WriterRun();
        var writer = run.Create(AefContentCapture.Off);
        var attribute = new JsonArray(new JsonObject { ["key"] = "gen_ai.system_instructions", ["value"] = new JsonObject { ["stringValue"] = "x" } });
        var traces = Traces(("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"));
        var logs = Logs(body: false);
        if (resource)
        {
            traces["resourceSpans"]![0]!["resource"] = new JsonObject { ["attributes"] = attribute };
            logs["resourceLogs"]![0]!["resource"] = new JsonObject { ["attributes"] = attribute.DeepClone() };
        }
        else
        {
            traces["resourceSpans"]![0]!["scopeSpans"]![0]!["scope"] = new JsonObject { ["attributes"] = attribute };
            logs["resourceLogs"]![0]!["scopeLogs"]![0]!["scope"] = new JsonObject { ["attributes"] = attribute.DeepClone() };
        }

        Assert.Throws<ArgumentException>(() => writer.AddTraces(traces));
        Assert.Throws<ArgumentException>(() => writer.AddLogs(logs));
    }

    [Fact]
    public void Close_WhenTheVerifierFindsAProblem_LeavesTheRunAsItWas()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1"));
        File.WriteAllBytes(run.Full("evidence.ndjson"), "{\"not\": \"an evidence line\"}\n"u8.ToArray());   // written behind the writer's back

        var refused = Assert.Throws<AefWriteException>(() => writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1)));
        Assert.Contains(refused.Problems, p => p.Path == "evidence.ndjson:1" && p.Code == "schema");
        Assert.Equal(AefRunStatus.Running, writer.Status);
        Assert.Equal("running", run.Json("run.json")["status"]!.GetValue<string>());
        Assert.False(File.Exists(run.Full("summary.json")));
    }

    // ------------------------------------------------------------------ helpers

    private static AefRunHeader WithJudge(AefCalibration calibration) =>
        WriterRun.Header() with { Judges = [new AefJudge { Model = "j", Calibration = calibration }] };

    internal static JsonObject Traces(params (string Trace, string Span)[] spans) => new()
    {
        ["resourceSpans"] = new JsonArray(new JsonObject
        {
            ["scopeSpans"] = new JsonArray(new JsonObject
            {
                ["spans"] = new JsonArray([.. spans.Select(s => (JsonNode?)new JsonObject { ["traceId"] = s.Trace, ["spanId"] = s.Span, ["name"] = "invoke_agent" })]),
            }),
        }),
    };

    internal static JsonObject Logs(bool body)
    {
        var record = new JsonObject
        {
            ["eventName"] = "gen_ai.evaluation.result",
            ["attributes"] = new JsonArray(new JsonObject { ["key"] = "gen_ai.evaluation.name", ["value"] = new JsonObject { ["stringValue"] = "m" } }),
        };
        if (body)
        {
            record["body"] = new JsonObject { ["stringValue"] = "reasoning" };
        }

        return new JsonObject { ["resourceLogs"] = new JsonArray(new JsonObject { ["scopeLogs"] = new JsonArray(new JsonObject { ["logRecords"] = new JsonArray(record) }) }) };
    }
}
