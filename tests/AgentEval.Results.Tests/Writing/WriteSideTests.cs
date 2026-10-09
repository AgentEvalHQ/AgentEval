using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Corpus;
using AgentEval.Results.Writing;
using Driver = AgentEval.Results.Conformance.Program;

namespace AgentEval.Results.Tests.Writing;

/// <summary>
/// The write side of spec 09 (§9.2.1, §9.3): <c>summarize</c>, <c>seal-write</c> and <c>sign</c>, run in process on
/// the corpus's write-vectors and judged as the conformance runner judges them (only its result counts for a claim:
/// tools/aef_conformance.py through the driver, in the container), and the pieces of AgentEval.Results under them:
/// <see cref="AefSummaryWriter"/>, <see cref="AefSealOptions.SealedAt"/>, <see cref="AefSigningKey"/>.
/// </summary>
public class WriteSideTests
{
    private static readonly string Vectors = Path.Combine(AefCorpus.Conformance, "write-vectors");

    public static TheoryData<string> SummarizeVectors() => Names("summarize");

    public static TheoryData<string> SealWriteVectors() => Names("seal-write");

    public static TheoryData<string> SignVectors() => Names("sign");

    public static TheoryData<string> ProduceVectors() => Names("produce");

    // ------------------------------------------------------------------ the corpus, in process

