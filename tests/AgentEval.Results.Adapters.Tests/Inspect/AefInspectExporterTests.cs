// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Inspect;
using AgentEval.Results.Adapters.Tests.Otel;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Inspect;

/// <summary>
/// AEF → Inspect (contracts/aef/1/interop/inspect.md), written from the page's text: the table, the rules "Beyond the
/// table" (IN-1 to IN-5, IN-11) and those settled 10-10 (R7I, IN-12, IN-13), the refusals and the worked example. The
/// checked examples (interop/examples/aef-inspect, aef-inspect-trials, aef-inspect-edges) are reproduced from their
/// input runs and compared as JSON values, as the page fixes values, not bytes (R7I-1); the first two also byte for
/// byte, as Python's <c>json.dumps(log, indent=2, ensure_ascii=False)</c> writes them.
/// </summary>
public sealed class AefInspectExporterTests : IDisposable
{
    private static readonly string Interop = Path.Combine(AefTestRuns.RepoRoot, "contracts", "aef", "1", "interop");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = AefTestRuns.TempPath("aef-inspect");

    public void Dispose() => AefTestRuns.Delete(_root);

    // ------------------------------------------------------------------ the checked examples

    public static TheoryData<string, int> Steps()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "aef-inspect", "aef-inspect-trials", "aef-inspect-edges" })
        {
            var steps = JsonNode.Parse(File.ReadAllText(Path.Combine(Interop, "examples", name, "expected.json")))!["steps"]!.AsArray();
            for (var i = 0; i < steps.Count; i++)
            {
                if ((string?)steps[i]!["args"]![0] == "to-inspect")
                {
                    data.Add(name, i);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void TheCheckedExample_IsReproduced(string name, int step)
    {
        // The page fixes values, not bytes (R7I-1): the logs are compared as JSON values, NaN as NaN.
        var example = Path.Combine(Interop, "examples", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!;
        var args = expected["steps"]![step]!["args"]!.AsArray().Select(a => (string)a!).ToList();
        var want = InspectJson.Parse(File.ReadAllBytes(Path.Combine(example, (string)expected["steps"]![step]!["expected"]!)));

        var export = AefInspectExporter.Export(Path.Combine(example, args[1]), new AefInspectExportOptions { IgnoreOverlays = args.Contains("--ignore-overlays") });

        Assert.Equal((string)expected["runs"]![args[1]]!, AefRunVerification.Name(export.Outcome));
        var have = InspectJson.Parse(Encoding.UTF8.GetBytes(export.Text));
        Assert.True(AefOtelExporterTests.SameJson(want, have), $"{name}:\nexpected {InspectJson.Indented(want)}\ngot      {export.Text}");
        Assert.Equal(want!["samples"]!.AsArray().Count, export.Samples);
    }

    [Theory]
    [InlineData("aef-inspect")]
    [InlineData("aef-inspect-trials")]
    public void TheLog_IsWhatPythonsJsonDumpsWrites_ByteForByteForTheFirstExamples(string name)
    {
        // Not required (R7I-1), but kept: the writer writes as Inspect does, json.dumps(indent=2, ensure_ascii=False).
        var example = Path.Combine(Interop, "examples", name);
        var want = File.ReadAllText(Path.Combine(example, "inspect.json"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);

        var export = AefInspectExporter.Export(Path.Combine(example, "input"), new AefInspectExportOptions { IgnoreOverlays = true });

        var at = Enumerable.Range(0, Math.Min(want.Length, export.Text.Length)).FirstOrDefault(i => want[i] != export.Text[i], -1);
        Assert.True(want == export.Text, $"{name}: the export differs at {at}:\nexpected …{Around(want, at)}…\ngot      …{Around(export.Text, at)}…");
    }

    [Fact]
    public void ThePagesWorkedExample_IsTheFirstSampleOfTheExport()
    {
        // The page's blocks: the corpus line of triage/policy (the second of the input run), the first sample without
        // triage/groundedness, and that score apart, as text (Inspect writes it with the NaN token).
        var (json, text) = WorkedExampleBlocks();
        var input = Path.Combine(Interop, "examples", "aef-inspect", "input");
        Assert.True(AefOtelExporterTests.SameJson(JsonNode.Parse(json[0]), JsonNode.Parse(File.ReadAllLines(Path.Combine(input, "results.ndjson"))[1])));

        var export = AefInspectExporter.Export(input, new AefInspectExportOptions { IgnoreOverlays = true });

        var sample = InspectJson.Parse(Encoding.UTF8.GetBytes(export.Text))!["samples"]![0]!.AsObject();
        var groundedness = sample["scores"]!.AsObject()["triage/groundedness"]!.DeepClone();
        sample["scores"]!.AsObject().Remove("triage/groundedness");
        Assert.True(AefOtelExporterTests.SameJson(JsonNode.Parse(json[1]), sample), sample.ToJsonString());
        var block = InspectJson.Parse(Encoding.UTF8.GetBytes("{" + text + "}"))!;
        Assert.True(AefOtelExporterTests.SameJson(block["triage/groundedness"], groundedness), groundedness.ToJsonString());
    }

    public static TheoryData<string, int> Refusals()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "aef-inspect", "aef-inspect-trials", "aef-inspect-edges" })
        {
            var refusals = JsonNode.Parse(File.ReadAllText(Path.Combine(Interop, "examples", name, "expected.json")))!["refusals"]?.AsArray() ?? new JsonArray();
            for (var i = 0; i < refusals.Count; i++)
            {
                if ((string?)refusals[i]!["args"]![0] == "to-inspect")
                {
                    data.Add(name, i);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusalOfTheExamples_IsRefused_NamingItsRule_AndNothingIsWritten(string name, int index)
    {
        var example = Path.Combine(Interop, "examples", name);
        var refusal = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["refusals"]![index]!;
        var args = refusal["args"]!.AsArray().Select(a => (string)a!).ToList();
        var output = Path.Combine(_root, $"refused-{name}-{index}.json");
        Directory.CreateDirectory(_root);

        var refused = Assert.Throws<AefInspectExportException>(() => AefInspectExporter.ExportToFile(
            Path.Combine(example, args[1]), output, new AefInspectExportOptions { IgnoreOverlays = args.Contains("--ignore-overlays") }));

        Assert.Contains((string)refusal["says"]!, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ARunWithOverlayEvents_IsRefused_OrWithIgnoreOverlaysConvertedFromItsSealedLines()
    {
        // IN-4, on the example's input (an annotate event on triage/helpfulness, and a waiver): neither goes into the log,
        // which holds the sealed lines.
        var input = Path.Combine(Interop, "examples", "aef-inspect", "input");

        Assert.Contains("IN-4", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(input)).Message, StringComparison.Ordinal);
        var export = AefInspectExporter.Export(input, new AefInspectExportOptions { IgnoreOverlays = true });

        Assert.Contains(export.Notes, n => n.Contains("IN-4", StringComparison.Ordinal));
        var helpfulness = InspectJson.Parse(Encoding.UTF8.GetBytes(export.Text))!["samples"]![0]!["scores"]!["triage/helpfulness"]!;
        Assert.Equal("failed", (string)helpfulness["metadata"]!["aef"]!["state"]!);
        Assert.Null(helpfulness["history"]);
    }

    // ------------------------------------------------------------------ IN-1: the eval header

    [Fact]
    public void TheEvalHeader_IsTheSuiteWithoutSuite_AModelSubjectWithoutModel_AndTheJudgeUnderJudge()
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), header: h => h with
        {
            Subject = new AefSubject { Ref = "model:openai/gpt-5.1-mini", Kind = AefSubjectKind.Model },
            Judges = [new AefJudge { Model = "openai/gpt-5.1", Provider = "openai", Mode = AefJudgeMode.Single }],
        });

        var eval = Log(dir)["eval"]!;

        Assert.Equal(("bookings", "7", "openai/gpt-5.1-mini"), ((string)eval["task"]!, (string)eval["task_version"]!, (string)eval["model"]!));
        Assert.Equal("""{"judge":{"model":"openai/gpt-5.1"}}""", eval["model_roles"]!.ToJsonString());
        Assert.Equal("""{"samples":1,"sample_ids":["k1"]}""", eval["dataset"]!.ToJsonString());
        Assert.Null(eval["metadata"]!["aef"]!["judges"]);   // neither a rubric digest nor a calibration to keep
    }

    [Fact]
    public void AnAgentSubject_IsItsRefAsWritten_AndRunsWithoutSuiteOrWithTwoJudges_AreRefused()
    {
        var agent = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])));
        Assert.Equal("agent:support/triage", (string)Log(agent)["eval"]!["model"]!);

        var noSuite = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), header: h => h with { Suite = null });
        var otherKind = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), header: h => h with { Suite = h.Suite! with { Ref = "dataset:bookings" } });
        var twoJudges = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), header: h => h with
        {
            Judges = [new AefJudge { Model = "a" }, new AefJudge { Model = "b" }],
        });

        Assert.Contains("IN-1", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(noSuite)).Message, StringComparison.Ordinal);
        Assert.Contains("is not suite:<task>", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(otherKind)).Message, StringComparison.Ordinal);
        Assert.Contains("2 judges", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(twoJudges)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the table and IN-2

    [Fact]
    public void AScore_IsItsValueOrLabel_TwoScoresAMap_AndALineWithoutScoresNaNWithItsState()
    {
        var dir = Write(w =>
        {
            w.AddResult(Leaf("k1", "q", AefState.Passed, null) with { Scores = [new AefScore { Metric = "m", Value = 1, Label = "C" }] });
            w.AddResult(Leaf("k1", "r", AefState.Failed, [("m", 0.25), ("n", 3)]) with { Reason = "two scores" });
            w.AddResult(Leaf("k1", "s", AefState.Passed, null));   // a code check's verdict: no score (IN-2)
        });

        var scores = Log(dir)["samples"]![0]!["scores"]!;

        Assert.Equal("C", (string)scores["q"]!["value"]!);
        Assert.Equal("""{"m":0.25,"n":3}""", scores["r"]!["value"]!.ToJsonString());
        Assert.Equal("two scores", (string)scores["r"]!["explanation"]!);
        Assert.True(InspectJson.IsNaN(scores["s"]!["value"]));
        Assert.Equal("passed", (string)scores["s"]!["reason"]!);
        Assert.Equal("passed", (string)scores["s"]!["metadata"]!["aef"]!["state"]!);
    }

    [Fact]
    public void ARunsStatus_GivesTheLogsStatus_AnAbortedRunsReasonIsTheErrorMessage()
    {
        var aborted = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), status: AefRunStatus.Aborted);
        var log = Log(aborted);
        Assert.Equal("error", (string)log["status"]!);
        Assert.Equal("the target went away", (string)log["error"]!["message"]!);

        var running = Path.Combine(_root, "running");
        Create(running).AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]));
        var started = Log(running);
        Assert.Equal("started", (string)started["status"]!);
        Assert.Null(started["results"]);
        Assert.Null(started["stats"]!["completed_at"]);
    }

    // ------------------------------------------------------------------ IN-3: results

    [Fact]
    public void Results_AreOneEvalScorePerSummaryEntry_AnAggregateUnderItsMethod_AndANullValueNaN()
    {
        var dir = Write(w =>
        {
            // Two lanes: a line belongs to the lane it names ([SUM-3]).
            w.AddResult(Leaf("k1", "q", AefState.Scored, [("m", 0.2)]) with { Lane = "main" });
            w.AddResult(Leaf("k2", "q", AefState.Scored, [("m", 0.6)]) with { Lane = "main" });
        }, summary: new AefSummary
        {
            Lanes =
            [
                new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("median") }]),
                new AefSummaryLane("other", [new AefSummaryEntry { Metric = "n", Path = "absent" }]),
            ],
        });

        var results = Log(dir)["results"]!;

        Assert.Equal((2, 2), ((int)results["total_samples"]!, (int)results["completed_samples"]!));
        var median = results["scores"]![0]!;
        Assert.Equal(("q", "q", 2), ((string)median["name"]!, (string)median["scorer"]!, (int)median["scored_samples"]!));
        Assert.Equal(0.4, (double)median["metrics"]!["median"]!["value"]!, 12);
        Assert.Equal("main", (string)median["metadata"]!["aef"]!["lane"]!);
        Assert.True(InspectJson.IsNaN(results["scores"]![1]!["metrics"]!["mean"]!["value"]));
    }

    [Fact]
    public void TwoSummaryEntriesAtOnePath_OrAnEntryOfACountMetric_AreRefused()
    {
        var twoAtOnePath = Write(w => w.AddResult(Leaf("k1", "q", AefState.Scored, [("m", 1), ("n", 2)])), summary: new AefSummary
        {
            Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }, new AefSummaryEntry { Metric = "n", Path = "q" }])],
        });
        var count = Write(w => w.AddResult(Leaf("k1", "q", AefState.Scored, [("c", 3)])), summary: new AefSummary
        {
            Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "c", Path = "q" }])],
        });

        Assert.Contains("one EvalScore per scorer (IN-3)", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(twoAtOnePath)).Message, StringComparison.Ordinal);
        Assert.Contains("kind count", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(count)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ IN-5: samples

    [Fact]
    public void ASample_TakesItsInputAndTargetFromItsEvidence_AndSumsItsLinesUsagePerRoleAndModel()
    {
        var dir = Write(w =>
        {
            w.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Input, Link = AefEvidenceLink.ToBlob(w.PutBlob("Refund?"u8)) });
            w.AddEvidence(new AefEvidence { EvidenceId = "E-2", Kind = AefEvidenceKind.Expected, Link = AefEvidenceLink.ToBlob(w.PutBlob("Yes."u8)) });
            var root = w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with
            {
                Evidence = ["E-1"],
                StartedAt = AefTime.Parse("2026-10-01T10:00:01Z"),
                EndedAt = AefTime.Parse("2026-10-01T10:00:03.5Z"),
                DurationMs = 2500,
                Aggregation = new AefAggregation { Strategy = AefAggregationStrategy.WeightedSum, RulePath = AefRulePath.Threshold, Measured = 1, Total = 1 },
                Usage = [new AefUsage { Role = AefUsageRole.Agent, InputTokens = 10, OutputTokens = 2 }],
            });
            root.AddChild(Leaf("k1", "q/a", AefState.Passed, [("m", 1)]) with
            {
                Evidence = ["E-2"],
                Component = new AefComponent(1, true),
                Annotator = new AefAnnotator { Kind = AefAnnotatorKind.Llm, Model = "judge-1" },
                StartedAt = AefTime.Parse("2026-10-01T10:00:02Z"),
                Usage =
                [
                    new AefUsage { Role = AefUsageRole.Agent, InputTokens = 5, OutputTokens = 1, CacheReadInputTokens = 4 },
                    new AefUsage { Role = AefUsageRole.Judge, InputTokens = 7, OutputTokens = 3, ReasoningOutputTokens = 2, CostUsd = 0.25 },
                ],
            });
        });

        var sample = Log(dir)["samples"]![0]!;

        Assert.Equal(("Refund?", "Yes."), ((string)sample["input"]!, (string)sample["target"]!));
        Assert.Equal("""{"agent":{"input_tokens":15,"output_tokens":3,"total_tokens":18,"input_tokens_cache_read":4},"judge":{"input_tokens":7,"output_tokens":3,"total_tokens":10,"reasoning_tokens":2,"total_cost":0.25}}""", sample["role_usage"]!.ToJsonString());
        Assert.Equal("""{"judge-1":{"input_tokens":7,"output_tokens":3,"total_tokens":10,"reasoning_tokens":2,"total_cost":0.25}}""", sample["model_usage"]!.ToJsonString());
        Assert.Equal(("2026-10-01T10:00:01Z", "2026-10-01T10:00:03.5Z", 2.5), ((string)sample["started_at"]!, (string)sample["completed_at"]!, (double)sample["total_time"]!));
        Assert.Equal("""{"weight":1,"required":true}""", sample["scores"]!["q/a"]!["metadata"]!["aef"]!["component"]!.ToJsonString());
    }

    [Fact]
    public void OutputEvidence_InputEvidenceThatIsNoBlob_TwoInputsOfOneSample_AndTwoRootTimes_AreRefused()
    {
        var output = Write(w =>
        {
            w.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Output, Link = AefEvidenceLink.ToBlob(w.PutBlob("answer"u8)) });
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with { Evidence = ["E-1"] });
        });
        var uri = Write(w =>
        {
            w.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Input, Link = AefEvidenceLink.ToUri("https://example.com/cases/k1") });
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with { Evidence = ["E-1"] });
        });
        var twoInputs = Write(w =>
        {
            w.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Input, Link = AefEvidenceLink.ToBlob(w.PutBlob("one"u8)) });
            w.AddEvidence(new AefEvidence { EvidenceId = "E-2", Kind = AefEvidenceKind.Input, Link = AefEvidenceLink.ToBlob(w.PutBlob("two"u8)) });
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with { Evidence = ["E-1", "E-2"] });
        });
        var twoTimes = Write(w =>
        {
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with { StartedAt = AefTime.Parse("2026-10-01T10:00:01Z") });
            w.AddResult(Leaf("k1", "r", AefState.Passed, [("m", 1)]) with { StartedAt = AefTime.Parse("2026-10-01T10:00:02Z") });
        });

        foreach (var (dir, says) in new[] { (output, "output evidence"), (uri, "not a blob of the run"), (twoInputs, "2 input records"), (twoTimes, "2 different startedAt") })
        {
            var refused = Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(dir));
            Assert.Contains(says, refused.Message, StringComparison.Ordinal);
            Assert.Contains("IN-5", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EvidenceNoLineCites_IsNeitherCarriedNorRefused_AndACitedBlobThatIsNotUtf8_RefusesTheExport()
    {
        // R7I-6: an uncited output record, and an uncited input record that is no blob. IN-12: a cited input that is not UTF-8.
        var uncited = Write(w =>
        {
            w.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Output, Link = AefEvidenceLink.ToBlob(w.PutBlob("answer"u8)) });
            w.AddEvidence(new AefEvidence { EvidenceId = "E-2", Kind = AefEvidenceKind.Input, Link = AefEvidenceLink.ToUri("https://example.com/cases/k1") });
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]));
        });
        var notUtf8 = Write(w =>
        {
            w.AddEvidence(new AefEvidence { EvidenceId = "E-1", Kind = AefEvidenceKind.Input, Link = AefEvidenceLink.ToBlob(w.PutBlob([0x66, 0xFF, 0x6F])) });
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with { Evidence = ["E-1"] });
        });

        Assert.Equal("", (string)Log(uncited)["samples"]![0]!["input"]!);
        Assert.Contains("IN-12", Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(notUtf8)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithContentCaptureOff_GivesNoExplanation_NotEvenAReason()
    {
        // IN-13, on the example's input-off run: c2's skipped line has a reason, and no explanation.
        var export = AefInspectExporter.Export(Path.Combine(Interop, "examples", "aef-inspect-edges", "input-off"));

        var scores = InspectJson.Parse(Encoding.UTF8.GetBytes(export.Text))!["samples"]!.AsArray().Select(s => s!["scores"]!["q"]!).ToList();
        Assert.All(scores, s => Assert.Null(s["explanation"]));
        Assert.Equal("skipped", (string)scores[1]["reason"]!);
        Assert.Contains(export.Notes, n => n.Contains("IN-13", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("model:acme/gpt%20x", true, "acme/gpt x")]
    [InlineData("model:%C3%A9t%C3%A9", true, "été")]
    [InlineData("model:-", true, "")]
    [InlineData("model:%2D", true, "-")]
    [InlineData("agent:support/triage", false, "agent:support/triage")]
    [InlineData("agent:Support%20Agent", false, "agent:Support Agent")]
    [InlineData("model:50%", true, "50%")]               // does not decode: kept as written
    [InlineData("model:bad%FF", true, "bad%FF")]         // not UTF-8: kept as written
    public void TheModelName_IsTheRefsNameDecodedAsEnc13EncodesIt(string subjectRef, bool modelSubject, string model) =>
        Assert.Equal(model, AefInspectExporter.ModelName(subjectRef, modelSubject));

    [Fact]
    public void TheSummarysUsage_IsTheLogsStatsPerRoleAndPerModel()
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), summary: new AefSummary
        {
            Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])],
            Usage =
            [
                new AefUsage { Role = AefUsageRole.Agent, Model = "a", InputTokens = 1, OutputTokens = 1 },
                new AefUsage { Role = AefUsageRole.Judge, Model = "j1", InputTokens = 2, OutputTokens = 2, CostUsd = 0.5 },
                new AefUsage { Role = AefUsageRole.Judge, Model = "j2", InputTokens = 3, OutputTokens = 3, CostUsd = 1 },
            ],
        });

        var stats = Log(dir)["stats"]!;

        Assert.Equal(["a", "j1", "j2"], stats["model_usage"]!.AsObject().Select(m => m.Key));
        Assert.Equal("""{"input_tokens":5,"output_tokens":5,"total_tokens":10,"total_cost":1.5}""", stats["role_usage"]!["judge"]!.ToJsonString());
    }

    // ------------------------------------------------------------------ IN-11, and the file

    [Fact]
    public void AnInvalidRun_IsRefused()
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])));
        File.AppendAllText(Path.Combine(dir, "results.ndjson"), "{\"not\":\"a result\"}\n");

        var refused = Assert.Throws<AefInspectExportException>(() => AefInspectExporter.Export(dir));

        Assert.Contains("results.ndjson:2 schema", refused.Message, StringComparison.Ordinal);
        Assert.Contains("IN-11", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportToFile_WritesTheLog_AndNeverOverAnExistingFile()
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])));
        var output = Path.Combine(_root, "inspect.json");

        var export = AefInspectExporter.ExportToFile(dir, output);

        Assert.Equal(export.ToBytes(), File.ReadAllBytes(output));
        Assert.EndsWith("}\n", File.ReadAllText(output), StringComparison.Ordinal);
        Assert.Throws<IOException>(() => AefInspectExporter.ExportToFile(dir, output));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(4.21, "4.21")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.2345678901234567e20, "1.2345678901234567e+20")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    public void ANumber_IsWrittenAsPythonsReprWritesIt(double value, string text) => Assert.Equal(text, InspectJson.Repr(value));

    // ------------------------------------------------------------------ helpers

    private static string Around(string text, int at) => at < 0 ? "" : text[Math.Max(0, at - 60)..Math.Min(text.Length, at + 60)];

    // The page's "Worked example": its json blocks, and its text block.
    private static (List<string> Json, string Text) WorkedExampleBlocks()
    {
        var page = File.ReadAllText(Path.Combine(Interop, "inspect.md"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var section = page[page.IndexOf("\n## Worked example\n", StringComparison.Ordinal)..];
        List<string> Blocks(string fence)
        {
            var blocks = new List<string>();
            for (var at = section.IndexOf(fence, StringComparison.Ordinal); at >= 0; at = section.IndexOf(fence, at + 1, StringComparison.Ordinal))
            {
                var start = at + fence.Length;
                blocks.Add(section[start..section.IndexOf("\n```", start, StringComparison.Ordinal)]);
            }

            return blocks;
        }

        return (Blocks("```json\n"), Blocks("```text\n").Single());
    }

    private static JsonObject Log(string dir) => InspectJson.Parse(Encoding.UTF8.GetBytes(AefInspectExporter.Export(dir).Text))!.AsObject();

    private string Write(
        Action<AefRunWriter> results, Func<AefRunHeader, AefRunHeader>? header = null, AefSummary? summary = null, AefRunStatus status = AefRunStatus.Completed)
    {
        var dir = Path.Combine(_root, $"run-{Guid.NewGuid():N}");
        var writer = Create(dir, header);
        results(writer);
        writer.SetSummary(summary ?? new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])] });
        writer.Close(status, Start.AddMinutes(5), status == AefRunStatus.Aborted ? "the target went away" : null);
        return dir;
    }

    private static AefRunWriter Create(string dir, Func<AefRunHeader, AefRunHeader>? header = null)
    {
        var run = new AefRunHeader
        {
            RunId = "inspect-test",
            Producer = new AefProducer { Name = "test", Version = "1.0" },
            Subject = new AefSubject { Ref = "agent:support/triage", Kind = AefSubjectKind.Agent },
            Suite = new AefSuite { Ref = "suite:bookings", Version = "7", ExecutionPolicy = new AefExecutionPolicy { TrialsPerCase = 1 } },
            StartedAt = Start,
            ContentCapture = AefContentCapture.On,
            Execution = new AefExecution { TargetMode = AefTargetMode.Live },
        };
        var writer = AefRunWriter.Create(dir, header is null ? run : header(run));
        writer.SetMetrics([Metric("m", AefMetricKind.Score), Metric("n", AefMetricKind.Score), Metric("c", AefMetricKind.Count)]);
        return writer;
    }

    private static AefMetric Metric(string id, AefMetricKind kind) =>
        new() { Id = id, Kind = kind, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Unbounded };

    private static AefResult Leaf(string caseId, string path, AefState state, (string Metric, double Value)[]? scores) => new()
    {
        CaseId = caseId,
        Path = path,
        Evaluator = new AefEvaluator("code:q"),
        State = state,
        Scores = scores?.Select(s => new AefScore { Metric = s.Metric, Value = s.Value }).ToList(),
    };
}
