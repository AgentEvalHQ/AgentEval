using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Tests.Integrity;

namespace AgentEval.Results.Tests.Runs;

/// <summary>
/// §3.9: each rule across files, one code at a time, on a small run (<see cref="TestRun"/>) that is valid until a test
/// changes one thing; and the reading rules that decide whether they are checked at all.
/// </summary>
public class CrossFileRulesTests
{
    private static readonly string R1 = TestRun.R1, R2 = TestRun.R2, R3 = TestRun.R3, R4 = TestRun.R4;

    [Fact]
    public void TheTestRun_IsValid_AndUnsealed()
    {
        using var run = new TestRun().Write();

        var verification = run.Verify();

        Assert.Empty(verification.Problems);
        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
    }

    [Fact]
    public void ResultId_NotTheHashOfTheLine_OrOneAnEarlierLineHas()
    {
        using var run = new TestRun();
        run.Results[1]["caseId"] = "k9";             // the id is no longer the hash of the line
        run.Results.Add(run.Results[2].DeepClone().AsObject());   // line 5 repeats line 3's id

        // Line 5 repeats a child of line 1: the same child again (counted once by [RES-6]'s total), only a result-id problem.
        Assert.Equal(["results.ndjson:2 result-id", "results.ndjson:5 result-id"], Problems(run));
    }

