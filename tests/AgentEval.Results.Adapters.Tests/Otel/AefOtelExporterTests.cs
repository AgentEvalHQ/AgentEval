// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Otel;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Otel;

/// <summary>
/// AEF → OpenTelemetry (contracts/aef/1/interop/opentelemetry.md), written from the page's text: the table, the rules
/// "Beyond the table" (OT-1 to OT-3, settled 10-09) and the worked example. The checked examples
/// (interop/examples/aef-otel-aef, aef-otel-aef-redteam) are reproduced from their input runs: each folder's
/// expected.json names the step (<c>to-otel input → otel.jsonl</c>). The page does not fix the bytes (number spelling,
/// member and attribute order), so the lines are compared as JSON values: objects by member, arrays in order, numbers by
/// value, strings exactly.
/// </summary>
public sealed class AefOtelExporterTests : IDisposable
{
    private static readonly string Interop = Path.Combine(AefTestRuns.RepoRoot, "contracts", "aef", "1", "interop");
    private static readonly string Conformance = Path.Combine(AefTestRuns.RepoRoot, "contracts", "aef", "1", "conformance");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = AefTestRuns.TempPath("aef-otel");

    public void Dispose() => AefTestRuns.Delete(_root);

    // ------------------------------------------------------------------ the checked examples