    [Theory]
    [MemberData(nameof(SummarizeVectors))]
    public void Summarize_GivesTheExpectedSummary_AndTheRunWithItVerifies(string name)
    {
        var folder = Path.Combine(Vectors, "summarize", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        var (code, output, error) = Dispatch("summarize", Path.Combine(folder, "run"), Path.Combine(folder, "request.json"));
        if (expected["refused"] is not null)
        {
            Assert.True(code == 2, $"a request the Producer must refuse was summarized: {output}");  // §9.3: an input error
            return;
        }

        Assert.True(code == 0, error);

        var summary = JsonNode.Parse(output)!.AsObject();
        var want = expected["summary"]!;
        Assert.Equal(want["runId"]!.GetValue<string>(), summary["runId"]!.GetValue<string>());
        var lanes = summary["lanes"]!.AsArray();
        Assert.Equal(want["lanes"]!.AsArray().Select(l => l!["lane"]!.GetValue<string>()), lanes.Select(l => l!["lane"]!.GetValue<string>()));
        foreach (var (got, lane) in lanes.Zip(want["lanes"]!.AsArray()))
        {
            var entries = got!["metrics"]!.AsArray();
            Assert.Equal(lane!["metrics"]!.AsArray().Count, entries.Count);
            foreach (var (have, x) in entries.Zip(lane["metrics"]!.AsArray()))
            {
                foreach (var field in new[] { "metric", "path", "N", "n", "notMeasured", "verdict", "rule", "aggregate" })
                {
                    Assert.True(JsonNode.DeepEquals(have![field], x![field]) && have.AsObject().ContainsKey(field) == x.AsObject().ContainsKey(field),
                        $"{field}: {have[field]?.ToJsonString()} for {x[field]?.ToJsonString()}");
                }

                foreach (var field in new[] { "sum", "sumSq", "value" })
                {
                    var (g, w) = (have![field], x![field]);
                    Assert.True(w is null ? g is null : g is not null && AefSummaryCalculator.Matches(g.GetValue<double>(), w.GetValue<double>()),
                        $"{field}: {g?.ToJsonString()} for {w?.ToJsonString()}");
                }
            }
        }

        // §9.3: the run, with this summary.json added, verifies unsealed with no problem.
        using var copy = Copy(Path.Combine(folder, "run"));
        File.WriteAllBytes(Path.Combine(copy.Dir, "summary.json"), AefJsonWriter.Document(summary));
        var verification = AefRunVerifier.Verify(copy.Dir);
        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Empty(verification.Problems);
    }

    [Theory]
    [MemberData(nameof(SealWriteVectors))]
    public void SealWrite_WritesTheExpectedSeal_OrRefusesAndWritesNothing(string name)
    {
        var folder = Path.Combine(Vectors, "seal-write", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        using var copy = Copy(Path.Combine(folder, "run"));
        var before = Files(copy.Dir);

        var (code, output, error) = Dispatch("seal-write", copy.Dir, "--sealed-by", expected["sealedBy"]!.GetValue<string>(), "--sealed-at", expected["sealedAt"]!.GetValue<string>());

        var after = Files(copy.Dir);
        if (expected["refused"] is not null)
        {
            Assert.Equal(2, code);
            Assert.Equal(before.Keys, after.Keys);
            return;
        }

        Assert.True(code == 0, error);
        var manifest = File.ReadAllBytes(Path.Combine(folder, expected["manifest"]!.GetValue<string>()));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(), JsonNode.Parse(output)!["runHash"]!.GetValue<string>());
        Assert.Equal(before.Keys.Append("seal.json").Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, f => Assert.Equal(f.Value, after[f.Key]));

        var seal = JsonNode.Parse(after["seal.json"])!.AsObject();
        Assert.True(AefSchemas.Writer.IsValid("seal", seal));
        var want = Encoding.UTF8.GetString(manifest).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ", 3)).Select(p => (p[2], p[0]));
        Assert.Equal(want, seal["subject"]!.AsArray().Select(s => (s!["name"]!.GetValue<string>(), s["digest"]!["sha256"]!.GetValue<string>())));
        var predicate = seal["predicate"]!.AsObject();
        var wantPredicate = expected["predicate"]!.AsObject();
        Assert.Equal(wantPredicate.Select(m => m.Key).Order(StringComparer.Ordinal), predicate.Select(m => m.Key).Order(StringComparer.Ordinal));
        foreach (var (field, value) in wantPredicate)
        {
            Assert.True(field is "closedAt" or "sealedAt"
                    ? AefTime.Parse(predicate[field]!.GetValue<string>()) == AefTime.Parse(value!.GetValue<string>())
                    : JsonNode.DeepEquals(predicate[field], value),
                $"predicate.{field}: {predicate[field]?.ToJsonString()} for {value?.ToJsonString()}");
        }

        var verification = AefRunVerifier.Verify(copy.Dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
    }

    [Theory]
    [MemberData(nameof(SignVectors))]
    public void Sign_GivesAnEnvelopeThatVerifiesForTheKeysIdentity_AndRefusesAnEd25519Key(string name)
    {
        var folder = Path.Combine(Vectors, "sign", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        var file = Path.Combine(folder, expected["file"]!.GetValue<string>());
        var payloadType = expected["payloadType"]!.GetValue<string>();

        var (code, output, error) = Dispatch("sign", file, Path.Combine(folder, expected["key"]!.GetValue<string>()), "--payload-type", payloadType);

        if (expected["sig"] is not null)
        {
            // Ed25519: AgentEval signs with ECDSA P-256 only ([SIG-2]: a signer uses one of the two).
            Assert.Equal(2, code);
            Assert.Contains("algorithm not supported for signing", error, StringComparison.Ordinal);
            return;
        }

        Assert.True(code == 0, error);
        var envelope = JsonNode.Parse(output)!;
        var signatures = envelope["signatures"]!.AsArray();
        Assert.Single(signatures);
        Assert.Equal(expected["keyid"]!.GetValue<string>(), signatures[0]!["keyid"]!.GetValue<string>());
        foreach (var text in new[] { envelope["payload"]!.GetValue<string>(), signatures[0]!["sig"]!.GetValue<string>() })
        {
            Assert.Equal(text, Convert.ToBase64String(Convert.FromBase64String(text)));   // standard alphabet, padded ([SIG-1])
        }

        var policy = TrustPolicy.Parse(File.ReadAllBytes(Path.Combine(folder, expected["policy"]!.GetValue<string>())));
        var verification = DsseVerifier.Verify(Encoding.UTF8.GetBytes(output), File.ReadAllBytes(file), payloadType, policy);
        Assert.Equal([expected["identity"]!.GetValue<string>()], verification.VerifiesFor);
    }

    [Theory]
    [MemberData(nameof(ProduceVectors))]
    public void Produce_WritesTheExpectedLinesAndSummary_OrRefusesAndWritesNothing(string name)
    {
        var folder = Path.Combine(Vectors, "produce", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        var scenarioFile = Path.Combine(folder, expected["scenario"]!.GetValue<string>());
        using var output = new WriterRun();

        var (code, stdout, error) = Dispatch("produce", scenarioFile, output.Dir);

        if (expected["refused"] is not null)
        {
            Assert.True(code == 2, $"a scenario the Producer must refuse was written: {stdout}");   // §9.3: an input error
            Assert.False(Directory.Exists(output.Dir) && Directory.EnumerateFileSystemEntries(output.Dir).Any(), "files were written although it refused");
            return;
        }

        Assert.True(code == 0, error);

        // §9.3: the four files and no other; run.json and metrics.json the scenario's, as given.
        var scenario = JsonNode.Parse(File.ReadAllBytes(scenarioFile))!;
        Assert.Equal(["metrics.json", "results.ndjson", "run.json", "summary.json"], Files(output.Dir).Keys.Order(StringComparer.Ordinal));
        Assert.True(Same(RunAsWritten(output.Json("run.json")), RunAsWritten(scenario["run"]!.AsObject())), output.Json("run.json").ToJsonString());
        Assert.True(Same(WithoutNulls(output.Json("metrics.json")), WithoutNulls(scenario["metrics"]!.AsObject())), output.Json("metrics.json").ToJsonString());

        // The lines, as a set: matched by case, path and trial, member by member, each valid against the writer schema.
        var lines = output.Lines("results.ndjson");
        Assert.Equal(lines.Count, JsonNode.Parse(stdout)!["results"]!.GetValue<int>());
        Assert.All(lines, l => Assert.True(AefSchemas.Writer.IsValid("result", l), l.ToJsonString()));
        var have = lines.ToDictionary(Key, AsWritten);
        var want = File.ReadAllLines(Path.Combine(folder, expected["results"]!.GetValue<string>()))
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToDictionary(Key, AsWritten);
        Assert.Equal(want.Keys.Order(StringComparer.Ordinal), have.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, line) in want)
        {
            Assert.True(Same(have[key], line), $"{key}: expected {line.ToJsonString()}, got {have[key].ToJsonString()}");
        }

        // summary.json as summarize's output is judged, and the run verifies unsealed with no problem.
        var summary = output.Json("summary.json");
        Assert.True(AefSchemas.Writer.IsValid("summary", summary));
        Assert.True(Same(summary, expected["summary"]), $"expected {expected["summary"]!.ToJsonString()}, got {summary.ToJsonString()}");
        var verification = AefRunVerifier.Verify(output.Dir);
        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Empty(verification.Problems);

        // A line's place in the run (§9.3 matches lines by case, path and trial).
        static string Key(JsonObject line) => $"{line["caseId"]}@{line["path"]}#{line["trial"]?.ToJsonString() ?? "-"}";
    }

    [Fact]
    public void Produce_KeepsEveryMemberOfRunJsonAndMetricsJson_AsGiven()
    {
        // The writer's header holds every member of the writer run schema; a scenario's run.json goes through it whole.
        var scenario = Scenario();
        scenario["run"] = JsonNode.Parse("""
            {"schemaVersion":"1.0","runId":"produce-everything","status":"completed",
             "producer":{"name":"p","version":"1.2.3","runtime":{"name":"dotnet","version":"10.0"}},
             "subject":{"ref":"agent:a/b","kind":"agent","version":"v1","environment":"staging","externalIds":{"foundry":"x-1","jira":null},"telemetry":{"agentId":"a-1","serviceName":"svc"}},
             "deployment":{"ref":"deployment:eu","environment":"eu","endpoint":"https://example.com/agent","externalIds":{"azure":"sub-1"}},
             "suite":{"ref":"suite:golden","version":"2.0.0","digest":"sha256:0000000000000000000000000000000000000000000000000000000000000000","frozen":true,
                      "executionPolicy":{"trialsPerCase":3,"requirePasses":2,"aggregation":"PassAtK","k":3}},
             "judges":[{"model":"judge-1","provider":"lab","mode":"panel","panelSize":3,"rubricDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111",
                        "calibration":{"labelSet":"labels:gold","n":100,"accuracy":0.9,"kappa":0.75,"dangerousErrors":2,"measuredAt":"2026-09-30T12:00:00.5Z"}}],
             "config":{"thresholds":{"quality":{"op":">=","value":0.8}},"temperature":0,"seed":"abc"},
             "startedAt":"2026-10-01T00:00:00Z","endedAt":"2026-10-01T00:01:00.25Z",
             "otel":{"semconvVersion":"1.37","dialects":["gen_ai"],"schemaUrls":["https://opentelemetry.io/schemas/1.37.0"]},
             "contentCapture":"off","costPolicy":{"maxUsd":12.5,"priceTable":"list-2026"},"ext":{"vendor.x":{"a":[1,2]}},
             "provenance":{"planId":"plan-1","planDigest":"2222222222222222222222222222222222222222222222222222222222222222","jobId":"job-1","runnerId":"runner-1"},
             "execution":{"targetMode":"replayed","stimulus":"suite"},
             "imported":{"from":"tool 1.0","asserted":["subject.version"]}}
            """);
        scenario["metrics"] = JsonNode.Parse("""
            {"schemaVersion":"1.0","metrics":[{"id":"quality","kind":"score","direction":"higher_better","scale":"unbounded","unit":"points","description":"How good."},
                                              {"id":"pass_rate","kind":"rate","direction":"higher_better","scale":{"min":0,"max":1}}],"ext":{"vendor.y":true}}
            """);
        using var output = new WriterRun();

        var (code, _, error) = Produce(scenario, output);

        Assert.True(code == 0, error);
        Assert.True(Same(output.Json("run.json"), scenario["run"]), output.Json("run.json").ToJsonString());
        Assert.True(Same(output.Json("metrics.json"), scenario["metrics"]), output.Json("metrics.json").ToJsonString());
    }

    [Fact]
    public void Produce_DerivesTrialsRollupsAndAggregation_FromTheFacts()
    {
        // A composite case in two trials, the second without the tools child: §9.2.1 derives each trial's lines, the
        // rollups' n, passed and agree per path, the counts and the decisive ids of each tree's own children.
        var scenario = Scenario();
        scenario["cases"] = JsonNode.Parse("""
            [{"caseId":"k1","path":"plan","evaluator":{"id":"c"},"state":"failed","severity":"high",
              "aggregation":{"strategy":"Min","rulePath":"threshold","decisive":["plan/steps"]},
              "children":[{"path":"plan/steps","evaluator":{"id":"s"},"state":"failed","severity":"high","component":{"weight":1,"required":true}},
                          {"path":"plan/tools","evaluator":{"id":"t"},"state":"not_measured","reason":"r","component":{"weight":1,"required":false}}],
              "trials":{"aggregation":"AllPass","trees":[
                {"path":"plan","evaluator":{"id":"c"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold"},
                 "children":[{"path":"plan/steps","evaluator":{"id":"s"},"state":"passed","component":{"weight":1,"required":true}},
                             {"path":"plan/tools","evaluator":{"id":"t"},"state":"not_measured","reason":"r","component":{"weight":1,"required":false}}]},
                {"path":"plan","evaluator":{"id":"c"},"state":"failed","severity":"high","aggregation":{"strategy":"Min","rulePath":"threshold","decisive":["plan/steps"]},
                 "children":[{"path":"plan/steps","evaluator":{"id":"s"},"state":"failed","severity":"high","component":{"weight":1,"required":true}}]}]}}]
            """);
        using var output = new WriterRun();

        var (code, _, error) = Produce(scenario, output);

        Assert.True(code == 0, error);
        var lines = output.Lines("results.ndjson").ToDictionary(l => $"{l["path"]}#{l["trial"]?.ToJsonString() ?? "-"}", StringComparer.Ordinal);
        Assert.Equal(8, lines.Count);
        string Id(string path, int? trial = null) => AefResultId.Compute("produce-unit", "k1", path, trial);

        var rollup = lines["plan#-"];
        Assert.Equal("""{"n":2,"passed":1,"aggregation":"AllPass","agree":false}""", rollup["trials"]!.ToJsonString());
        Assert.Equal("""{"strategy":"Min","rulePath":"threshold","measured":1,"total":2,"unmeasured":{"not_measured":1},"decisive":["{{0}}"]}""".Replace("{{0}}", Id("plan/steps"), StringComparison.Ordinal),
            rollup["aggregation"]!.ToJsonString());
        Assert.Equal("""{"n":2,"passed":1,"aggregation":"AllPass","agree":false}""", lines["plan/steps#-"]["trials"]!.ToJsonString());
        Assert.Equal("""{"n":1,"passed":0,"aggregation":"AllPass","agree":true}""", lines["plan/tools#-"]["trials"]!.ToJsonString());
        Assert.Equal(Id("plan"), lines["plan/tools#-"]["parentResultId"]!.GetValue<string>());

        Assert.Equal(Id("plan", 1), lines["plan/steps#1"]["parentResultId"]!.GetValue<string>());
        Assert.Equal(1, lines["plan/steps#1"]["trial"]!.GetValue<int>());
        Assert.Equal([Id("plan/steps", 1)], lines["plan#1"]["aggregation"]!["decisive"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal("""{"strategy":"Min","rulePath":"threshold","measured":1,"total":2,"unmeasured":{"not_measured":1}}""", lines["plan#0"]["aggregation"]!.ToJsonString());
        Assert.False(lines["plan#0"].ContainsKey("parentResultId"));
    }

    public static TheoryData<string, string> NotScenarios() => new()
    {
        { "a member a node does not have", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","annotator":{"kind":"CODE"}}]""" },
        { "component on a root", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","component":{"weight":1,"required":true}}]""" },
        { "caseId on a child", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold"},"children":[{"caseId":"k1","path":"q/a","evaluator":{"id":"e"},"state":"passed","component":{"weight":1,"required":true}}]}]""" },
        { "a state the schema does not list", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"great"}]""" },
        { "no trial tree", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","trials":{"aggregation":"AllPass","trees":[]}}]""" },
        { "a rollup where no trial has a line", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold"},"children":[{"path":"q/a","evaluator":{"id":"e"},"state":"passed","component":{"weight":1,"required":true}}],"trials":{"aggregation":"AllPass","trees":[{"path":"q","evaluator":{"id":"e"},"state":"passed"}]}}]""" },
        { "two siblings at one path", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold"},"children":[{"path":"q/a","evaluator":{"id":"e"},"state":"passed","component":{"weight":1,"required":true}},{"path":"q/a","evaluator":{"id":"e"},"state":"failed","component":{"weight":1,"required":true}}]}]""" },
        { "a member a component does not have", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold"},"children":[{"path":"q/a","evaluator":{"id":"e"},"state":"passed","component":{"weight":1,"required":true,"note":"x"}}]}]""" },
        { "a grandchild two levels down", """[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold"},"children":[{"path":"q/a/b","evaluator":{"id":"e"},"state":"passed","component":{"weight":1,"required":true}}]}]""" },
    };

    [Theory]
    [MemberData(nameof(NotScenarios))]
    public void Produce_RefusesAScenarioNotOfItsShape_AndWritesNothing(string what, string cases)
    {
        var scenario = Scenario();
        scenario["cases"] = JsonNode.Parse(cases);
        using var output = new WriterRun();

        var (code, _, error) = Produce(scenario, output);

        Assert.True(code == 2, what);
        Assert.False(Directory.Exists(output.Dir), $"{what}: {error}");
    }

    [Fact]
    public void Produce_WhatTheWriterRefuses_LeavesOutAsItWas()
    {
        // The writer refuses at close (a summary entry naming an undeclared metric, [SUM-1]) after it wrote the run's
        // first files: they are removed, and OUT is as it was, absent or empty.
        var scenario = Scenario();
        scenario["summary"] = JsonNode.Parse("""{"lanes":[{"lane":"quality","metrics":[{"metric":"undeclared","path":"q"}]}]}""");
        using var output = new WriterRun();

        Assert.Equal(2, Produce(scenario, output).Code);
        Assert.False(Directory.Exists(output.Dir));

        Directory.CreateDirectory(output.Dir);
        Assert.Equal(2, Produce(scenario, output).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output.Dir));

        // A reason the schema requires on a typed absence, missing: refused when the line is added.
        scenario = Scenario();
        scenario["cases"] = JsonNode.Parse("""[{"caseId":"k1","path":"q","evaluator":{"id":"e"},"state":"skipped"}]""");
        Assert.Equal(2, Produce(scenario, output).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output.Dir));

        // OUT must not hold files already, nor be a file.
        File.WriteAllText(Path.Combine(output.Dir, "keep.txt"), "x");
        Assert.Equal(2, Produce(Scenario(), output).Code);
        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(output.Dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("2026-10-01T00:01:00.000000001Z")]   // nine fraction digits: finer than a DateTimeOffset holds
    [InlineData("2026-10-01T00:01:00.123456789Z")]
    [InlineData("2026-10-01T00:01:00.0000001Z")]
    public void Produce_WritesATimeAsGiven_AtFullPrecision(string endedAt)
    {
        // n2-d: [ENC-8] allows nine fraction digits, and the writer's model holds AefTime, so a scenario's time is
        // written exactly (round 4 refused one finer than 100 ns rather than round it).
        var scenario = Scenario();
        scenario["run"]!["endedAt"] = endedAt;
        scenario["run"]!["startedAt"] = "2026-10-01T00:00:00.999999999Z";
        using var output = new WriterRun();

        var (code, _, error) = Produce(scenario, output);

        Assert.True(code == 0, error);
        Assert.Equal(endedAt, output.Json("run.json")["endedAt"]!.GetValue<string>());
        Assert.Equal("2026-10-01T00:00:00.999999999Z", output.Json("run.json")["startedAt"]!.GetValue<string>());
    }

    [Fact]
    public void Summarize_RefusesAnUndeclaredMetric_ADuplicate_AndAProducersMethodWithoutItsValue()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var request = Path.Combine(run.Root, "request.json");

        foreach (var body in new[]
        {
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "undeclared", "path": "q"}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q"}, {"metric": "m", "path": "q"}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": []}, {"lane": "a", "metrics": []}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q", "aggregate": {"method": "f1"}}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q", "verdict": "excellent"}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q", "value": 0.5}]}]}""",
            """{"lanes": "a"}""",
        })
        {
            File.WriteAllText(request, body);
            var (code, output, _) = Dispatch("summarize", run.Dir, request);
            Assert.True(code == 2, body);
            Assert.Empty(output);
        }
    }

    [Fact]
    public void Summarize_GivesWhatTheWriterWroteAtClose()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var request = Path.Combine(run.Root, "request.json");
        File.WriteAllText(request, """{"lanes": [{"lane": "main", "metrics": [{"metric": "m", "path": "q"}]}]}""");

        var (code, output, error) = Dispatch("summarize", run.Dir, request);

        Assert.True(code == 0, error);
        Assert.True(JsonNode.DeepEquals(run.Json("summary.json"), JsonNode.Parse(output)), output);
    }

    [Fact]
    public void SealWrite_RefusesASealedAtBeforeTheEnd_AtFullPrecision_AndAnOpenRun()
    {
        using var run = new WriterRun();
        run.WriteSmall();   // ended at 2026-10-01T10:05:00Z

        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "producer", "--sealed-at", "2026-10-01T10:04:59.999999999Z").Code);
        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "producer", "--sealed-at", "yesterday").Code);
        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "host", "--sealed-at", "2026-10-02T00:00:00Z").Code);
        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "producer").Code);
        Assert.False(File.Exists(run.Full("seal.json")));

        var (code, output, _) = Dispatch("seal-write", run.Dir, "--sealed-by", "producer", "--sealed-at", "2026-10-01T10:05:00.000000001Z");
        Assert.Equal(0, code);
        Assert.Equal(AefRunFolder.Open(run.Dir).ComputeRunHash(), JsonNode.Parse(output)!["runHash"]!.GetValue<string>());
        Assert.Equal("2026-10-01T10:05:00.000000001Z", run.Json("seal.json")["predicate"]!["sealedAt"]!.GetValue<string>());

        using var open = new WriterRun();
        open.Create();
        Assert.Equal(2, Dispatch("seal-write", open.Dir, "--sealed-by", "producer", "--sealed-at", "2026-10-02T00:00:00Z").Code);
        Assert.False(File.Exists(open.Full("seal.json")));
    }

    [Fact]
    public void Sign_NeedsAPayloadType_AndAPkcs8Key()
    {
        using var run = new WriterRun();
        Directory.CreateDirectory(run.Root);
        var file = Path.Combine(run.Root, "file.json");
        File.WriteAllText(file, "{}\n");
        var key = Path.Combine(run.Root, "key.pem");
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());
        }

        Assert.Equal(2, Dispatch("sign", file, key).Code);
        Assert.Equal(0, Dispatch("sign", file, key, "--payload-type", Dsse.InTotoPayloadType).Code);
        File.WriteAllText(key, "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n");
        Assert.Equal(2, Dispatch("sign", file, key, "--payload-type", Dsse.InTotoPayloadType).Code);
    }

    // ------------------------------------------------------------------ AefSummaryWriter

    [Fact]
    public void TheExactSum_SurvivesCancellation_Sum5()
    {
        var results = new[] { 1e20, 1, -1e20 }.Select((v, i) => Line($"k{i}", v)).ToList();

        var summary = AefSummaryWriter.Build("r", results, Kinds(), Request(new AefSummaryEntry { Metric = "m", Path = "q" }));

        var entry = summary["lanes"]![0]!["metrics"]![0]!;
        Assert.Equal(1.0, entry["sum"]!.GetValue<double>());   // 1e20 + 1 − 1e20 summed in order is 0
        Assert.Equal(1.0 / 3, entry["value"]!.GetValue<double>());
        Assert.Equal(2e40, entry["sumSq"]!.GetValue<double>());
    }

    [Fact]
    public void ASumOfSquaresBeyondBinary64_IsLeftOut_Sum5()
    {
        // sumSq is optional, and (1e200)² has no binary64 value: the summary is still written, without it.
        var summary = AefSummaryWriter.Build("r", [Line("k1", 1e200), Line("k2", 1)], Kinds(), Request(new AefSummaryEntry { Metric = "m", Path = "q" }));

        var entry = summary["lanes"]![0]!["metrics"]![0]!.AsObject();
        Assert.False(entry.ContainsKey("sumSq"));
        Assert.Equal(1e200, entry["sum"]!.GetValue<double>());
    }

    [Fact]
    public void AProducersMethod_WithNothingMeasured_NeedsNoValue_AndAnAefMethod_TakesNone()
    {
        var results = new List<JsonObject> { Line("k1", null, "skipped") };
        var entry = new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("f1"), Decide = _ => throw new InvalidOperationException("not called") };

        var summary = AefSummaryWriter.Build("r", results, Kinds(), Request(entry));

        Assert.Null(summary["lanes"]![0]!["metrics"]![0]!["value"]);
        Assert.Equal("not_measured", summary["lanes"]![0]!["metrics"]![0]!["verdict"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => AefSummaryWriter.Build("r", [Line("k1", 0.5)], Kinds(), Request(new AefSummaryEntry
        {
            Metric = "m", Path = "q", Aggregate = new AefAggregate("median"), Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Passed, Value: 1),
        })));
        Assert.Throws<ArgumentException>(() => AefSummaryWriter.Build("r", [], Kinds(), Request(new AefSummaryEntry { Metric = "nope", Path = "q" })));
    }

    // ------------------------------------------------------------------ AefSigningKey

    [Fact]
    public void AP256Key_InPkcs8_SignsUnderItsKeyId()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = AefSigningKey.FromPkcs8Pem(ecdsa.ExportPkcs8PrivateKeyPem());

        Assert.Equal(PublicKeyInfo.FromDer(ecdsa.ExportSubjectPublicKeyInfo()).KeyId, signer.KeyId);
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, "x"u8, signer);
        Assert.Equal(["me"], DsseVerifier.Verify(envelope, "x"u8, Dsse.InTotoPayloadType, new TrustPolicy([new TrustedKey("me", signer.PublicKey)])).VerifiesFor);
    }

    [Fact]
    public void AnEd25519Key_IsNotSupportedForSigning_AndOtherKeysAreRefused()
    {
        // PKCS#8 for Ed25519 (RFC 8410): version 0, id-Ed25519, and the 32-byte seed in an OCTET STRING.
        var ed25519 = Convert.FromHexString("302e020100300506032b657004220420" + new string('1', 64));
        Assert.Throws<NotSupportedException>(() => AefSigningKey.FromPkcs8Pem(PemEncoding.WriteString("PRIVATE KEY", ed25519)));

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem(p384.ExportPkcs8PrivateKeyPem()));
        using var rsa = RSA.Create(2048);
        Assert.Throws<NotSupportedException>(() => AefSigningKey.FromPkcs8Pem(rsa.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem("not a key"));
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem(PemEncoding.WriteString("EC PRIVATE KEY", [0x30, 0x00])));
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem(PemEncoding.WriteString("PRIVATE KEY", [0x30, 0x03, 0x02, 0x01])));
    }

    // ------------------------------------------------------------------ helpers

    private static (int Code, string Output, string Error) Dispatch(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = Driver.Dispatch(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static TheoryData<string> Names(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var folder in Directory.GetDirectories(Path.Combine(Vectors, kind)).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(folder));
        }

        return data;
    }

    private static WriterRun Copy(string source)
    {
        var copy = new WriterRun();
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy.Dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Directory.CreateDirectory(copy.Dir);
        return copy;
    }

    private static Dictionary<string, byte[]> Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    private static JsonObject Line(string caseId, double? value, string state = "scored")
    {
        var line = new JsonObject { ["caseId"] = caseId, ["path"] = "q", ["state"] = state };
        if (value is { } v)
        {
            line["scores"] = new JsonArray(new JsonObject { ["metric"] = "m", ["value"] = v });
        }

        return line;
    }

    private static Dictionary<string, AefMetricKind> Kinds() => new(StringComparer.Ordinal) { ["m"] = AefMetricKind.Score };

    // A small closed scenario (spec 09 §9.2.1): one flat case, two metrics, one summary entry.
    private static JsonObject Scenario() => JsonNode.Parse("""
        {"run":{"schemaVersion":"1.0","runId":"produce-unit","status":"completed","producer":{"name":"p","version":"1"},
                "subject":{"ref":"agent:a/b","kind":"agent","version":"v1"},"execution":{"targetMode":"live"},
                "startedAt":"2026-10-01T00:00:00Z","contentCapture":"on","endedAt":"2026-10-01T00:01:00Z"},
         "metrics":{"schemaVersion":"1.0","metrics":[{"id":"quality","kind":"score","direction":"higher_better","scale":{"min":0,"max":1}},
                                                     {"id":"pass_rate","kind":"rate","direction":"higher_better","scale":{"min":0,"max":1}}]},
         "cases":[{"caseId":"k1","path":"q","evaluator":{"id":"code:q"},"state":"passed","scores":[{"metric":"quality","value":0.9}]}],
         "summary":{"lanes":[{"lane":"quality","metrics":[{"metric":"pass_rate","path":"q"}]}]}}
        """)!.AsObject();

    // produce, the scenario written beside OUT (the run's folder, which does not exist yet unless a test made it).
    private static (int Code, string Output, string Error) Produce(JsonObject scenario, WriterRun output)
    {
        Directory.CreateDirectory(output.Root);
        var file = Path.Combine(output.Root, "scenario.json");
        File.WriteAllText(file, scenario.ToJsonString());
        return Dispatch("produce", file, output.Dir);
    }

    // Two JSON values are the same member by member, numbers under §3.6's rule, a boolean only a boolean (as the
    // conformance runner compares a produce vector's documents and lines).
    // §9.3's judge: numbers under §3.6's rule, times as times ([ENC-8]: two strings that are both AEF times compare as
    // instants, at full precision), everything else as written.
    private static bool Same(JsonNode? actual, JsonNode? expected) => (actual, expected) switch
    {
        (null, null) => true,
        (JsonObject a, JsonObject e) => a.Count == e.Count && e.All(m => a.ContainsKey(m.Key) && Same(a[m.Key], m.Value)),
        (JsonArray a, JsonArray e) => a.Count == e.Count && a.Zip(e).All(p => Same(p.First, p.Second)),
        (JsonValue a, JsonValue e) when e.GetValueKind() == System.Text.Json.JsonValueKind.Number =>
            a.GetValueKind() == System.Text.Json.JsonValueKind.Number && AefSummaryCalculator.Matches(a.GetValue<double>(), e.GetValue<double>()),
        (JsonValue a, JsonValue e) when AsTime(a) is { } at && AsTime(e) is { } et => at == et,
        (JsonValue a, JsonValue e) => JsonNode.DeepEquals(a, e),
        _ => false,
    };

    private static AefTime? AsTime(JsonValue value)
    {
        if (value.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return AefTime.Parse(value.GetValue<string>());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // §9.3: a null member and none compare equal ([ENC-2]), at any depth.
    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (name, value) in o.Where(m => m.Value is not null))
                {
                    copy[name] = WithoutNulls(value);
                }

                return copy;
            case JsonArray a:
                return new JsonArray([.. a.Select(WithoutNulls)]);
            default:
                return node?.DeepClone();
        }
    }

    // run.json as §9.3 judges it: null members dropped, and a contentCapture of on is none ([RUN-11], round 5).
    private static JsonObject RunAsWritten(JsonObject run)
    {
        var copy = (JsonObject)WithoutNulls(run)!;
        if (copy["contentCapture"] is JsonValue capture && capture.GetValueKind() == System.Text.Json.JsonValueKind.String && capture.GetValue<string>() == "on")
        {
            copy.Remove("contentCapture");
        }

        return copy;
    }

    // A result line with each absence §9.3 equates with a value written as the absence: a null parentResultId, a null
    // threshold or score, an unmeasured count of 0, an empty decisive list; decisive in any order.
    private static JsonObject AsWritten(JsonObject line)
    {
        var copy = (JsonObject)WithoutNulls(line)!;   // §9.3: a null member and none compare equal ([ENC-2])

        if (copy["aggregation"] is JsonObject aggregation)
        {
            foreach (var name in new[] { "threshold", "score" }.Where(n => aggregation.ContainsKey(n) && aggregation[n] is null))
            {
                aggregation.Remove(name);
            }

            if (aggregation["unmeasured"] is JsonObject unmeasured)
            {
                foreach (var zero in unmeasured.Where(m => AefNode.Number(m.Value) == 0).Select(m => m.Key).ToList())
                {
                    unmeasured.Remove(zero);
                }

                if (unmeasured.Count == 0)
                {
                    aggregation.Remove("unmeasured");
                }
            }

            if (aggregation["decisive"] is JsonArray decisive)
            {
                var ids = decisive.Select(d => d!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
                aggregation.Remove("decisive");
                if (ids.Count > 0)
                {
                    aggregation["decisive"] = new JsonArray([.. ids.Select(i => (JsonNode?)i)]);
                }
            }
        }

        return copy;
    }

    private static AefSummary Request(AefSummaryEntry entry) => new() { Lanes = [new AefSummaryLane("main", [entry])] };
}