    [Fact]
    public void ResultId_IsTheHashWithTheTrialNumber_2Point0IsTrial2()
    {
        using var run = new TestRun();
        run.Results.Add(TestRun.Obj($$"""
            {"schemaVersion": "1.0", "resultId": "{{TestRun.Id("k3", "t", 2)}}", "caseId": "k3", "path": "t", "evaluator": {"id": "e"},
             "state": "passed", "trial": 2}
            """));
        run.Results.Add(Rollup("k3", "t", n: 1, passed: 1));
        run.Write();
        run.WriteText("results.ndjson", Encoding.UTF8.GetString(run.ReadBytes("results.ndjson")).Replace("\"trial\":2", "\"trial\":2.0", StringComparison.Ordinal));

        Assert.Contains("\"trial\":2.0", Encoding.UTF8.GetString(run.ReadBytes("results.ndjson")), StringComparison.Ordinal);
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void Parent_ThatIsNoLineOfTheRun()
    {
        using var run = new TestRun();
        run.Results[1]["parentResultId"] = "r_00000000000000000000000000000000";

        // Line 1 is now a child short of its total ([RES-6]).
        Assert.Equal(["results.ndjson:1 aggregation", "results.ndjson:2 parent"], Problems(run));
    }

    [Theory]
    [InlineData("""{"measured": 3, "total": 2}""")]                                   // measured above total
    [InlineData("""{"unmeasured": {"skipped": 1}}""")]                                 // counts that do not add up
    [InlineData("""{"measured": 1, "unmeasured": null}""")]                            // no counts: they add up to 0, not 1
    [InlineData("""{"decisive": ["__R4__"]}""")]                                       // a decisive id that is not a child
    public void Aggregation_CountsThatDoNotAddUp_OrADecisiveIdThatIsNotAChild(string change)
    {
        using var run = new TestRun();
        var aggregation = run.Results[0]["aggregation"]!.AsObject();
        foreach (var (name, value) in TestRun.Obj(change.Replace("__R4__", R4, StringComparison.Ordinal)))
        {
            if (value is null)
            {
                aggregation.Remove(name);
            }
            else
            {
                aggregation[name] = value.DeepClone();
            }
        }

        Assert.Equal(["results.ndjson:1 aggregation"], Problems(run));
    }

    [Fact]
    public void Aggregation_AnAbsentPendingCountIs0_AndAPresentOneCounts()
    {
        using var run = new TestRun();
        var aggregation = run.Results[0]["aggregation"]!.AsObject();
        aggregation["total"] = 3;
        aggregation["unmeasured"]!["pending"] = 1;
        run.Results.Add(TestRun.Obj($$"""
            {"schemaVersion": "1.0", "resultId": "{{TestRun.Id("k1", "q/c")}}", "parentResultId": "{{R1}}", "caseId": "k1", "path": "q/c",
             "evaluator": {"id": "code:c"}, "state": "pending", "reason": "still running", "component": {"weight": 1, "required": false} }
            """));   // the third child, still pending
        run.Run["status"] = "running";
        run.Run.Remove("endedAt");
        run.Summary = null;

        Assert.Empty(Problems(run));
    }

    [Fact]
    public void Annotator_APanelWhoseAgreeExceedsOf()
    {
        using var run = new TestRun();
        run.Results[2]["annotator"] = TestRun.Obj("""{"kind": "LLM", "panel": {"agree": 4, "of": 3}}""");

        Assert.Equal(["results.ndjson:3 annotator"], Problems(run));
    }

    [Fact]
    public void Trials_ARollupWhosePassedExceedsN()
    {
        using var run = new TestRun();
        run.Results[3]["trials"] = TestRun.Obj("""{"n": 2, "passed": 3, "aggregation": "MajorityVote", "agree": true}""");

        Assert.Equal(["results.ndjson:4 trials"], Problems(run));
    }

    [Fact]
    public void Pending_InAClosedRun_ButNotInARunningOne()
    {
        using var closed = new TestRun();
        closed.Results.Add(Pending());

        Assert.Equal(["results.ndjson:5 pending"], Problems(closed));

        using var running = new TestRun();
        running.Results.Add(Pending());
        running.Run["status"] = "running";
        running.Run.Remove("endedAt");
        running.Summary = null;

        Assert.Empty(Problems(running));

        static JsonObject Pending() => TestRun.Obj($$"""
            {"schemaVersion": "1.0", "resultId": "{{TestRun.Id("k5", "z")}}", "caseId": "k5", "path": "z", "evaluator": {"id": "e"},
             "state": "pending", "reason": "still running"}
            """);
    }

    [Fact]
    public void Evidence_AnIdNoRecordHas_AndAnEvidenceIdTwice()
    {
        using var run = new TestRun();
        run.Results[3]["evidence"] = new JsonArray("E-9");
        run.Evidence.Add(run.Evidence[0].DeepClone().AsObject());

        Assert.Equal(["evidence.ndjson:2 evidence-id", "results.ndjson:4 evidence"], Problems(run));
    }

    [Fact]
    public void EvidenceDigest_ABlobLinkWhoseDigestIsNotTheBlobsName()
    {
        using var run = new TestRun();
        run.Evidence[0]["digest"] = "sha256:" + new string('0', 64);

        Assert.Equal(["evidence.ndjson:1 evidence-digest"], Problems(run));
    }

    [Fact]
    public void Blob_ReferencedByAnEvidenceRecordAndAResult_ButNotInTheRun()
    {
        using var run = new TestRun();
        run.Files.Clear();

        Assert.Equal(["evidence.ndjson:1 blob", "results.ndjson:3 blob"], Problems(run));
    }

    [Fact]
    public void BlobDigest_ABlobWhoseBytesDoNotHashToItsName()
    {
        using var run = new TestRun();
        run.Files[TestRun.ReasoningPath] = [.. TestRun.Reasoning, (byte)'!'];

        // reasoning.bytes no longer matches either.
        Assert.Equal([$"{TestRun.ReasoningPath} blob-digest", "results.ndjson:3 reasoning-size"], Problems(run));
    }

    [Fact]
    public void ReasoningSize_NotTheBlobsSize()
    {
        using var run = new TestRun();
        run.Results[2]["reasoning"]!["bytes"] = TestRun.Reasoning.Length + 1;

        Assert.Equal(["results.ndjson:3 reasoning-size"], Problems(run));
    }

    [Fact]
    public void Metric_UndeclaredInALine_ScoredTwice_UndeclaredInTheSummary_DeclaredTwice_OrAnInvertedScale()
    {
        using var run = new TestRun();
        run.Results[1]["scores"]!.AsArray().Add(new JsonObject { ["metric"] = "nope", ["value"] = 1 });
        run.Results[3]["scores"]!.AsArray().Add(new JsonObject { ["metric"] = "m", ["value"] = 0.95 });
        run.Summary!["lanes"]![0]!["metrics"]!.AsArray().Add(TestRun.Obj("""
            {"metric": "nope", "path": "q", "N": 2, "n": 0, "notMeasured": 2, "value": null, "verdict": "not_measured"}
            """));
        run.Summary!["lanes"]![0]!["metrics"]![0]!["n"] = 1;            // line 4 scores m twice: not measured for it
        run.Summary!["lanes"]![0]!["metrics"]![0]!["notMeasured"] = 1;
        run.Summary!["lanes"]![0]!["metrics"]![0]!["value"] = 0.4;
        run.Summary!["lanes"]![0]!["metrics"]![0]!["sum"] = 0.4;

        Assert.Equal(["results.ndjson:2 metric", "results.ndjson:4 metric", "summary.json metric"], Problems(run));

        using var declared = new TestRun();
        declared.Metrics["metrics"]!.AsArray().Add(declared.Metrics["metrics"]![0]!.DeepClone());
        Assert.Equal(["metrics.json metric"], Problems(declared));

        using var inverted = new TestRun();
        inverted.Metrics["metrics"]![1]!["scale"] = TestRun.Obj("""{"min": 1, "max": 0}""");
        Assert.Equal(["metrics.json metric"], Problems(inverted));
    }

    [Fact]
    public void SummaryRunId_NotRunJsonsRunId()
    {
        using var run = new TestRun();
        run.Summary!["runId"] = "another-run";

        Assert.Equal(["summary.json summary-run-id"], Problems(run));
    }

    [Theory]
    [InlineData("N", 3)]
    [InlineData("n", 1)]
    [InlineData("notMeasured", 1)]
    [InlineData("sum", 1.31)]
    [InlineData("value", 0.66)]
    public void Summary_AFigureThatIsNotWhatTheResultsGive(string figure, double written)
    {
        using var run = new TestRun();
        run.Summary!["lanes"]![0]!["metrics"]![0]![figure] = written;

        Assert.Equal(["summary.json summary"], Problems(run));
    }

    [Fact]
    public void Summary_WithinTheTolerance_Matches()
    {
        using var run = new TestRun();
        run.Summary!["lanes"]![0]!["metrics"]![0]!["value"] = 0.65 + 5e-10;
        run.Summary!["lanes"]![0]!["metrics"]![0]!["sum"] = 1.3 - 5e-10;

        Assert.Empty(Problems(run));
    }

    [Theory]
    [InlineData("median", 0.65, true)]     // two values: the mean of the two middle ones
    [InlineData("median", 0.4, false)]
    [InlineData("min", 0.4, true)]
    [InlineData("max", 0.4, false)]
    [InlineData("pass@k", 0.123, true)]    // the producer's: never recomputed
    public void Summary_AnAggregatesValue_IsRecomputedOnlyForTheMethodsAefDefines(string method, double value, bool valid)
    {
        using var run = new TestRun();
        var entry = run.Summary!["lanes"]![0]!["metrics"]![0]!;
        entry["aggregate"] = new JsonObject { ["method"] = method };
        entry["value"] = value;

        Assert.Equal(valid ? Array.Empty<string>() : new[] { "summary.json summary" }, Problems(run));
    }

    [Fact]
    public void SummaryDuplicate_TwoEntriesWithOneLaneMetricAndPath()
    {
        using var run = new TestRun();
        run.Summary!["lanes"]!.AsArray().Add(run.Summary!["lanes"]![0]!.DeepClone());   // the lane "main" again

        Assert.Equal(["summary.json summary-duplicate"], Problems(run));
    }

    [Fact]
    public void SummaryDuplicate_ALaneNameTwice_ButLinesWithoutLaneStillBelongToTheOneName()
    {
        using var run = new TestRun();
        run.Summary!["lanes"]!.AsArray().Add(TestRun.Obj("""{"lane": "main", "metrics": []}"""));

        // [SUM-9] (W3-2, ruled 10-08): a lane name twice is the problem; [SUM-3] still finds a single lane name, so the
        // entry recomputes as before and is not also a summary problem.
        Assert.Equal(["summary.json summary-duplicate"], Problems(run));
    }

    [Theory]
    [InlineData("""[{"role": "agent"}, {"role": "agent"}]""", true)]
    [InlineData("""[{"role": "judge", "model": "x"}, {"role": "judge", "model": "x"}]""", true)]
    [InlineData("""[{"role": "judge", "model": "x"}, {"role": "judge"}]""", false)]   // an absent model is a value of its own
    [InlineData("""[{"role": "judge", "model": "x"}, {"role": "agent", "model": "x"}]""", false)]
    public void SummaryDuplicate_TwoUsageEntriesWithOneRoleAndModel(string usage, bool duplicate)
    {
        using var run = new TestRun();
        run.Summary!["usage"] = JsonNode.Parse(usage);

        Assert.Equal(duplicate ? new[] { "summary.json summary-duplicate" } : Array.Empty<string>(), Problems(run));
    }

    [Fact]
    public void Gate_AResultNoLineHas_OrShipOnAComparabilityThatReadsAsIncomparable()
    {
        using var run = new TestRun();
        run.Gates[0]["decisive"] = new JsonArray("r_00000000000000000000000000000000");
        var ship = run.Gates[0].DeepClone().AsObject();
        ship["decisive"] = new JsonArray(R1);
        ship["outcome"] = "ship";
        ship["comparability"] = "partially-comparable";   // unknown: reads as incomparable (§7.3)
        run.Gates.Add(ship);

        Assert.Equal(["gates.ndjson:1 gate", "gates.ndjson:2 gate"], Problems(run));
    }

    [Fact]
    public void TraceLink_ASpanOrATraceTheTracesFileDoesNotHave_IdsCompareWithoutCase()
    {
        using var run = new TestRun();
        const string trace = "4bf92f3577b34da6a3ce929d0e0e4736";
        run.Files["traces.otlp.jsonl"] = Encoding.UTF8.GetBytes(
            $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"{{trace.ToUpperInvariant()}}","spanId":"00F067AA0BA902B7","name":"s"}]}]}]}""" + "\n");
        run.Results[0]["traceLink"] = new JsonObject { ["traceId"] = trace, ["spanId"] = "00f067aa0ba902b7" };   // resolves
        run.Results[1]["traceLink"] = new JsonObject { ["traceId"] = trace };                                     // a trace: resolves
        run.Results[2]["traceLink"] = new JsonObject { ["traceId"] = trace, ["spanId"] = "1111111111111111" };   // no such span
        run.Results[3]["traceLink"] = new JsonObject { ["traceId"] = new string('a', 32) };                         // no such trace
        run.Evidence.Add(TestRun.Obj($$"""
            {"schemaVersion": "1.0", "evidenceId": "E-2", "kind": "span", "link": {"traceId": "{{trace}}", "spanId": "2222222222222222"} }
            """));

        Assert.Equal(["evidence.ndjson:2 trace-link", "results.ndjson:3 trace-link", "results.ndjson:4 trace-link"], Problems(run));
    }

    [Fact]
    public void TraceLink_WithoutATracesFile_NothingIsVerified()
    {
        using var run = new TestRun();
        run.Results[2]["traceLink"] = new JsonObject { ["traceId"] = new string('a', 32), ["spanId"] = "1111111111111111" };

        Assert.Empty(Problems(run));
    }

    [Fact]
    public void ContentCapture_Off_NoReasoning_NoPromptHash_NoContentEvidence()
    {
        using var run = new TestRun();
        run.Run["contentCapture"] = "off";
        run.Results[0]["annotator"] = TestRun.Obj("""{"kind": "LLM", "promptHash": "sha256:0000000000000000000000000000000000000000000000000000000000000000"}""");
        run.Evidence.Add(TestRun.Obj("""{"schemaVersion": "1.0", "evidenceId": "E-2", "kind": "input", "link": {"uri": "https://example.com/case/k1"}}"""));
        run.Evidence.Add(TestRun.Obj("""{"schemaVersion": "1.0", "evidenceId": "E-3", "kind": "compliance_artifact", "link": {"uri": "https://example.com/a"}}"""));
        run.Evidence.Add(TestRun.Obj("""{"schemaVersion": "1.0", "evidenceId": "E-4", "kind": "a-later-kind", "link": {"uri": "https://example.com/b"}}"""));

        Assert.Equal(
            ["evidence.ndjson:1 content-capture", "evidence.ndjson:2 content-capture", "results.ndjson:1 content-capture", "results.ndjson:3 content-capture"],
            Problems(run));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("a-later-value")]   // reads as "on" (§7.3)
    public void ContentCapture_OnOrUnknown_KeepsContent(string value)
    {
        using var run = new TestRun();
        run.Run["contentCapture"] = value;

        Assert.Empty(Problems(run));
    }

    [Fact]
    public void ContentCapture_Off_InTracesAndLogs_Sec6()
    {
        using var run = new TestRun();
        run.Run["contentCapture"] = "off";
        run.Results[2].Remove("reasoning");
        run.Evidence.Clear();
        run.Results[0].Remove("evidence");
        run.Files.Clear();
        run.Files["traces.otlp.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(
            Spans("""{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","attributes":[{"key":"gen_ai.request.model","value":{"stringValue":"m"}}]}"""),
            Spans("""{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b8","events":[{"name":"e","attributes":[{"key":"gen_ai.tool.call.arguments","value":{"stringValue":"{}"}}]}]}"""),
            Spans("""{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b9","attributes":[{"key":"gen_ai.prompt","value":{"stringValue":"hi"}}]}""")));
        run.Files["logs.otlp.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(
            Logs("""{"eventName":"gen_ai.evaluation.result","attributes":[{"key":"gen_ai.evaluation.score.label","value":{"stringValue":"passed"}}]}"""),
            Logs("""{"eventName":"gen_ai.evaluation.result","attributes":[{"key":"gen_ai.evaluation.explanation","value":{"stringValue":"why"}}]}"""),
            Logs("""{"body":null}""")));

        Assert.Equal(
            ["logs.otlp.jsonl:2 content-capture", "logs.otlp.jsonl:3 content-capture", "traces.otlp.jsonl:2 content-capture", "traces.otlp.jsonl:3 content-capture"],
            Problems(run));

        static string Spans(string span) => $$"""{"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{{span}}]}]}]}""" + "\n";
        static string Logs(string record) => $$"""{"resourceLogs":[{"scopeLogs":[{"logRecords":[{{record}}]}]}]}""" + "\n";
    }

    [Fact]
    public void RunTimes_EndedBeforeStarted()
    {
        using var run = new TestRun();
        run.Run["endedAt"] = "2026-10-01T09:59:59.999999999Z";

        Assert.Equal(["run.json run-times"], Problems(run));
    }

    [Fact]
    public void RunTimes_TimesCompareAtFullPrecision()
    {
        using var run = new TestRun();
        run.Run["startedAt"] = "2026-10-01T10:00:00.000000002Z";
        run.Run["endedAt"] = "2026-10-01T10:00:00.000000001Z";   // DateTimeOffset would round both to the same tick

        Assert.Equal(["run.json run-times"], Problems(run));
    }

    [Fact]
    public void UnexpectedFile_AFileRun2DoesNotListThatTheSealLists()
    {
        using var run = new TestRun();
        run.Files["notes.txt"] = "x"u8.ToArray();
        run.Files["ext/notes.txt"] = "x"u8.ToArray();        // ext/ is the producer's
        run.Files["blobs/sha256/00/x"] = "x"u8.ToArray();    // not a blob's path
        run.Write().Seal();

        Assert.Equal(["blobs/sha256/00/x unexpected-file", "notes.txt unexpected-file"], run.Problems());
        Assert.Equal(AefOutcome.Invalid, run.Verify().Outcome);
    }

    [Fact]
    public void Attack_ThatSucceededOnAPassedLine()
    {
        using var run = new TestRun();
        run.Results[3]["attack"] = TestRun.Obj("""{"technique": "prompt-injection", "success": true}""");
        run.Results[1]["attack"] = TestRun.Obj("""{"technique": "prompt-injection", "success": false}""");

        Assert.Equal(["results.ndjson:4 attack"], Problems(run));
    }

    [Theory]
    [InlineData(5, 4, "2026-09-30T00:00:00Z")]   // dangerousErrors above n
    [InlineData(0, 4, "2026-10-01T10:00:00.5Z")]  // measured after the run started
    public void Calibration_ImpossibleOrMeasuredAfterTheRun(int dangerous, int n, string measuredAt)
    {
        using var run = new TestRun();
        run.Run["judges"] = new JsonArray(TestRun.Obj($$"""
            {"model": "judge-1", "calibration": {"labelSet": "set:a", "n": {{n}}, "dangerousErrors": {{dangerous}}, "measuredAt": "{{measuredAt}}"} }
            """));

        Assert.Equal(["run.json calibration"], Problems(run));
    }

    [Fact]
    public void ExecutionPolicy_RequirePassesAboveTrialsPerCase()
    {
        using var run = new TestRun();
        run.Run["suite"] = TestRun.Obj("""{"ref": "suite:a", "version": "1", "executionPolicy": {"trialsPerCase": 3, "requirePasses": 4}}""");

        Assert.Equal(["run.json execution-policy"], Problems(run));
    }

    [Fact]
    public void Interval_LowAboveHigh_OnALineAndInTheSummary()
    {
        using var run = new TestRun();
        run.Results[0]["uncertainty"] = TestRun.Obj("""{"ci": {"low": 0.5, "high": 0.3, "level": 0.95}}""");
        run.Summary!["lanes"]![0]!["metrics"]![0]!["ci"] = TestRun.Obj("""{"low": 0.7, "high": 0.6, "level": 0.95}""");

        Assert.Equal(["results.ndjson:1 interval", "summary.json interval"], Problems(run));
    }

    [Fact]
    public void ResultTimes_EndedBeforeStarted_OrARoleTwiceInUsage()
    {
        using var run = new TestRun();
        run.Results[0]["startedAt"] = "2026-10-01T10:01:00Z";
        run.Results[0]["endedAt"] = "2026-10-01T10:00:59Z";
        run.Results[1]["usage"] = JsonNode.Parse("""[{"role": "judge"}, {"role": "judge"}]""");
        run.Results[2]["usage"] = JsonNode.Parse("""[{"role": "judge"}, {"role": "agent"}]""");

        Assert.Equal(["results.ndjson:1 result-times", "results.ndjson:2 result-times"], Problems(run));
    }

    [Fact]
    public void ProblemsAreOrderedByPath_LinePathsByLineNumber_ThenByCode()
    {
        using var run = new TestRun();
        for (var i = 0; i < 8; i++)
        {
            run.Results.Add(TestRun.Obj($$"""
                {"schemaVersion": "1.0", "resultId": "{{TestRun.Id($"p{i}", "z")}}", "caseId": "p{{i}}", "path": "z", "evaluator": {"id": "e"},
                 "state": "pending", "reason": "r", "parentResultId": "r_00000000000000000000000000000000"}
                """));
        }

        // Each line is also a child without component ([RES-5]).
        Assert.Equal(
            Enumerable.Range(5, 8).SelectMany(n => new[] { $"results.ndjson:{n} component", $"results.ndjson:{n} parent", $"results.ndjson:{n} pending" }),
            Problems(run));
    }

    [Fact]
    public void ARunWithAReadingProblem_IsNotCheckedAcrossFiles()
    {
        using var run = new TestRun();
        run.Results[1]["parentResultId"] = "r_00000000000000000000000000000000";   // would be a parent problem
        run.Results[3]["state"] = "a-later-state";                                 // state is a closed enum ([VER-9])

        Assert.Equal(["results.ndjson:4 schema"], Problems(run));
    }

    [Fact]
    public void Reading_AnEncodingProblemPerLine_FramingPerFile_AndARequiredFileThatIsAbsent()
    {
        using var run = new TestRun();
        run.Write();
        var results = Encoding.UTF8.GetString(run.ReadBytes("results.ndjson")).Split('\n');
        results[1] = """{"a": 1, "a": 2}""";
        run.WriteText("results.ndjson", string.Join('\n', results));
        run.WriteText("gates.ndjson", "{}\r\n");
        run.Delete("summary.json");   // a closed run has one

        Assert.Equal(["gates.ndjson encoding", "results.ndjson:2 encoding", "summary.json schema"], run.Problems());
    }

    [Fact]
    public void Reading_ALineBeyondTheDepthLimit_IsALimitProblemAtTheLine_AndTheOtherLinesAreStillRead()
    {
        using var run = new TestRun();
        run.Write();
        var results = Encoding.UTF8.GetString(run.ReadBytes("results.ndjson")).Split('\n');
        results[1] = results[1][..^1] + ",\"ext\":" + Integrity.OverlayChainTests.Deep(64) + "}";   // 65 deep: a writer would refuse it
        results[3] = """{"a": 1, "a": 2}""";
        run.WriteText("results.ndjson", string.Join('\n', results));

        // [ENC-18]: limit at the line, and line 4 is read (an encoding problem) after it.
        Assert.Equal(["results.ndjson:2 limit", "results.ndjson:4 encoding"], run.Problems());
    }

    [Fact]
    public void Reading_ALineNested64Deep_IsWithinTheLimit()
    {
        using var run = new TestRun();
        run.Results[1]["ext"] = JsonNode.Parse(Integrity.OverlayChainTests.Deep(63));

        Assert.Empty(Problems(run));
    }

    [Fact]
    public void Reading_AnNdjsonFileAbove1GiB_IsALimitProblemAtTheFile_AndIsNotRead()
    {
        using var run = new TestRun().Write();
        using (var file = new FileStream(Path.Combine(run.Dir, "results.ndjson"), FileMode.Open, FileAccess.Write))
        {
            file.SetLength(AefLimits.MaxNdjsonBytes + 1);   // sparse where the file system allows it: never read
        }

        var documents = AefRunDocuments.Read(AefRunFolder.Open(run.Dir));

        Assert.Equal([new AefProblem("results.ndjson", "limit")], documents.Problems);
        Assert.False(documents.Results.IsRead);
    }

    [Fact]
    public void Reading_ATimeThatDoesNotExist_IsASchemaProblem()
    {
        using var run = new TestRun();
        run.Run["startedAt"] = "2026-02-29T10:00:00Z";   // 2026 is not a leap year

        Assert.Equal(["run.json schema"], Problems(run));
    }

    [Fact]
    public void Reading_ADocumentAboveTheLimit_IsALimitProblem_AndIsNotRead()
    {
        using var run = new TestRun();
        run.Write();
        var large = run.Run.DeepClone();
        large["ext"] = new JsonObject { ["padding"] = new string('x', 4 * 1024 * 1024) };   // a writer would refuse it
        run.WriteText("run.json", large.ToJsonString());

        Assert.Equal(["run.json limit"], run.Problems());
    }

    [Fact]
    public void Paths_AHiddenFileOrABadName_IsAPathProblem_AndTheRestIsStillChecked()
    {
        using var run = new TestRun();
        run.Files[".DS_Store"] = [];
        run.Results[3]["evidence"] = new JsonArray("E-9");

        Assert.Equal([".DS_Store path", "results.ndjson:4 evidence"], Problems(run));
    }

    [Fact]
    public void Aggregation_ATotalThatIsNotTheNumberOfChildren()
    {
        using var run = new TestRun();
        var aggregation = run.Results[0]["aggregation"]!.AsObject();
        aggregation["total"] = 3;                       // two children
        aggregation["unmeasured"]!["skipped"] = 1;      // so the counts alone still add up

        Assert.Equal(["results.ndjson:1 aggregation"], Problems(run));
    }

    [Fact]
    public void Aggregation_ANodeWithChildrenAndNoAggregation_AndComponent_AChildWithoutOne()
    {
        using var run = new TestRun();
        run.Results[0].Remove("aggregation");
        run.Results[2].Remove("component");

        // [RES-5] (round 3 addendum).
        Assert.Equal(["results.ndjson:1 aggregation", "results.ndjson:3 component"], Problems(run));
    }

    [Fact]
    public void Trials_ARollupThatMatchesItsTrialLines_IsValid()
    {
        using var run = new TestRun();
        run.Results.Add(Trial("k3", "t", 0, "failed"));
        run.Results.Add(Trial("k3", "t", 1, "passed"));
        run.Results.Add(Rollup("k3", "t", n: 2, passed: 1));

        Assert.Empty(Problems(run));
    }

    [Theory]
    [InlineData(5, 1)]   // n is not the number of trial lines
    [InlineData(2, 2)]   // passed is not the number of them passed
    public void Trials_ARollupThatContradictsItsTrialLines(int n, int passed)
    {
        using var run = new TestRun();
        run.Results.Add(Trial("k3", "t", 0, "failed"));
        run.Results.Add(Trial("k3", "t", 1, "passed"));
        run.Results.Add(Rollup("k3", "t", n, passed));

        Assert.Equal(["results.ndjson:7 trials"], Problems(run));
    }

    [Theory]
    [InlineData("failed", "passed", true, false)]    // the trials disagreed: agree is false
    [InlineData("failed", "passed", false, true)]
    [InlineData("failed", "warn", true, false)]      // two states, none passed: still a disagreement
    [InlineData("failed", "warn", false, true)]
    [InlineData("failed", "failed", true, true)]
    [InlineData("failed", "failed", false, false)]
    public void Trials_ARollupsAgree_IsTrueExactlyWhenItsTrialsAreAllInOneState(string first, string second, bool agree, bool holds)
    {
        // [RES-8], §3.9 trials (round 4).
        using var run = new TestRun();
        run.Results.Add(Trial("k3", "t", 0, first));
        run.Results.Add(Trial("k3", "t", 1, second));
        var rollup = Rollup("k3", "t", n: 2, passed: new[] { first, second }.Count(s => s == "passed"));
        rollup["trials"]!["agree"] = agree;
        run.Results.Add(rollup);
        string[] expected = holds ? [] : ["results.ndjson:7 trials"];

        Assert.Equal(expected, Problems(run));
    }

    [Fact]
    public void Trials_ASecondRollupForOneCaseAndPath_IsReportedAtTheSecond()
    {
        using var run = new TestRun();
        run.Results.Add(Trial("k3", "t", 0, "passed"));
        run.Results.Add(Rollup("k3", "t", n: 1, passed: 1));
        var second = Rollup("k3", "t", n: 1, passed: 1);
        second["evaluator"] = new JsonObject { ["id"] = "e2" };
        run.Results.Add(second);

        // The second line has the first's id too (the id is the hash of case and path).
        Assert.Equal(["results.ndjson:7 result-id", "results.ndjson:7 trials"], Problems(run));
    }

    [Fact]
    public void Trials_TrialLinesWithoutARollup_InAClosedRunButNotARunningOne()
    {
        using var closed = new TestRun();
        closed.Results.Add(Trial("k3", "t", 0, "failed"));
        closed.Results.Add(Trial("k3", "t", 1, "passed"));

        Assert.Equal(["results.ndjson:5 trials", "results.ndjson:6 trials"], Problems(closed));

        using var running = new TestRun();
        running.Results.Add(Trial("k3", "t", 0, "failed"));
        running.Run["status"] = "running";
        running.Run.Remove("endedAt");
        running.Summary = null;

        Assert.Empty(Problems(running));
    }

    [Fact]
    public void Trials_ALineUnderATrialsLine_CarriesTheSameTrial()
    {
        using var run = new TestRun();
        var parent = Trial("k3", "t", 0, "passed");
        parent["aggregation"] = TestRun.Obj("""{"strategy": "Min", "rulePath": "threshold", "measured": 2, "total": 2}""");
        run.Results.Add(parent);
        run.Results.Add(Child(Trial("k3", "t/a", 0, "passed"), parent));       // the same trial
        run.Results.Add(Child(Line("k3", "t/b", "passed"), parent));           // none: counted as the case's by SUM-3
        var rollup = Rollup("k3", "t", n: 1, passed: 1);
        rollup["aggregation"] = TestRun.Obj("""{"strategy": "Min", "rulePath": "threshold", "measured": 1, "total": 1}""");
        run.Results.Add(rollup);
        run.Results.Add(Child(Rollup("k3", "t/a", n: 1, passed: 1), rollup));

        Assert.Equal(["results.ndjson:7 trials"], Problems(run));
    }

    [Fact]
    public void Trials_TheRollupsOfACompositeCase_FormItsOwnTree()
    {
        // [RES-8] (round 4, W5a-23): a rollup at a child path has its case's rollup at the parent path (the path without
        // its last '/' segment) as its parent, when the case has one there.
        JsonObject[] Case(string caseId, bool asTree)
        {
            var (first, second) = (Trial(caseId, "t", 0, "passed"), Trial(caseId, "t", 1, "passed"));
            foreach (var trial in new[] { first, second })
            {
                trial["aggregation"] = TestRun.Obj("""{"strategy": "Min", "rulePath": "threshold", "measured": 1, "total": 1}""");
            }

            var rollup = Rollup(caseId, "t", n: 2, passed: 2);
            var child = Rollup(caseId, "t/x", n: 2, passed: 2);
            if (asTree)
            {
                rollup["aggregation"] = TestRun.Obj("""{"strategy": "Min", "rulePath": "threshold", "measured": 1, "total": 1}""");
                child = Child(child, rollup);
            }

            return [first, Child(Trial(caseId, "t/x", 0, "passed"), first), second, Child(Trial(caseId, "t/x", 1, "passed"), second), rollup, child];
        }

        using var tree = new TestRun();
        tree.Results.AddRange(Case("k3", asTree: true));
        Assert.Empty(Problems(tree));

        using var root = new TestRun();
        root.Results.AddRange(Case("k3", asTree: false));
        Assert.Equal(["results.ndjson:10 trials"], Problems(root));

        // A case with no rollup at the parent path: its rollup at the child path may be a root.
        using var alone = new TestRun();
        alone.Results.Add(Trial("k4", "u/x", 0, "passed"));
        alone.Results.Add(Rollup("k4", "u/x", n: 1, passed: 1));
        Assert.Empty(Problems(alone));

        // Another case's rollup at the parent path is not this case's.
        using var other = new TestRun();
        other.Results.AddRange(Case("k3", asTree: true));
        other.Results.Add(Trial("k4", "t/x", 0, "passed"));
        other.Results.Add(Rollup("k4", "t/x", n: 1, passed: 1));
        Assert.Empty(Problems(other));
    }

    [Fact]
    public void ResultTimes_UsageNamesARoleAndModelOnce_TwoJudgeModelsAreTwoParties()
    {
        using var run = new TestRun();
        run.Results[1]["usage"] = JsonNode.Parse("""[{"role": "judge", "model": "a"}, {"role": "judge", "model": "b"}, {"role": "judge"}]""");
        run.Results[2]["usage"] = JsonNode.Parse("""[{"role": "judge", "model": "a"}, {"role": "judge", "model": "a"}]""");

        Assert.Equal(["results.ndjson:3 result-times"], Problems(run));
    }

    [Theory]
    [InlineData(0.97, true)]              // 0.4² + 0.9²
    [InlineData(0.4 * 0.4 + 0.9 * 0.9, true)]   // summed in order in binary64: within §3.6
    [InlineData(0.97 + 1e-12, true)]
    [InlineData(0.98, false)]
    public void Summary_ASumOfSquaresWritten_IsComparedWithinTheTolerance(double sumSq, bool matches)
    {
        // [SUM-5] (round 4): only sum is exact; sumSq is binary64, compared within §3.6 like sum.
        using var run = new TestRun();
        run.Summary!["lanes"]![0]!["metrics"]![0]!["sumSq"] = sumSq;
        string[] expected = matches ? [] : ["summary.json summary"];

        Assert.Equal(expected, Problems(run));
    }

    [Fact]
    public void Summary_AProducersAggregate_HasANumberWhenNIsNot0()
    {
        using var run = new TestRun();
        var entry = run.Summary!["lanes"]![0]!["metrics"]![0]!;
        entry["aggregate"] = new JsonObject { ["method"] = "pass@k" };
        entry["value"] = null;

        // [SUM-8]: never recomputed, but null only when n is 0.
        Assert.Equal(["summary.json summary"], Problems(run));
    }

    [Fact]
    public void ContentCapture_Off_InAResourceOrAScopesAttributes_Sec6()
    {
        using var run = new TestRun();
        run.Run["contentCapture"] = "off";
        run.Results[2].Remove("reasoning");
        run.Evidence.Clear();
        run.Results[0].Remove("evidence");
        run.Files.Clear();
        const string Content = """[{"key":"gen_ai.system_instructions","value":{"stringValue":"be nice"}}]""";
        run.Files["traces.otlp.jsonl"] = Encoding.UTF8.GetBytes(
            $$"""{"resourceSpans":[{"resource":{"attributes":{{Content}} },"scopeSpans":[{"scope":{"name":"p"},"spans":[]}]}]}""" + "\n" +
            $$"""{"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"scope":{"name":"p","attributes":{{Content}} },"spans":[]}]}]}""" + "\n" +
            """{"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"scope":{"name":"p","attributes":[]},"spans":[]}]}]}""" + "\n");
        run.Files["logs.otlp.jsonl"] = Encoding.UTF8.GetBytes(
            $$"""{"resourceLogs":[{"resource":{"attributes":{{Content}} },"scopeLogs":[]}]}""" + "\n");

        Assert.Equal(["logs.otlp.jsonl:1 content-capture", "traces.otlp.jsonl:1 content-capture", "traces.otlp.jsonl:2 content-capture"], Problems(run));
    }

    [Fact]
    public void Reading_RunJsonNestedDeeperThan64_IsALimitProblem_EvenWhenItIsAlsoNotIJson()
    {
        using var run = new TestRun();
        run.Write();
        var text = Encoding.UTF8.GetString(run.ReadBytes("run.json"));
        run.WriteText("run.json", text.Replace("\"runId\"", "\"deep\": " + Integrity.OverlayChainTests.Deep(64) + ", \"runId\": 1, \"runId\"", StringComparison.Ordinal));

        // [ENC-17]: limits are checked on the bytes first; the member named twice is never reached.
        Assert.Equal(["run.json limit"], run.Problems());
    }

    [Fact]
    public void TheFileLimit_DoesNotCountTheSealItsSignatureOrOverlays()
    {
        Assert.False(AefFolder.CountsTowardTheLimit("seal.json"));
        Assert.False(AefFolder.CountsTowardTheLimit("attestation.dsse.json"));
        Assert.False(AefFolder.CountsTowardTheLimit("overlays/seal-0001.json"));
        Assert.True(AefFolder.CountsTowardTheLimit("results.ndjson"));
        Assert.True(AefFolder.CountsTowardTheLimit("ext/seal.json"));
    }

    private static IReadOnlyList<string> Problems(TestRun run) => run.Write().Problems();

    // A result line of the test run's (no lane: it belongs to the summary's only lane).
    private static JsonObject Line(string caseId, string path, string state, long? trial = null)
    {
        var line = TestRun.Obj($$"""
            {"schemaVersion": "1.0", "resultId": "{{TestRun.Id(caseId, path, trial)}}", "caseId": "{{caseId}}", "path": "{{path}}",
             "evaluator": {"id": "code:t"}, "state": "{{state}}"}
            """);
        if (trial is { } t)
        {
            line["trial"] = t;
        }

        return line;
    }

    private static JsonObject Trial(string caseId, string path, long trial, string state) => Line(caseId, path, state, trial);

    // [RES-8]: the rollup line of a case and path.
    private static JsonObject Rollup(string caseId, string path, int n, int passed)
    {
        var line = Line(caseId, path, passed > 0 ? "passed" : "failed");
        line["trials"] = new JsonObject { ["n"] = n, ["passed"] = passed, ["aggregation"] = "AnyPass", ["agree"] = passed == n || passed == 0 };
        return line;
    }

    private static JsonObject Child(JsonObject line, JsonObject parent)
    {
        line["parentResultId"] = (string)parent["resultId"]!;
        line["component"] = new JsonObject { ["weight"] = 1, ["required"] = true };
        return line;
    }
}