    [Theory]
    [InlineData("aef-otel-aef")]
    [InlineData("aef-otel-aef-redteam")]
    public void TheCheckedExample_IsReproduced_FromItsInputRun(string name)
    {
        var example = Path.Combine(Interop, "examples", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!;
        var step = expected["steps"]!.AsArray().Single(s => (string?)s!["args"]![0] == "to-otel")!;
        var input = Path.Combine(example, (string)step["args"]![1]!);
        var want = File.ReadAllLines(Path.Combine(example, (string)step["expected"]!), new UTF8Encoding(false));

        var export = AefOtelExporter.Export(input);

        Assert.Equal(AefOutcome.Intact, export.Outcome);
        Assert.Equal(want.Length, export.Lines.Count);
        for (var i = 0; i < want.Length; i++)
        {
            Assert.True(SameJson(JsonNode.Parse(want[i]), JsonNode.Parse(export.Lines[i])),
                $"{name} line {i + 1}:\nexpected {want[i]}\ngot      {export.Lines[i]}");
        }

        Assert.Equal(want.Sum(l => JsonNode.Parse(l)!["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]!.AsArray().Count), export.Events);
    }

    [Fact]
    public void ThePagesWorkedExample_IsTheExportOfItsCorpusLine()
    {
        // The page's own blocks: the corpus line for triage/helpfulness of case-17, then its export. The line is the third
        // of the example's input run, and the export's third line is the page's block.
        var blocks = WorkedExampleBlocks();
        var input = Path.Combine(Interop, "examples", "aef-otel-aef", "input");
        Assert.True(SameJson(JsonNode.Parse(blocks[0]), JsonNode.Parse(File.ReadAllLines(Path.Combine(input, "results.ndjson"))[2])));

        var export = AefOtelExporter.Export(input);

        Assert.True(SameJson(JsonNode.Parse(blocks[1]), JsonNode.Parse(export.Lines[2])), $"expected {blocks[1]}\ngot      {export.Lines[2]}");
    }

    [Fact]
    public void AWithheldReasoningBlob_GivesNoExplanation_AndWithoutThePolicyTheRunIsRefused()
    {
        // The corpus run runs/withheld-blob: the reasoning blob of triage/helpfulness is redacted by an identity its
        // policy lets redact ([OVL-10]). Its event has no explanation; every other line exports as in aef-otel-aef.
        var run = Path.Combine(Conformance, "runs", "withheld-blob", "run");
        var policy = TrustPolicy.Parse(File.ReadAllBytes(Path.Combine(Conformance, "runs", "withheld-blob", "policy.json")));
        var reference = File.ReadAllLines(Path.Combine(Interop, "examples", "aef-otel-aef", "otel.jsonl"), new UTF8Encoding(false));

        var export = AefOtelExporter.Export(run, new AefOtelExportOptions { Policy = policy });

        Assert.Equal(reference.Length, export.Lines.Count);
        var helpfulness = JsonNode.Parse(export.Lines[2])!;
        Assert.DoesNotContain("gen_ai.evaluation.explanation", Keys(Record(helpfulness)));
        Assert.Contains(export.Notes, n => n.Contains("withholds", StringComparison.Ordinal));
        for (var i = 0; i < reference.Length; i++)
        {
            if (i != 2)
            {
                Assert.True(SameJson(JsonNode.Parse(reference[i]), JsonNode.Parse(export.Lines[i])), $"line {i + 1}: {export.Lines[i]}");
            }
        }

        var refused = Assert.Throws<AefOtelExportException>(() => AefOtelExporter.Export(run));
        Assert.Contains("missing", refused.Message, StringComparison.Ordinal);
        Assert.Contains("OT-8", refused.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, int> Refusals()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "aef-otel-aef", "aef-otel-aef-redteam" })
        {
            var refusals = JsonNode.Parse(File.ReadAllText(Path.Combine(Interop, "examples", name, "expected.json")))!["refusals"]?.AsArray() ?? new JsonArray();
            for (var i = 0; i < refusals.Count; i++)
            {
                if ((string?)refusals[i]!["args"]![0] == "to-otel")
                {
                    data.Add(name, i);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusedExportOfTheExamples_IsRefused_NamingItsRule_AndNothingIsWritten(string name, int index)
    {
        // OT-8 (a run that does not verify) and OT-9 (a time timeUnixNano cannot hold, a reasoning blob that is not UTF-8):
        // the whole export is refused, and nothing is written.
        var example = Path.Combine(Interop, "examples", name);
        var refusal = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["refusals"]![index]!;
        var output = Path.Combine(_root, $"refused-{index}.jsonl");
        Directory.CreateDirectory(_root);

        var refused = Assert.Throws<AefOtelExportException>(() => AefOtelExporter.ExportToFile(Path.Combine(example, (string)refusal["args"]![1]!), output));

        Assert.Contains((string)refusal["says"]!, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("1970-01-01T00:00:00Z", false)]                        // 0 means unknown in OTLP (OT-9)
    [InlineData("1970-01-01T00:00:00.000000001Z", true)]               // the first time it holds
    [InlineData("2554-07-21T23:34:33.709551615Z", true)]               // the last: 2^64 − 1 nanoseconds
    [InlineData("2554-07-21T23:34:33.709551616Z", false)]
    [InlineData("1969-12-31T23:59:59.999999999Z", false)]
    public void ATimeTimeUnixNanoCannotHold_RefusesTheExport(string endedAt, bool exported)
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with { StartedAt = AefTime.Parse(endedAt), EndedAt = AefTime.Parse(endedAt) }));

        if (exported)
        {
            var time = (string)Record(JsonNode.Parse(AefOtelExporter.Export(dir).Lines.Single())!)["timeUnixNano"]!;
            Assert.Equal(endedAt.StartsWith("1970", StringComparison.Ordinal) ? "1" : ulong.MaxValue.ToString(CultureInfo.InvariantCulture), time);
        }
        else
        {
            Assert.Contains("OT-9", Assert.Throws<AefOtelExportException>(() => AefOtelExporter.Export(dir)).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSealedResultsAreExported_AnOverrideDoesNotChangeTheState()
    {
        // OT-7: the lines as results.ndjson holds them, not the effective view (§4.3).
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Failed, [("m", 0.2)])));
        AefSealer.Seal(dir, new AefSealOptions { SealedBy = AefSealedBy.Producer, TimeProvider = new Clock(Start.AddMinutes(6)) });
        var overlay = AefOverlayWriter.Open(dir);
        overlay.Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Override,
            Target = new AefOverlayTarget { Result = AefResultId.Compute("otel-test", "k1", "q") },
            State = AefState.Passed,
            Reason = "Re-graded by hand.",
            By = new AefIdentity("git:alice@example.com", AefAssurance.SelfAttested),
            At = Start.AddDays(1),
        });
        overlay.SealBatch();
        Assert.Equal("passed", EffectiveView.Compute(dir, AefTime.Parse("2026-10-09T00:00:00Z"), null).Results.Single().EffectiveState);

        var export = AefOtelExporter.Export(dir);

        Assert.Equal(AefOutcome.Intact, export.Outcome);
        Assert.Equal("failed", (string)Attr(Record(JsonNode.Parse(export.Lines.Single())!), "gen_ai.evaluation.score.label")!["stringValue"]!);
    }

    [Fact]
    public void TheComparison_ReadsAnAttributeListAsAMap()
    {
        // The page fixes values, not bytes (R7N-3): the order of attributes is free.
        var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(Interop, "examples", "aef-otel-aef", "otel.jsonl"))[8])!;
        var reordered = line.DeepClone();
        var attributes = Record(reordered)["attributes"]!.AsArray();
        var items = attributes.Select(a => a!.DeepClone()).Reverse().ToList();
        attributes.Clear();
        items.ForEach(attributes.Add);

        Assert.True(SameJson(line, reordered));
        attributes.RemoveAt(0);
        Assert.False(SameJson(line, reordered));
    }

    // ------------------------------------------------------------------ the table and the rules, on written runs

    [Fact]
    public void EachScoreIsAnEvent_OfOneLogsDataLine_AndTheLineFactsGoOnEachEvent()
    {
        var dir = Write(w =>
        {
            w.AddResult(Leaf("k1", "q", AefState.Failed, [("m", 0.25), ("n", 3)]) with
            {
                EndedAt = AefTime.Parse("2026-10-01T10:00:01.5Z"),
                TraceLink = new AefTraceLink("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"), // DevSkim: ignore DS173237 — W3C Trace Context example ids
                Reason = "two scores",
            });
        });

        var export = AefOtelExporter.Export(dir);

        var records = Records(JsonNode.Parse(Assert.Single(export.Lines))!);
        Assert.Equal(2, records.Count);
        Assert.Equal(2, export.Events);
        Assert.Equal(["m", "n"], records.Select(r => Attr(r, "gen_ai.evaluation.name")!["stringValue"]!.GetValue<string>()));
        Assert.Equal([0.25, 3.0], records.Select(r => Attr(r, "gen_ai.evaluation.score.value")!["doubleValue"]!.GetValue<double>()));
        foreach (var record in records)
        {
            var nanos = new DateTimeOffset(2026, 10, 1, 10, 0, 1, 500, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.Equal(nanos.ToString(CultureInfo.InvariantCulture), (string)record["timeUnixNano"]!);
            Assert.Equal(("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"), ((string)record["traceId"]!, (string)record["spanId"]!)); // DevSkim: ignore DS173237 — W3C Trace Context example ids
            Assert.Equal("gen_ai.evaluation.result", (string)record["eventName"]!);
            Assert.Equal("failed", (string)Attr(record, "gen_ai.evaluation.score.label")!["stringValue"]!);
            Assert.Equal("two scores", (string)Attr(record, "gen_ai.evaluation.explanation")!["stringValue"]!);
            Assert.Equal("k1", (string)Attr(record, "test.case.name")!["stringValue"]!);
        }
    }

    [Fact]
    public void ATraceLinkWithTraceIdOnly_GivesNoSpanId_StartedAtIsTheTimeWithoutEndedAt_AndNoTimeIsNone()
    {
        var dir = Write(w =>
        {
            w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)]) with
            {
                StartedAt = AefTime.Parse("1970-01-01T00:00:00.000000001Z"),
                TraceLink = new AefTraceLink("4bf92f3577b34da6a3ce929d0e0e4736"), // DevSkim: ignore DS173237 — W3C Trace Context example id
            });
            w.AddResult(Leaf("k2", "q", AefState.Passed, [("m", 1)]));
        });

        var export = AefOtelExporter.Export(dir);

        var first = Records(JsonNode.Parse(export.Lines[0])!).Single();
        Assert.Equal("1", (string)first["timeUnixNano"]!);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", (string)first["traceId"]!); // DevSkim: ignore DS173237 — W3C Trace Context example id
        Assert.False(first.ContainsKey("spanId"));
        var second = Records(JsonNode.Parse(export.Lines[1])!).Single();
        Assert.False(second.ContainsKey("timeUnixNano"));   // OT-2: OTLP reads none as unknown
        Assert.False(second.ContainsKey("traceId"));
    }

    [Fact]
    public void AnErrorLine_HasErrorTypeOther_BesideTheLabelError_AndATypedAbsenceIsNamedByTheSummary()
    {
        var dir = Write(w =>
        {
            w.AddResult(Leaf("k1", "q", AefState.Error, null) with { Reason = "the judge timed out" });
            w.AddResult(Leaf("k2", "q", AefState.NotApplicable, null) with { Reason = "no context" });
        });

        var export = AefOtelExporter.Export(dir);

        var error = Records(JsonNode.Parse(export.Lines[0])!).Single();
        Assert.Equal(
            ["gen_ai.evaluation.name", "gen_ai.evaluation.score.label", "error.type", "gen_ai.evaluation.explanation", "test.case.name"],
            Keys(error));
        Assert.Equal("m", (string)Attr(error, "gen_ai.evaluation.name")!["stringValue"]!);   // OT-1: the summary's metric at q
        Assert.Equal("_OTHER", (string)Attr(error, "error.type")!["stringValue"]!);
        var absent = Records(JsonNode.Parse(export.Lines[1])!).Single();
        Assert.Equal(["gen_ai.evaluation.name", "gen_ai.evaluation.score.label", "gen_ai.evaluation.explanation", "test.case.name"], Keys(absent));
        Assert.Equal("not_applicable", (string)Attr(absent, "gen_ai.evaluation.score.label")!["stringValue"]!);
    }

    [Fact]
    public void ARunWithContentCaptureOff_GetsNoExplanation_NotEvenAReason()
    {
        // OT-3: SEC-6 forbids the attribute in that run's own logs.otlp.jsonl, and the reason row yields to it.
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Skipped, null) with { Reason = "skipped by --max-cases" }), AefContentCapture.Off);

        var export = AefOtelExporter.Export(dir);

        Assert.DoesNotContain("gen_ai.evaluation.explanation", Keys(Records(JsonNode.Parse(Assert.Single(export.Lines))!).Single()));
        Assert.Contains(export.Notes, n => n.Contains("OT-3", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnvelope_IsTheScopeAgentEval_WithServiceNameAsTheOnlyResourceAttribute_OrNoResource()
    {
        // OT-2.
        var named = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])), serviceName: "support-api");
        var unnamed = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])));

        var withService = JsonNode.Parse(Assert.Single(AefOtelExporter.Export(named).Lines))!["resourceLogs"]!.AsArray().Single()!;
        var withoutService = JsonNode.Parse(Assert.Single(AefOtelExporter.Export(unnamed).Lines))!["resourceLogs"]!.AsArray().Single()!;

        Assert.Equal("""{"attributes":[{"key":"service.name","value":{"stringValue":"support-api"}}]}""", withService["resource"]!.ToJsonString());
        Assert.False(withoutService.AsObject().ContainsKey("resource"));
        Assert.Equal("""{"name":"agenteval"}""", withService["scopeLogs"]![0]!["scope"]!.ToJsonString());
    }

    [Fact]
    public void ALineWithoutScores_ThatTheSummaryNamesNoMetricOrTwoFor_IsRefused_AndNothingIsWritten()
    {
        // OT-1. Two lanes, and a line without lane: it belongs to neither ([SUM-3]).
        var noLane = Write(w => w.AddResult(Leaf("k1", "q", AefState.Skipped, null) with { Reason = "r" }),
            summary: new AefSummary { Lanes = [Lane("a", ("m", "q")), Lane("b", ("m", "q"))] });
        // One lane with two metrics at the line's path.
        var twoMetrics = Write(w => w.AddResult(Leaf("k1", "q", AefState.Skipped, null) with { Reason = "r" }),
            summary: new AefSummary { Lanes = [Lane("a", ("m", "q"), ("n", "q"))] });
        // Its lane names the metric: named after it.
        var inLane = Write(w => w.AddResult(Leaf("k1", "q", AefState.Skipped, null) with { Reason = "r", Lane = "b" }),
            summary: new AefSummary { Lanes = [Lane("a", ("n", "q")), Lane("b", ("m", "q"))] });

        Assert.Contains("OT-1", Assert.Throws<AefOtelExportException>(() => AefOtelExporter.Export(noLane)).Message, StringComparison.Ordinal);
        Assert.Contains("2 metrics (m, n)", Assert.Throws<AefOtelExportException>(() => AefOtelExporter.Export(twoMetrics)).Message, StringComparison.Ordinal);
        Assert.Equal("m", (string)Attr(Records(JsonNode.Parse(AefOtelExporter.Export(inLane).Lines.Single())!).Single(), "gen_ai.evaluation.name")!["stringValue"]!);

        var output = Path.Combine(_root, "refused.jsonl");
        Assert.Throws<AefOtelExportException>(() => AefOtelExporter.ExportToFile(noLane, output));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ARunningRun_ExportsItsScoredLines_AndRefusesALineWithoutScores()
    {
        // A running run has no summary.json: a line without scores names no metric (OT-1).
        var scored = Path.Combine(_root, "running-scored");
        var writer = Create(scored);
        writer.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 0.5)]));
        var pending = Path.Combine(_root, "running-pending");
        var other = Create(pending);
        other.AddResult(Leaf("k1", "q", AefState.Pending, null) with { Reason = "running" });

        var export = AefOtelExporter.Export(scored);

        Assert.Equal(AefOutcome.Unsealed, export.Outcome);
        Assert.Single(export.Lines);
        Assert.Contains("a running one", Assert.Throws<AefOtelExportException>(() => AefOtelExporter.Export(pending)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidRun_IsRefused()
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])));
        File.AppendAllText(Path.Combine(dir, "results.ndjson"), "{\"not\":\"a result\"}\n");

        var refused = Assert.Throws<AefOtelExportException>(() => AefOtelExporter.Export(dir));

        Assert.Contains("results.ndjson:2 schema", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportToFile_WritesTheLinesWithLf_AndNeverOverAnExistingFile()
    {
        var dir = Write(w => w.AddResult(Leaf("k1", "q", AefState.Passed, [("m", 1)])));
        var output = Path.Combine(_root, "otel.jsonl");

        var export = AefOtelExporter.ExportToFile(dir, output);

        Assert.Equal(export.ToBytes(), File.ReadAllBytes(output));
        Assert.EndsWith("\n", File.ReadAllText(output), StringComparison.Ordinal);
        Assert.DoesNotContain("\r", File.ReadAllText(output), StringComparison.Ordinal);
        Assert.Throws<IOException>(() => AefOtelExporter.ExportToFile(dir, output));
    }

    [Fact]
    public void AScoreValue_IsWrittenAsTheLineSpellsIt_AndATextAsAefWritesOne()
    {
        // The page fixes values, not bytes: 1.0 stays 1.0 (the AEF line's own spelling), and non-ASCII text stays UTF-8,
        // only quotes, backslashes and C0 controls escaped (as AEF's writer writes strings).
        var export = AefOtelExporter.Export(Path.Combine(Interop, "examples", "aef-otel-aef", "input"));

        Assert.Contains("{\"doubleValue\":1.0}", export.Lines[1], StringComparison.Ordinal);
        Assert.Contains("« correct » ✓ 👍 مرحبا\\r\\nSecond line (CRLF kept).\\n", export.Lines[2], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    // The page's "Worked example" json blocks, in order.
    private static List<string> WorkedExampleBlocks()
    {
        var page = File.ReadAllText(Path.Combine(Interop, "opentelemetry.md"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var section = page[page.IndexOf("\n## Worked example\n", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", 1, StringComparison.Ordinal)];
        var blocks = new List<string>();
        for (var at = section.IndexOf("```json\n", StringComparison.Ordinal); at >= 0; at = section.IndexOf("```json\n", at + 1, StringComparison.Ordinal))
        {
            var start = at + "```json\n".Length;
            blocks.Add(section[start..section.IndexOf("\n```", start, StringComparison.Ordinal)]);
        }

        return blocks;
    }

    private string Write(Action<AefRunWriter> results, AefContentCapture capture = AefContentCapture.On, string? serviceName = null, AefSummary? summary = null)
    {
        var dir = Path.Combine(_root, $"run-{Guid.NewGuid():N}");
        var writer = Create(dir, capture, serviceName);
        results(writer);
        writer.SetSummary(summary ?? new AefSummary { Lanes = [Lane("main", ("m", "q"))] });
        writer.Close(AefRunStatus.Completed, Start.AddMinutes(5));
        return dir;
    }

    private static AefRunWriter Create(string dir, AefContentCapture capture = AefContentCapture.On, string? serviceName = null)
    {
        var writer = AefRunWriter.Create(dir, new AefRunHeader
        {
            RunId = "otel-test",
            Producer = new AefProducer { Name = "test", Version = "1.0" },
            Subject = new AefSubject
            {
                Ref = "agent:t",
                Kind = AefSubjectKind.Agent,
                Version = "1",
                Telemetry = serviceName is null ? null : new AefSubjectTelemetry { ServiceName = serviceName },
            },
            StartedAt = Start,
            ContentCapture = capture,
            Execution = new AefExecution { TargetMode = AefTargetMode.Live },
        });
        writer.SetMetrics([Metric("m"), Metric("n")]);
        return writer;
    }

    private static AefMetric Metric(string id) =>
        new() { Id = id, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Unbounded };

    private static AefSummaryLane Lane(string lane, params (string Metric, string Path)[] entries) =>
        new(lane, [.. entries.Select(e => new AefSummaryEntry { Metric = e.Metric, Path = e.Path })]);

    private static AefResult Leaf(string caseId, string path, AefState state, (string Metric, double Value)[]? scores) => new()
    {
        CaseId = caseId,
        Path = path,
        Evaluator = new AefEvaluator("code:q"),
        State = state,
        Scores = scores?.Select(s => new AefScore { Metric = s.Metric, Value = s.Value }).ToList(),
    };

    private static JsonObject Record(JsonNode line) => Records(line).Single();

    private static List<JsonObject> Records(JsonNode line) =>
        [.. line["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]!.AsArray().Select(r => r!.AsObject())];

    private static List<string> Keys(JsonObject record) =>
        [.. record["attributes"]!.AsArray().Select(a => (string)a!["key"]!)];

    private static JsonObject? Attr(JsonObject record, string key) =>
        record["attributes"]!.AsArray().SingleOrDefault(a => (string?)a!["key"] == key)?["value"]?.AsObject();

    // Two JSON values are equal: objects member by member whatever their order, arrays in order, numbers by value
    // (1.0 and 1 are one number), strings and literals exactly. An OTLP attribute list (objects with distinct "key"s) is
    // a map, compared whatever its order: the page fixes values, not bytes, and leaves the attributes' order free (R7N-3).
    internal static bool SameJson(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(m => y.TryGetPropertyValue(m.Key, out var v) && SameJson(m.Value, v)),
        (JsonArray x, JsonArray y) when Attributes(x) is { } xs && Attributes(y) is { } ys =>
            xs.Count == ys.Count && xs.All(kv => ys.TryGetValue(kv.Key, out var v) && SameJson(kv.Value, v)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => SameJson(p.First, p.Second)),
        (JsonValue x, JsonValue y) when x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number =>
            double.Parse(x.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) == double.Parse(y.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
        (JsonValue x, JsonValue y) when x.GetValueKind() == JsonValueKind.String && y.GetValueKind() == JsonValueKind.String =>
            string.Equals(x.GetValue<string>(), y.GetValue<string>(), StringComparison.Ordinal),
        (JsonValue x, JsonValue y) => x.GetValueKind() == y.GetValueKind() && x.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null,
        _ => false,
    };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // An OTLP attribute list as a map from key to value, or null when the array is not one.
    private static Dictionary<string, JsonNode?>? Attributes(JsonArray array)
    {
        if (array.Count == 0 || !array.All(i => i is JsonObject o && o.Count == 2 && o["key"] is JsonValue k && k.GetValueKind() == JsonValueKind.String && o.ContainsKey("value")))
        {
            return null;
        }

        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        return array.All(i => map.TryAdd(i!["key"]!.GetValue<string>(), i["value"])) ? map : null;
    }
}
