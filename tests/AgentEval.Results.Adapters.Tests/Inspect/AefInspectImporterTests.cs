// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Inspect;
using AgentEval.Results.Adapters.Tests.Otel;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Inspect;

/// <summary>
/// Inspect → AEF (contracts/aef/1/interop/inspect.md), written from the page's text: the table, the rules IN-6 to IN-9,
/// the rules settled 10-10 (R7I), the refusals (IN-6 to IN-11) and the worked example. Each checked example's
/// <c>from-inspect</c> step (interop/examples/inspect-aef and inspect-aef-edges, and the way back of aef-inspect,
/// aef-inspect-trials and aef-inspect-edges) is reproduced and compared with
/// the example's run as JSON values ([ENC-2], [ENC-4]: an AEF writer's member order and number spelling are free), except
/// the producer, which names the converter ([RUN-15]); blobs by name (their SHA-256). Each refusal of the examples is
/// refused, naming its rule.
/// </summary>
public sealed class AefInspectImporterTests : IDisposable
{
    private static readonly string Examples = Path.Combine(AefTestRuns.RepoRoot, "contracts", "aef", "1", "interop", "examples");
    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = AefTestRuns.TempPath("aef-inspect-import");

    public void Dispose() => AefTestRuns.Delete(_root);

    public static TheoryData<string, int> Steps() => new()
    {
        { "inspect-aef", 0 },
        { "inspect-aef", 1 },
        { "aef-inspect", 1 },
        { "aef-inspect-trials", 1 },
        { "aef-inspect-edges", 1 },
        { "inspect-aef-edges", 0 },
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void TheCheckedExample_IsReproduced(string name, int step)
    {
        var example = Path.Combine(Examples, name);
        var expectedJson = JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!;
        var args = expectedJson["steps"]![step]!;
        var (input, options) = Arguments(example, args["args"]!.AsArray());
        Assert.Equal("from-inspect", (string)args["args"]![0]!);
        var expectedName = (string)args["expected"]!;
        var expected = Path.Combine(example, expectedName);
        var output = Path.Combine(_root, $"{name}-{step}");

        var conversion = AefInspectImporter.Import(input, output, options);

        Assert.Equal((string)expectedJson["runs"]![expectedName]!, AefRunVerification.Name(conversion.Verification.Outcome));
        Assert.Empty(conversion.Verification.Problems);

        // results.ndjson and evidence.ndjson line by line; metrics.json and summary.json: the example's, as JSON values.
        foreach (var file in new[] { "results.ndjson", "evidence.ndjson" })
        {
            Assert.Equal(File.Exists(Path.Combine(expected, file)), File.Exists(Path.Combine(output, file)));
            if (!File.Exists(Path.Combine(expected, file)))
            {
                continue;
            }

            var want = Lines(expected, file);
            var have = Lines(output, file);
            Assert.Equal(want.Count, have.Count);
            for (var i = 0; i < want.Count; i++)
            {
                Assert.True(AefOtelExporterTests.SameJson(want[i], have[i]), $"{name} {file} line {i + 1}:\nexpected {want[i].ToJsonString()}\ngot      {have[i].ToJsonString()}");
            }
        }

        foreach (var file in new[] { "metrics.json", "summary.json" })
        {
            Assert.Equal(File.Exists(Path.Combine(expected, file)), File.Exists(Path.Combine(output, file)));
            if (File.Exists(Path.Combine(expected, file)))
            {
                Assert.True(AefOtelExporterTests.SameJson(AefTestRuns.Document(expected, file), AefTestRuns.Document(output, file)), $"{file}: {AefTestRuns.Document(output, file).ToJsonString()}");
            }
        }

        // run.json: the example's but for the producer (the converter, [RUN-15]).
        var run = AefTestRuns.Document(output, "run.json");
        Assert.Equal(AefConverter.ProducerName, (string)run["producer"]!["name"]!);
        run.Remove("producer");
        var wantRun = AefTestRuns.Document(expected, "run.json");
        wantRun.Remove("producer");
        Assert.True(AefOtelExporterTests.SameJson(wantRun, run), $"run.json: {run.ToJsonString()}");

        // The blobs, by name (the SHA-256 of their bytes).
        Assert.Equal(Blobs(expected), Blobs(output));

        // The seal: ingest, at the conversion time, closedAt the run's end; a running run is not sealed ([SEAL-1]).
        Assert.Equal(File.Exists(Path.Combine(expected, "seal.json")), File.Exists(Path.Combine(output, "seal.json")));
        if (File.Exists(Path.Combine(expected, "seal.json")))
        {
            var seal = AefTestRuns.Document(output, "seal.json")["predicate"]!;
            var wantSeal = AefTestRuns.Document(expected, "seal.json")["predicate"]!;
            Assert.Equal("ingest", (string)seal["sealedBy"]!);
            foreach (var time in new[] { "sealedAt", "closedAt" })
            {
                Assert.Equal(AefTime.Parse((string)wantSeal[time]!), AefTime.Parse((string)seal[time]!));
            }
        }
    }

    [Fact]
    public void ThePagesWorkedExample_ImportsAsItsLastBlock()
    {
        // The page's last json block is the second line of examples/aef-inspect/run/results.ndjson: triage/policy read back.
        var example = Path.Combine(Examples, "aef-inspect");
        var (input, options) = Arguments(example, JsonNode.Parse(File.ReadAllText(Path.Combine(example, "expected.json")))!["steps"]![1]!["args"]!.AsArray());
        var output = Path.Combine(_root, "worked");

        AefInspectImporter.Import(input, output, options);

        var page = File.ReadAllText(Path.Combine(Examples, "..", "inspect.md"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var section = page[page.IndexOf("\n## Worked example\n", StringComparison.Ordinal)..];
        var last = section[(section.LastIndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length)..];
        last = last[..last.IndexOf("\n```", StringComparison.Ordinal)];
        Assert.True(AefOtelExporterTests.SameJson(JsonNode.Parse(last), AefTestRuns.Results(output)[1]), AefTestRuns.Results(output)[1].ToJsonString());
    }

    public static TheoryData<string, int> Refusals()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "inspect-aef", "inspect-aef-edges" })
        {
            var refusals = JsonNode.Parse(File.ReadAllText(Path.Combine(Examples, name, "expected.json")))!["refusals"]!.AsArray();
            for (var i = 0; i < refusals.Count; i++)
            {
                if ((string?)refusals[i]!["args"]![0] == "from-inspect")
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
        var output = Path.Combine(_root, $"refused-{name}-{index}");

        var refused = Assert.Throws<AefInspectImportException>(() => AefInspectImporter.Import(input, output, options));

        Assert.Contains((string)refusal["says"]!, refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    // ------------------------------------------------------------------ IN-6: the run header

    [Fact]
    public void TheRunId_IsEvalId_OrRunIdWhenEvalIdIsEmpty_AndCreatedStartsALogWithoutStartedAt()
    {
        var (output, _) = Import(log =>
        {
            log["eval"]!["eval_id"] = "";
            log["stats"]!.AsObject().Remove("started_at");
        });

        var run = AefTestRuns.Document(output, "run.json");
        Assert.Equal(("r1", "2026-10-06T10:00:00Z"), ((string)run["runId"]!, (string)run["startedAt"]!));
        Assert.Contains("startedAt", run["imported"]!["asserted"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("inspect_ai 0.3.277", (string)run["imported"]!["from"]!);
    }

    [Fact]
    public void ACancelledLog_IsAnAbortedRun_AndAnErrorLog_HasItsMessageAsTheReason()
    {
        var (cancelled, _) = Import(log => log["status"] = "cancelled");
        Assert.Equal(("aborted", "cancelled"), ((string)AefTestRuns.Document(cancelled, "run.json")["status"]!, (string)AefTestRuns.Document(cancelled, "run.json")["abortReason"]!));

        var (error, _) = Import(log =>
        {
            log["status"] = "error";
            log["error"] = new JsonObject { ["message"] = "RuntimeError: boom", ["traceback"] = "", ["traceback_ansi"] = "" };
        });
        Assert.Equal("RuntimeError: boom", (string)AefTestRuns.Document(error, "run.json")["abortReason"]!);
    }

    [Theory]
    [InlineData("eval-id", "IN-6")]
    [InlineData("no-end", "IN-6")]
    [InlineData("end-before-start", "IN-6")]
    [InlineData("error-without-message", "IN-6")]
    public void TheHeader_IsRefused_WhenThePageRefusesIt(string what, string rule)
    {
        Action<JsonObject> edit = what switch
        {
            "eval-id" => log => log["eval"]!["eval_id"] = "not an id!",
            "no-end" => log => log["stats"]!.AsObject().Remove("completed_at"),
            "end-before-start" => log => log["stats"]!["completed_at"] = "2026-10-06T11:59:00+02:00",
            _ => log =>
            {
                log["status"] = "error";
                log["error"] = new JsonObject { ["message"] = "", ["traceback"] = "" };
            },
        };

        AssertRefused(edit, rule);
    }

    // ------------------------------------------------------------------ IN-7: scores

    [Fact]
    public void AScore_IsReadAsItsRowSays()
    {
        var (output, _) = Import(log => log["samples"]![0]!["scores"] = new JsonObject
        {
            ["s"] = new JsonObject { ["value"] = "C" },
            ["number"] = new JsonObject { ["value"] = 0.7, ["reason"] = "refusal" },             // blames the model: failed
            ["scoring"] = new JsonObject { ["value"] = InspectJson.NaN(), ["reason"] = "scoring_failed" },
            ["blamed"] = new JsonObject { ["value"] = InspectJson.NaN(), ["reason"] = "no_response" },
            ["absent"] = new JsonObject { ["value"] = InspectJson.NaN(), ["reason"] = "not_applicable" },
            ["list"] = new JsonObject { ["value"] = new JsonArray(1, 2) },
            ["map"] = new JsonObject { ["value"] = new JsonObject { ["grade"] = "P", ["steps"] = 2 } },
        });

        var lines = AefTestRuns.Results(output).ToDictionary(l => (string)l["path"]!);
        Assert.Equal(("passed", "C"), ((string)lines["s"]["state"]!, (string)lines["s"]["scores"]![0]!["label"]!));
        Assert.Equal(("failed", "refusal", 0.7), ((string)lines["number"]["state"]!, (string)lines["number"]["reason"]!, (double)lines["number"]["scores"]![0]!["value"]!));
        Assert.Equal(("error", "scoring_failed"), ((string)lines["scoring"]["state"]!, (string)lines["scoring"]["reason"]!));
        Assert.Equal(("failed", "no_response"), ((string)lines["blamed"]["state"]!, (string)lines["blamed"]["reason"]!));
        Assert.Equal(("not_measured", "not_applicable"), ((string)lines["absent"]["state"]!, (string)lines["absent"]["reason"]!));
        Assert.Equal(("scored", "[1,2]"), ((string)lines["list"]["state"]!, lines["list"]["ext"]!["inspect_ai"]!["value"]!.ToJsonString()));
        Assert.False(lines["list"].ContainsKey("scores"));
        Assert.Equal("""[{"metric":"grade","value":0.5,"label":"P"},{"metric":"steps","value":2}]""", lines["map"]["scores"]!.ToJsonString());
        Assert.Equal("scored", (string)lines["map"]["state"]!);
    }

    [Fact]
    public void WithContentCaptureOff_NoExplanationAnswerOrContentIsKept()
    {
        var (output, _) = Import(log => log["samples"]![0]!["scores"] = new JsonObject
        {
            ["s"] = new JsonObject { ["value"] = "C", ["answer"] = "Yes.", ["explanation"] = "Right.", ["metadata"] = new JsonObject { ["k"] = 1 } },
        }, AefContentCapture.Off);

        var line = Assert.Single(AefTestRuns.Results(output));
        Assert.False(line.ContainsKey("reasoning"));
        Assert.Equal("""{"inspect_ai":{"metadata":{"k":1}}}""", line["ext"]!.ToJsonString());
        Assert.False(File.Exists(Path.Combine(output, "evidence.ndjson")));
        Assert.False(Directory.Exists(Path.Combine(output, "blobs")));
    }

    [Theory]
    [InlineData("\"B\"")]
    [InlineData("true")]
    [InlineData("{\"grade\":\"B\"}")]
    [InlineData("{\"ok\":false}")]
    public void ABooleanOrAStringOtherThanALetter_IsRefused(string value) =>
        AssertRefused(log => log["samples"]![0]!["scores"]!["s"]!["value"] = JsonNode.Parse(value), "IN-7");

    // ------------------------------------------------------------------ IN-8: samples and epochs

    [Theory]
    [InlineData("two-reducers")]
    [InlineData("epoch-beyond")]
    [InlineData("no-scores")]
    [InlineData("stopped-without-scorers")]
    [InlineData("two-scores")]
    [InlineData("reduction-without-lines")]
    [InlineData("bad-limit")]
    [InlineData("limit-not-a-number")]
    [InlineData("reducer-without-value-in-a-reduction")]
    public void TheSamples_AreRefused_WhenThePageRefusesThem(string what)
    {
        Action<JsonObject> edit = what switch
        {
            "bad-limit" => log =>
            {
                log["samples"]![0]!["scores"] = null;
                log["samples"]![0]!["limit"] = new JsonObject { ["type"] = "token" };
            },
            "limit-not-a-number" => log =>
            {
                log["samples"]![0]!["scores"] = null;
                log["samples"]![0]!["limit"] = new JsonObject { ["type"] = "token", ["limit"] = "1000" };
            },
            "reducer-without-value-in-a-reduction" => log =>
            {
                // With more than one epoch, a reduction whose reducer has no AEF value (R7I-14).
                log["eval"]!["config"] = new JsonObject { ["epochs"] = 2, ["epochs_reducer"] = new JsonArray("collect") };
                var second = log["samples"]![0]!.DeepClone();
                second["epoch"] = 2;
                log["samples"]!.AsArray().Add(second);
                log["reductions"] = new JsonArray(new JsonObject
                {
                    ["scorer"] = "s",
                    ["reducer"] = "collect",
                    ["samples"] = new JsonArray(new JsonObject { ["value"] = "C", ["sample_id"] = 1 }),
                });
            },
            "two-reducers" => log => log["eval"]!["config"] = new JsonObject { ["epochs"] = 2, ["epochs_reducer"] = new JsonArray("mean", "max") },
            "epoch-beyond" => log => log["samples"]![0]!["epoch"] = 2,
            "no-scores" => log => log["samples"]![0]!["scores"] = new JsonObject(),
            "stopped-without-scorers" => log =>
            {
                log["eval"]!.AsObject().Remove("scorers");
                log["samples"]![0]!["scores"] = null;
                log["samples"]![0]!["error"] = new JsonObject { ["message"] = "crashed" };
            },
            "two-scores" => log => log["samples"]!.AsArray().Add(log["samples"]![0]!.DeepClone()),
            _ => log =>
            {
                log["eval"]!["config"] = new JsonObject { ["epochs"] = 2, ["epochs_reducer"] = new JsonArray("mean") };
                log["reductions"] = new JsonArray(new JsonObject
                {
                    ["scorer"] = "s",
                    ["reducer"] = "mean",
                    ["samples"] = new JsonArray(new JsonObject { ["value"] = 1, ["sample_id"] = "elsewhere" }),
                });
            },
        };

        AssertRefused(edit, "IN-8");
    }

    [Fact]
    public void Epochs_AreTrials_AReductionARollupCountedFromThem_AndTheReducerTheAggregation()
    {
        var (output, _) = Import(log =>
        {
            log["eval"]!["config"] = new JsonObject { ["epochs"] = 2, ["epochs_reducer"] = new JsonArray("at_least_2") };
            var second = log["samples"]![0]!.DeepClone();
            second["epoch"] = 2;
            second["scores"]!["s"]!["value"] = "I";
            log["samples"]!.AsArray().Add(second);
            log["reductions"] = new JsonArray(new JsonObject
            {
                ["scorer"] = "s",
                ["reducer"] = "pass_at_2",
                ["samples"] = new JsonArray(new JsonObject { ["value"] = 1.0, ["sample_id"] = 1 }),
            });
            log["results"]!["scores"]![0]!["metrics"] = new JsonObject { ["accuracy"] = new JsonObject { ["name"] = "accuracy", ["value"] = 1.0 } };
        });

        var run = AefTestRuns.Document(output, "run.json");
        Assert.Equal("""{"trialsPerCase":2,"aggregation":"AllPass"}""", run["suite"]!["executionPolicy"]!.ToJsonString());
        var lines = AefTestRuns.Results(output);
        Assert.Equal([0, 1], lines.Take(2).Select(l => (int)l["trial"]!));
        Assert.Equal("""{"n":2,"passed":1,"aggregation":"PassAtK","agree":false,"k":2}""", lines[2]["trials"]!.ToJsonString());
    }

    [Fact]
    public void ASampleThatStopped_IsALinePerScorer_InErrorOrNotMeasuredForALimit()
    {
        var (output, _) = Import(log =>
        {
            log["samples"]![0]!["scores"] = null;
            log["samples"]![0]!["limit"] = new JsonObject { ["type"] = "token", ["limit"] = 1000 };
            log["eval"]!["scorers"] = new JsonArray(new JsonObject { ["name"] = "s" }, new JsonObject { ["name"] = "t" });
            log["results"]!["scores"]![0]!["metrics"] = new JsonObject { ["accuracy"] = new JsonObject { ["name"] = "accuracy", ["value"] = InspectJson.NaN() } };
        });

        var lines = AefTestRuns.Results(output);
        Assert.Equal(["s", "t"], lines.Select(l => (string)l["path"]!));
        Assert.All(lines, l => Assert.Equal(("not_measured", "token limit 1000"), ((string)l["state"]!, (string)l["reason"]!)));
        Assert.Equal("match", (string)lines[0]["evaluator"]!["id"]!);
    }

    [Fact]
    public void AStartedLog_IsARunningRun_NotSealed()
    {
        var (output, conversion) = Import(log =>
        {
            log["status"] = "started";
            log["stats"]!.AsObject().Remove("completed_at");
        });

        Assert.Equal(AefOutcome.Unsealed, conversion.Verification.Outcome);
        Assert.Null(conversion.Seal);
        Assert.Equal("running", (string)AefTestRuns.Document(output, "run.json")["status"]!);
        Assert.False(File.Exists(Path.Combine(output, "summary.json")));
    }

    [Fact]
    public void ContentThatIsNotText_IsSerializedByJcs_SoAnySpellingOfTheSameValuesGivesOneBlob()
    {
        // RFC 8785: no whitespace, members sorted by UTF-16 code units (😀, U+D83D U+DE00, before ｚ, U+FF5A), numbers as
        // Number::toString writes them, only ", \ and control characters escaped.
        var one = InspectJson.Parse(Encoding.UTF8.GetBytes("{\"ｚ\": 2, \"b\": 1.0, \"😀\": [2.50, \"—\\n\"], \"a\": {\"y\": 1e21, \"x\": null}}"));
        var two = InspectJson.Parse(Encoding.UTF8.GetBytes("{\"a\":{\"x\":null,\"y\":1E+21},\"b\":1,\"😀\":[2.5,\"\\u2014\\u000a\"],\"ｚ\":2.0}"));

        var bytes = InspectJson.Canonical(one);

        Assert.Equal("{\"a\":{\"x\":null,\"y\":1e+21},\"b\":1,\"😀\":[2.5,\"—\\n\"],\"ｚ\":2}", Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes, InspectJson.Canonical(two));
    }

    [Fact]
    public void ContentHoldingNaN_IsRefusedByIn8_AndAnUnpairedSurrogateAsTheLogIsRead()
    {
        // JCS has no NaN (IN-8; content-nan.json in the example too). An unpaired surrogate never reaches it: the log is
        // refused as it is read (IN-6, R9-2).
        var (nan, nanOutput) = Files(log => log["samples"]![0]!["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["n"] = InspectJson.NaN() }));
        var (lone, loneOutput) = Files(log => log["samples"]![0]!["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "LONE" }));
        File.WriteAllText(lone, File.ReadAllText(lone).Replace("\"LONE\"", "\"\\ud800\"", StringComparison.Ordinal), new UTF8Encoding(false));
        var options = new AefInspectImportOptions { TargetMode = AefTargetMode.Live, TimeProvider = new Clock(At) };

        Assert.Contains("(IN-8)", Assert.Throws<AefInspectImportException>(() => AefInspectImporter.Import(nan, nanOutput, options)).Message, StringComparison.Ordinal);
        var refused = Assert.Throws<AefInspectImportException>(() => AefInspectImporter.Import(lone, loneOutput, options));
        Assert.Contains("unpaired surrogate", refused.Message, StringComparison.Ordinal);
        Assert.Contains("(IN-6)", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(nanOutput));
        Assert.False(Directory.Exists(loneOutput));

        // With contentCapture off the content is not written, so its NaN is not refused.
        var conversion = AefInspectImporter.Import(nan, nanOutput, options with { ContentCapture = AefContentCapture.Off });
        Assert.NotEqual(AefOutcome.Invalid, conversion.Verification.Outcome);
    }

    // ------------------------------------------------------------------ R9-2: reading the log, ids, errors and limits

    [Theory]
    [InlineData("id", "1.5", "IN-8")]                                   // z17
    [InlineData("id", "12345678901234567890", "IN-8")]                  // z19
    [InlineData("id", "9007199254740993", "IN-8")]                      // z19c: reads as 2^53, beyond 2^53 − 1
    [InlineData("task_version", "12345678901234567890", "IN-6")]        // z21
    [InlineData("task_version", "2.5", "IN-6")]
    [InlineData("error", "\"boom\"", "IN-8")]                           // z22: an error that is not an object
    [InlineData("error", "{\"message\": 3}", "IN-8")]                   // a message that is not text
    [InlineData("limit", "{}", "IN-8")]                                 // on a scored sample too
    [InlineData("limit-beside-error", "{}", "IN-8")]                    // beside an error too
    [InlineData("limit-number", "1e400", "IN-6")]                       // z1: overflows binary64, refused as read
    [InlineData("content", "1e400", "IN-6")]                            // z10
    [InlineData("score", "1e400", "IN-6")]                              // z15
    [InlineData("total_time", "1e400", "IN-6")]                         // z16
    [InlineData("content-key", "\"\\udc00\"", "IN-6")]                  // z12: an unpaired surrogate, a name
    [InlineData("score-metadata-key", "\"\\udc00\"", "IN-6")]           // z12b
    [InlineData("eval-metadata-key", "\"\\udc00\"", "IN-6")]            // z12c: in a part not carried
    [InlineData("explanation", "\"a\\ud800\"", "IN-6")]                 // z12d: a value
    [InlineData("deep", "600", "IN-6")]                                 // z14: deeper than 64
    [InlineData("deep", "200", "IN-6")]                                 // z14b
    [InlineData("eval-metadata", "NaN", "IN-6")]                        // NaN where no unscored value can be
    [InlineData("score-metadata", "NaN", "IN-6")]
    [InlineData("eval-metadata", "-Infinity", "IN-6")]
    [InlineData("score-metadata", "Infinity", "IN-6")]
    [InlineData("content-key", "NaN", "IN-6")]                          // R10-3: a bare NaN as a member name is no JSON
    [InlineData("content-key-off", "NaN", "IN-6")]                      // n04: in content not written too
    [InlineData("score-metadata-key", "NaN", "IN-6")]
    [InlineData("score-marker-string", "\"\\u0000NaN\\u0000\"", "IN-7")]   // n01: a string, never a NaN
    [InlineData("list-score", "[1, NaN]", "IN-6")]                      // n05: a NaN in a list is no unscored value
    [InlineData("epochs", "1.5", "IN-6")]                               // "Integers": epochs 1 to 1000
    [InlineData("epochs", "0", "IN-6")]
    [InlineData("epochs", "1001", "IN-6")]
    [InlineData("epoch", "0", "IN-8")]                                  // an epoch 1 to epochs
    [InlineData("epoch", "1.5", "IN-8")]
    [InlineData("sample-tokens", "-1", "IN-8")]                         // a sample's tokens, at least 0
    [InlineData("sample-tokens", "7.5", "IN-8")]
    [InlineData("run-tokens", "-1", "IN-9")]                            // the run's tokens
    [InlineData("run-tokens", "1e16", "IN-9")]                          // beyond 2^53 − 1
    [InlineData("k", "1.5", "IN-9")]                                    // k, at least 1
    [InlineData("k", "0", "IN-9")]
    [InlineData("twice", "\"\\u0073\"", "IN-6")]                        // a member named twice, once escaped
    public void AMalformedLog_IsRefused_NamingItsRule_AndNeverThrows(string where, string raw, string rule)
    {
        var (input, output) = Files(log =>
        {
            var sample = log["samples"]![0]!;
            switch (where)
            {
                case "id": sample["id"] = "RAW"; break;
                case "task_version": log["eval"]!["task_version"] = "RAW"; break;
                case "error": sample["error"] = "RAW"; break;
                case "limit": sample["limit"] = "RAW"; break;
                case "limit-beside-error":
                    sample["error"] = new JsonObject { ["message"] = "crashed" };
                    sample["limit"] = "RAW";
                    break;
                case "limit-number":
                    sample["scores"] = null;
                    sample["limit"] = new JsonObject { ["type"] = "token", ["limit"] = "RAW" };
                    break;
                case "content": sample["output"] = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["n"] = "RAW" }) }; break;
                case "score": sample["scores"]!["s"]!["value"] = "RAW"; break;
                case "total_time": sample["total_time"] = "RAW"; break;
                case "content-key" or "content-key-off": sample["output"] = new JsonObject { ["choices"] = new JsonArray(), ["metadata"] = new JsonObject { ["RAWKEY"] = 1 } }; break;
                case "score-marker-string": sample["scores"]!["s"]!["value"] = "RAW"; break;
                case "list-score": sample["scores"]!["s"]!["value"] = "RAW"; break;
                case "epochs": log["eval"]!["config"]!["epochs"] = "RAW"; break;
                case "epoch": sample["epoch"] = "RAW"; break;
                case "sample-tokens": sample["role_usage"] = new JsonObject { ["agent"] = new JsonObject { ["input_tokens"] = "RAW" } }; break;
                case "run-tokens": log["stats"]!["model_usage"] = new JsonObject { ["openai/m"] = new JsonObject { ["input_tokens"] = "RAW" } }; break;
                case "k":
                    log["results"]!["scores"]![0]!["metrics"] = new JsonObject
                    {
                        ["pass_at_k"] = new JsonObject { ["name"] = "pass_at_k", ["value"] = 0.5, ["params"] = new JsonObject { ["k"] = "RAW" } },
                    };
                    break;
                case "score-metadata-key": sample["scores"]!["s"]!["metadata"] = new JsonObject { ["RAWKEY"] = 1 }; break;
                case "eval-metadata-key": log["eval"]!["metadata"] = new JsonObject { ["RAWKEY"] = 1 }; break;
                case "explanation": sample["scores"]!["s"]!["explanation"] = "RAW"; break;
                case "deep": sample["messages"] = "RAW"; break;
                case "eval-metadata": log["eval"]!["metadata"] = new JsonObject { ["x"] = "RAW" }; break;
                case "score-metadata": sample["scores"]!["s"]!["metadata"] = new JsonObject { ["x"] = "RAW" }; break;
                case "twice": sample["scores"]!["RAWKEY"] = new JsonObject { ["value"] = 1 }; break;
            }
        });
        var text = where == "deep"
            ? new string('[', int.Parse(raw, CultureInfo.InvariantCulture)) + new string(']', int.Parse(raw, CultureInfo.InvariantCulture))
            : raw;
        File.WriteAllText(input, File.ReadAllText(input).Replace("\"RAWKEY\"", text, StringComparison.Ordinal).Replace("\"RAW\"", text, StringComparison.Ordinal), new UTF8Encoding(false));

        var capture = where.EndsWith("-off", StringComparison.Ordinal) ? AefContentCapture.Off : AefContentCapture.On;
        var refused = Assert.Throws<AefInspectImportException>(() => AefInspectImporter.Import(input, output, new AefInspectImportOptions { TargetMode = AefTargetMode.Live, ContentCapture = capture, TimeProvider = new Clock(At) }));

        Assert.Contains($"({rule})", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("epochs", "1.0")]
    [InlineData("epoch", "1.0")]
    [InlineData("sample-tokens", "7.0")]
    [InlineData("run-tokens", "100.0")]
    [InlineData("k", "2.0")]
    public void AnInteger_IsReadByItsValueAlone_HoweverItIsWritten(string where, string raw)
    {
        // "Integers" (IN-6, IN-8, IN-9): 7, 7.0 and 7e0 are one integer.
        var (input, output) = Files(log =>
        {
            var sample = log["samples"]![0]!;
            switch (where)
            {
                case "epochs": log["eval"]!["config"]!["epochs"] = "RAW"; break;
                case "epoch": sample["epoch"] = "RAW"; break;
                case "sample-tokens": sample["role_usage"] = new JsonObject { ["agent"] = new JsonObject { ["input_tokens"] = "RAW" } }; break;
                case "run-tokens": log["stats"]!["model_usage"] = new JsonObject { ["openai/m"] = new JsonObject { ["input_tokens"] = "RAW" } }; break;
                default:
                    log["results"]!["scores"]![0]!["metrics"] = new JsonObject
                    {
                        ["pass_at_k"] = new JsonObject { ["name"] = "pass_at_k", ["value"] = 0.5, ["params"] = new JsonObject { ["k"] = "RAW" } },
                    };
                    break;
            }
        });
        File.WriteAllText(input, File.ReadAllText(input).Replace("\"RAW\"", raw, StringComparison.Ordinal), new UTF8Encoding(false));

        AefInspectImporter.Import(input, output, new AefInspectImportOptions { TargetMode = AefTargetMode.Live, TimeProvider = new Clock(At) });

        var run = AefTestRuns.Document(output, "run.json");
        var line = AefTestRuns.Results(output)[0];
        var summary = AefTestRuns.Document(output, "summary.json");
        Assert.Equal(where switch
        {
            "epochs" => "1",
            "epoch" => "1",
            "sample-tokens" => "7",
            "run-tokens" => "100",
            _ => "2",
        }, where switch
        {
            "epochs" => run["suite"]!["executionPolicy"]!["trialsPerCase"]!.ToJsonString(),
            "epoch" => (AefTestRuns.Results(output).Count).ToString(CultureInfo.InvariantCulture),
            "sample-tokens" => line["usage"]![0]!["gen_ai.usage.input_tokens"]!.ToJsonString(),
            "run-tokens" => summary["usage"]![0]!["gen_ai.usage.input_tokens"]!.ToJsonString(),
            _ => summary["lanes"]![0]!["metrics"]![0]!["aggregate"]!["k"]!.ToJsonString(),
        });
    }

    [Theory]
    [InlineData("eval-metadata")]          // n02
    [InlineData("explanation")]            // n39
    [InlineData("model")]                  // n41
    [InlineData("role-model")]             // n42
    [InlineData("limit-type")]             // n46
    [InlineData("content")]                // n03 (with the infinity's spelling)
    public void TheStringThatOnceStoodForNaN_IsAStringLikeAnyOther(string where)
    {
        // R10-3: a NaN is the bare token alone, a node no string of a log can imitate; "\u0000NaN\u0000" is text.
        const string marker = "\u0000NaN\u0000";
        var (output, conversion) = Import(log =>
        {
            var sample = log["samples"]![0]!;
            switch (where)
            {
                case "eval-metadata": log["eval"]!["metadata"] = new JsonObject { ["x"] = marker }; break;
                case "explanation": sample["scores"]!["s"]!["explanation"] = marker; break;
                case "model": log["eval"]!["model"] = marker; break;
                case "role-model": log["eval"]!["model_roles"] = new JsonObject { ["grader"] = new JsonObject { ["model"] = marker } }; break;
                case "limit-type":
                    sample["scores"] = null;
                    sample["limit"] = new JsonObject { ["type"] = marker, ["limit"] = 1000 };
                    log["results"]!["scores"]![0]!["metrics"] = new JsonObject();
                    break;
                default: sample["output"] = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["text"] = "\u0000Infinity\u0000" }) }; break;
            }
        });

        Assert.NotEqual(AefOutcome.Invalid, conversion.Verification.Outcome);
        Assert.Single(AefTestRuns.Results(output));
    }

    [Theory]
    [InlineData("id", "1.0", "1")]                                      // z18: ids are read as binary64 values
    [InlineData("id", "1e3", "1000")]                                   // z19b
    [InlineData("id", "9007199254740991", "9007199254740991")]          // 2^53 − 1
    [InlineData("task_version", "2.0", "2")]                            // z20
    [InlineData("error", "{}", "Inspect recorded an error without a message")]   // z25b
    [InlineData("error", "{\"message\": null}", "Inspect recorded an error without a message")]
    public void ANumericIdOrTaskVersion_IsItsIntegerInDecimalDigits_AndAnErrorWithoutAMessageSaysSo(string where, string raw, string expected)
    {
        var (input, output) = Files(log =>
        {
            switch (where)
            {
                case "id": log["samples"]![0]!["id"] = "RAW"; break;
                case "task_version": log["eval"]!["task_version"] = "RAW"; break;
                default:
                    log["samples"]![0]!["error"] = "RAW";
                    log["results"]!["scores"]![0]!["metrics"] = new JsonObject();   // nothing of Inspect's to compare
                    break;
            }
        });
        File.WriteAllText(input, File.ReadAllText(input).Replace("\"RAW\"", raw, StringComparison.Ordinal), new UTF8Encoding(false));

        AefInspectImporter.Import(input, output, new AefInspectImportOptions { TargetMode = AefTargetMode.Live, TimeProvider = new Clock(At) });

        var line = AefTestRuns.Results(output)[0];
        Assert.Equal(expected, where switch
        {
            "id" => (string)line["caseId"]!,
            "task_version" => (string)AefTestRuns.Document(output, "run.json")["suite"]!["version"]!,
            _ => (string)line["reason"]!,
        });
    }

    // ------------------------------------------------------------------ usage

    [Fact]
    public void Usage_IsOneEntryPerRole_WithItsModel_AndTheRunsTotalsOnePerModel()
    {
        var (output, _) = Import(log =>
        {
            log["eval"]!["model_roles"] = new JsonObject { ["grader"] = new JsonObject { ["model"] = "openai/g" } };
            log["samples"]![0]!["role_usage"] = new JsonObject
            {
                ["grader"] = new JsonObject { ["input_tokens"] = 4, ["output_tokens"] = 1, ["total_tokens"] = 5 },
                ["critic"] = new JsonObject { ["input_tokens"] = 2, ["output_tokens"] = 1, ["total_tokens"] = 3 },
            };
            log["samples"]![0]!["model_usage"] = new JsonObject
            {
                ["openai/m"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5, ["total_tokens"] = 15, ["total_cost"] = 0.25 },
                ["openai/g"] = new JsonObject { ["input_tokens"] = 4, ["output_tokens"] = 1, ["total_tokens"] = 5 },
            };
            log["stats"]!["model_usage"] = log["samples"]![0]!["model_usage"]!.DeepClone();
        });

        var usage = AefTestRuns.Results(output)[0]["usage"]!.AsArray();
        Assert.Equal(
            ["judge openai/g", "other ", "agent openai/m"],
            usage.Select(u => $"{(string)u!["role"]!} {(string?)u["model"]}"));
        var summary = AefTestRuns.Document(output, "summary.json");
        Assert.Equal(["agent openai/m", "judge openai/g"], summary["usage"]!.AsArray().Select(u => $"{(string)u!["role"]!} {(string?)u["model"]}"));
        Assert.Equal(0.25, (double)summary["cost"]!["totalUsd"]!);
    }

    // ------------------------------------------------------------------ IN-9: the summary

    [Fact]
    public void AnEntryWithoutAMean_IsItsOneOtherMetricAsTheAggregate_RecomputedWhenAefDefinesIt()
    {
        var (output, _) = Import(log =>
        {
            log["samples"]![0]!["scores"]!["s"]!["value"] = 0.25;
            log["results"]!["scores"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "u",
                ["scorer"] = "u",
                ["metrics"] = new JsonObject { ["pass_at_k"] = new JsonObject { ["name"] = "pass_at_k", ["value"] = 0.5, ["params"] = new JsonObject { ["k"] = 3 } } },
            });
            log["samples"]![0]!["scores"]!["u"] = new JsonObject { ["value"] = 1 };
            log["results"]!["scores"]![0]!["metrics"] = new JsonObject { ["max"] = new JsonObject { ["name"] = "max", ["value"] = 0.25 } };
        });

        var entries = AefTestRuns.Document(output, "summary.json")["lanes"]![0]!["metrics"]!.AsArray();
        Assert.Equal(("""{"method":"max"}""", 0.25), (entries[0]!["aggregate"]!.ToJsonString(), (double)entries[0]!["value"]!));
        Assert.Equal(("""{"method":"pass_at_k","k":3}""", 0.5), (entries[1]!["aggregate"]!.ToJsonString(), (double)entries[1]!["value"]!));
    }

    [Theory]
    [InlineData("""{"max":{"name":"max","value":0.9}}""")]                                            // the lines give 1
    [InlineData("""{"max":{"name":"max","value":1},"median":{"name":"median","value":1}}""")]        // no mean, two others
    [InlineData("""{"Bad Name":{"name":"Bad Name","value":1}}""")]                                   // cannot be a method
    public void ASummaryEntry_IsRefused_WhenThePageRefusesIt(string metrics) =>
        AssertRefused(log => log["results"]!["scores"]![0]!["metrics"] = JsonNode.Parse(metrics), "IN-9");

    // ------------------------------------------------------------------ IN-10

    [Fact]
    public void AnInvalidation_IsRefused() =>
        AssertRefused(log => log["samples"]![0]!["invalidation"] = new JsonObject { ["reason"] = "leaked" }, "IN-10");

    // ------------------------------------------------------------------ helpers

    // The step's arguments as the reference converter takes them: input, {out}, then --target-mode, --content-capture and
    // --at (the conversion time).
    private static (string Input, AefInspectImportOptions Options) Arguments(string example, JsonArray args)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i + 1 < args.Count; i += 2)
        {
            named[(string)args[i]!] = (string)args[i + 1]!;
        }

        return (Path.Combine(example, (string)args[1]!), new AefInspectImportOptions
        {
            TargetMode = AefNames.TryParse<AefTargetMode>(named["--target-mode"], out var mode) ? mode.Value : throw new FormatException(named["--target-mode"]),
            ContentCapture = named.TryGetValue("--content-capture", out var text)
                ? AefNames.TryParse<AefContentCapture>(text, out var capture) ? capture.Value : throw new FormatException(text)
                : AefContentCapture.On,
            TimeProvider = new Clock(DateTimeOffset.Parse(named["--at"], CultureInfo.InvariantCulture)),
        });
    }

    // A small closed log: one sample of one case, one scorer s (scorer match) with its accuracy.
    private static JsonObject BaseLog() => new()
    {
        ["version"] = 2,
        ["status"] = "success",
        ["eval"] = new JsonObject
        {
            ["eval_id"] = "e1",
            ["run_id"] = "r1",
            ["created"] = "2026-10-06T12:00:00+02:00",
            ["task"] = "refunds",
            ["task_version"] = 1,
            ["model"] = "openai/m",
            ["config"] = new JsonObject { ["epochs"] = 1 },
            ["packages"] = new JsonObject { ["inspect_ai"] = "0.3.277" },
            ["scorers"] = new JsonArray(new JsonObject { ["name"] = "s" }),
        },
        ["results"] = new JsonObject
        {
            ["total_samples"] = 1,
            ["completed_samples"] = 1,
            ["scores"] = new JsonArray(new JsonObject
            {
                ["name"] = "s",
                ["scorer"] = "match",
                ["metrics"] = new JsonObject { ["accuracy"] = new JsonObject { ["name"] = "accuracy", ["value"] = 1.0 } },
            }),
        },
        ["stats"] = new JsonObject { ["started_at"] = "2026-10-06T12:00:01+02:00", ["completed_at"] = "2026-10-06T12:01:00+02:00" },
        ["samples"] = new JsonArray(new JsonObject
        {
            ["id"] = 1,
            ["epoch"] = 1,
            ["input"] = "Refund?",
            ["target"] = "Yes.",
            ["scores"] = new JsonObject { ["s"] = new JsonObject { ["value"] = "C" } },
        }),
    };

    private (string Output, AefConversion Conversion) Import(Action<JsonObject> edit, AefContentCapture capture = AefContentCapture.On)
    {
        var (input, output) = Files(edit);
        var conversion = AefInspectImporter.Import(input, output, new AefInspectImportOptions { TargetMode = AefTargetMode.Live, ContentCapture = capture, TimeProvider = new Clock(At) });
        Assert.NotEqual(AefOutcome.Invalid, conversion.Verification.Outcome);
        return (output, conversion);
    }

    private void AssertRefused(Action<JsonObject> edit, string rule)
    {
        var (input, output) = Files(edit);
        var refused = Assert.Throws<AefInspectImportException>(() => AefInspectImporter.Import(input, output, new AefInspectImportOptions { TargetMode = AefTargetMode.Live, TimeProvider = new Clock(At) }));
        Assert.Contains(rule, refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    private (string Input, string Output) Files(Action<JsonObject> edit)
    {
        var log = BaseLog();
        edit(log);
        Directory.CreateDirectory(_root);
        var name = Guid.NewGuid().ToString("N");
        var input = Path.Combine(_root, name + ".json");
        File.WriteAllText(input, InspectJson.Indented(log) + "\n", new UTF8Encoding(false));
        return (input, Path.Combine(_root, name));
    }

    private static List<JsonObject> Lines(string run, string file) =>
        [.. File.ReadAllLines(Path.Combine(run, file)).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];

    private static List<string> Blobs(string run) =>
        Directory.Exists(Path.Combine(run, "blobs"))
            ? [.. Directory.EnumerateFiles(Path.Combine(run, "blobs"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
