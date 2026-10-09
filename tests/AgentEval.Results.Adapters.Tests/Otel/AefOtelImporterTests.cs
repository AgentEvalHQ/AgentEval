// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Otel;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Otel;

/// <summary>
/// OpenTelemetry → AEF (contracts/aef/1/interop/opentelemetry.md), written from the page's text: the table, the rules
/// OT-4, OT-5, OT-8 and OT-10, and the refusals (OT-4, OT-6, OT-8). Each checked example's <c>from-otel</c> step
/// (interop/examples/otel-aef, and the way back of aef-otel-aef and aef-otel-aef-redteam) is reproduced and compared with
/// the example's run as JSON values (the page fixes values, not bytes), except the producer, which names the converter
/// ([RUN-15]). Each refusal of the examples is refused, naming its rule.
/// </summary>
public sealed class AefOtelImporterTests : IDisposable
{
    private static readonly string Examples = Path.Combine(AefTestRuns.RepoRoot, "contracts", "aef", "1", "interop", "examples");

    private readonly string _root = AefTestRuns.TempPath("aef-otel-import");

    public void Dispose() => AefTestRuns.Delete(_root);

    public static TheoryData<string, int> Steps() => new()
    {
        { "otel-aef", 0 },
        { "aef-otel-aef", 1 },
        { "aef-otel-aef-redteam", 1 },
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void TheCheckedExample_IsReproduced(string name, int step)
    {
        var example = Path.Combine(Examples, name);
        var args = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["steps"]![step]!;
        var (input, options) = Arguments(example, args["args"]!.AsArray());
        Assert.Equal("from-otel", (string)args["args"]![0]!);
        var expected = Path.Combine(example, (string)args["expected"]!);
        var output = Path.Combine(_root, name);

        var conversion = AefOtelImporter.Import(input, output, options);

        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        Assert.Empty(conversion.Verification.Problems);

        // results.ndjson, metrics.json and summary.json: the example's, as JSON values.
        var want = AefTestRuns.Results(expected);
        var have = AefTestRuns.Results(output);
        Assert.Equal(want.Count, have.Count);
        for (var i = 0; i < want.Count; i++)
        {
            Assert.True(AefOtelExporterTests.SameJson(want[i], have[i]), $"{name} line {i + 1}:\nexpected {want[i].ToJsonString()}\ngot      {have[i].ToJsonString()}");
        }

        foreach (var file in new[] { "metrics.json", "summary.json" })
        {
            Assert.True(AefOtelExporterTests.SameJson(AefTestRuns.Document(expected, file), AefTestRuns.Document(output, file)), $"{file}: {AefTestRuns.Document(output, file).ToJsonString()}");
        }

        // run.json: the example's but for the producer (the converter, [RUN-15]); contentCapture is the converter's
        // choice, on, listed in imported.asserted (OT-4, R7N-10).
        var run = AefTestRuns.Document(output, "run.json");
        Assert.Equal(AefConverter.ProducerName, (string)run["producer"]!["name"]!);
        run.Remove("producer");
        var wantRun = AefTestRuns.Document(expected, "run.json");
        wantRun.Remove("producer");
        Assert.True(AefOtelExporterTests.SameJson(wantRun, run), $"run.json: {run.ToJsonString()}");

        // logs.otlp.jsonl: the source's lines, as JSON values (the log records themselves).
        var source = File.ReadAllLines(input, new UTF8Encoding(false)).Where(l => l.Length > 0).ToList();
        var logs = File.ReadAllLines(Path.Combine(output, "logs.otlp.jsonl"), new UTF8Encoding(false));
        Assert.Equal(source.Count, logs.Length);
        Assert.All(source.Zip(logs), p => Assert.True(AefOtelExporterTests.SameJson(JsonNode.Parse(p.First), JsonNode.Parse(p.Second)), p.Second));

        // The seal: ingest, at the conversion time; closedAt is the run's end.
        var seal = AefTestRuns.Document(output, "seal.json")["predicate"]!;
        var wantSeal = AefTestRuns.Document(expected, "seal.json")["predicate"]!;
        Assert.Equal("ingest", (string)seal["sealedBy"]!);
        foreach (var time in new[] { "sealedAt", "closedAt" })
        {
            Assert.Equal(AefTime.Parse((string)wantSeal[time]!), AefTime.Parse((string)seal[time]!));
        }
    }

    [Fact]
    public void ThePagesWorkedExample_ImportsAsItsBlocks()
    {
        // The page's third and fourth json blocks are the first two lines of examples/otel-aef/run/results.ndjson.
        var example = Path.Combine(Examples, "otel-aef");
        var args = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["steps"]![0]!["args"]!.AsArray();
        var (input, options) = Arguments(example, args);
        var output = Path.Combine(_root, "worked");

        AefOtelImporter.Import(input, output, options);

        var page = File.ReadAllText(Path.Combine(Examples, "..", "opentelemetry.md"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = page.Split("```json\n").Skip(1).Select(b => b[..b.IndexOf("\n```", StringComparison.Ordinal)]).ToList();
        var lines = AefTestRuns.Results(output);
        Assert.True(AefOtelExporterTests.SameJson(JsonNode.Parse(blocks[2]), lines[0]), lines[0].ToJsonString());
        Assert.True(AefOtelExporterTests.SameJson(JsonNode.Parse(blocks[3]), lines[1]), lines[1].ToJsonString());
    }

    public static TheoryData<string, int> Refusals()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "otel-aef", "aef-otel-aef-redteam" })
        {
            var refusals = JsonNode.Parse(File.ReadAllText(Path.Combine(Examples, name, "expected.json")))!["refusals"]!.AsArray();
            for (var i = 0; i < refusals.Count; i++)
            {
                if ((string?)refusals[i]!["args"]![0] == "from-otel")
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
        var example = Path.Combine(Examples, name);
        var refusal = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["refusals"]![index]!;
        var (input, options) = Arguments(example, refusal["args"]!.AsArray());
        var output = Path.Combine(_root, $"refused-{index}");

        var refused = Assert.Throws<AefOtelImportException>(() => AefOtelImporter.Import(input, output, options));

        Assert.Contains((string)refusal["says"]!, refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ErrorTypeAlone_IsTheReason_AndBesideAnExplanationTheExplanationIsKept()
    {
        // OT-5, on the example's input: case-31's faithfulness has error.type timeout alone; case-32's has an explanation.
        var example = Path.Combine(Examples, "otel-aef");
        var (input, options) = Arguments(example, JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["steps"]![0]!["args"]!.AsArray());
        var output = Path.Combine(_root, "ot5");

        AefOtelImporter.Import(input, output, options);

        var lines = AefTestRuns.Results(output).ToDictionary(l => $"{l["caseId"]}/{l["path"]}");
        Assert.Equal(("error", "timeout"), ((string)lines["case-31/faithfulness"]["state"]!, (string)lines["case-31/faithfulness"]["reason"]!));
        Assert.Equal(("error", "The grader timed out after 30 s."), ((string)lines["case-32/faithfulness"]["state"]!, (string)lines["case-32/faithfulness"]["reason"]!));
    }

    [Theory]
    [InlineData("""{"key":"error.type","value":{"stringValue":"timeout"}},{"key":"gen_ai.evaluation.score.value","value":{"doubleValue":0.5}}""", "OT-6")]   // error is a typed absence: no scores
    [InlineData("""{"key":"gen_ai.evaluation.score.label","value":{"stringValue":"error"}},{"key":"error.type","value":{"stringValue":"timeout"}}""", null)]   // error.type is the reason
    [InlineData("""{"key":"gen_ai.evaluation.score.label","value":{"stringValue":"passed"}}""", null)]                                                      // a state with no value: no scores
    public void TheEdgesOfTheTable(string attributes, string? refusedFor)
    {
        var input = Path.Combine(_root, "edge.jsonl");
        Directory.CreateDirectory(_root);
        File.WriteAllText(input, $$$"""{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"eventName":"gen_ai.evaluation.result","attributes":[{"key":"gen_ai.evaluation.name","value":{"stringValue":"m"}},{{{attributes}}},{"key":"test.case.name","value":{"stringValue":"c1"}}]}]}]}]}""" + "\n");
        var options = new AefOtelImportOptions { RunId = "edge", From = "f", SubjectRef = "agent:a", TargetMode = AefTargetMode.Live, TimeProvider = new Clock(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)) };
        var output = Path.Combine(_root, "edge");

        if (refusedFor is not null)
        {
            Assert.Contains(refusedFor, Assert.Throws<AefOtelImportException>(() => AefOtelImporter.Import(input, output, options)).Message, StringComparison.Ordinal);
            return;
        }

        var conversion = AefOtelImporter.Import(input, output, options);
        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        var line = Assert.Single(AefTestRuns.Results(output));
        Assert.False(line.ContainsKey("scores"));
    }

    // The step's arguments as the reference converter takes them: input, {out}, then --run-id, --from, --subject,
    // --subject-kind, --target-mode and --at (the conversion time).
    private static (string Input, AefOtelImportOptions Options) Arguments(string example, JsonArray args)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i + 1 < args.Count; i += 2)
        {
            named[(string)args[i]!] = (string)args[i + 1]!;
        }

        return (Path.Combine(example, (string)args[1]!), new AefOtelImportOptions
        {
            RunId = named["--run-id"],
            From = named["--from"],
            SubjectRef = named["--subject"],
            SubjectKind = AefNames.TryParse<AefSubjectKind>(named["--subject-kind"], out var kind) ? kind.Value : throw new FormatException(named["--subject-kind"]),
            TargetMode = AefNames.TryParse<AefTargetMode>(named["--target-mode"], out var mode) ? mode.Value : throw new FormatException(named["--target-mode"]),
            ContentCapture = named.TryGetValue("--content-capture", out var text)
                ? AefNames.TryParse<AefContentCapture>(text, out var capture) ? capture.Value : throw new FormatException(text)
                : AefContentCapture.On,
            TimeProvider = new Clock(DateTimeOffset.Parse(named["--at"], System.Globalization.CultureInfo.InvariantCulture)),
        });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
