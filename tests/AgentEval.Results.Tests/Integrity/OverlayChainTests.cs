using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Integrity;

/// <summary>§4.2: each code of [OVL-5], and the verified prefix of §4.3 that the batch-level codes end and the event-level codes do not.</summary>
public class OverlayChainTests
{
    private static readonly JsonObject RunTarget = new();

    [Fact]
    public void AnIntactChain_HasNoProblems_AndEveryEventIsVerified()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1"), Annotate("ov_2")]);
        run.AppendBatch([Annotate("ov_3")]);

        var chain = run.Chain();

        Assert.Empty(chain.Problems);
        Assert.Equal(run.ReadBytes("overlays/events.ndjson").Length, chain.VerifiedEnd);
        Assert.Equal([1, 1, 2], chain.Events.Select(e => e.Batch ?? 0));
        Assert.All(chain.Events, e => Assert.True(e.Verified));
        Assert.Equal(0, chain.UnsealedEvents);
    }

    [Fact]
    public void ARunWithoutOverlays_HasNoProblems()
    {
        using var run = Sealed();

        Assert.Empty(run.ChainProblems());
    }

    [Fact]
    public void BatchInvalid_IsNotCheckedFurther_AndEndsThePrefix()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")], edit: p => p.Remove("length"));
        run.AppendBatch([Annotate("ov_3")]);

        var chain = run.Chain();

        // Batch 3's offset follows an invalid seal, whose range is unknown: not checked. Batch 2's range is claimed by nobody.
        Assert.Equal(["overlays/events.ndjson uncovered", "overlays/seal-0002.json batch-invalid"], Strings(chain));
        Assert.Equal([true, false, false], chain.Events.Select(e => e.Verified));
        Assert.Equal(2, chain.UnsealedEvents);
    }

    [Fact]
    public void Missing_ASealBelowTheHighest_AndPreviousWhenThatFileIsGone()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")]);
        run.Delete("overlays/seal-0001.json");

        Assert.Equal(
            ["overlays/events.ndjson uncovered", "overlays/seal-0001.json missing", "overlays/seal-0002.json previous"],
            run.ChainProblems());
    }

    [Fact]
    public void BatchNumber_APredicateBatchThatIsNotTheFilesNumber_AndSeal0000()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")], edit: p => p["batch"] = 3);
        run.WriteText("overlays/seal-0000.json", "{}");

        Assert.Equal(["overlays/seal-0000.json batch-number", "overlays/seal-0002.json batch-number"], run.ChainProblems());
    }

    [Fact]
    public void RunIdAndRunHash_AnotherRunsOrAnotherRunHash()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")], edit: p => p["runId"] = "another-run");
        run.AppendBatch([Annotate("ov_2")], edit: p => p["runHash"] = new string('a', 64));

        Assert.Equal(["overlays/seal-0001.json run-id", "overlays/seal-0002.json run-hash"], run.ChainProblems());
    }

    [Fact]
    public void RunHash_IsTheSealedValue_SoAWithheldBlobDoesNotBreakTheChain()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.Delete(TestRun.ReasoningPath);   // the recomputed hash changes; the sealed one does not
        run.AppendBatch([Annotate("ov_2")]);

        Assert.Empty(run.ChainProblems());
    }

    [Fact]
    public void Offset_AndLineBoundary_ARangeThatOverlapsThePreviousOne()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")], edit: p =>
        {
            // Starts 10 bytes early, inside the previous line; the digest is of those bytes, so batch-digest holds.
            p["offset"] = (int)p["offset"]! - 10;
            p["length"] = (int)p["length"]! + 10;
        });
        Resign(run, 2);

        Assert.Equal(["overlays/seal-0002.json line-boundary", "overlays/seal-0002.json offset"], run.ChainProblems());
    }

    [Fact]
    public void LineBoundaryAndBatchDigest_ARangeThatRunsPastTheEndOfTheFile()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")], edit: p => p["length"] = (int)p["length"]! + 1);

        Assert.Equal(["overlays/seal-0001.json batch-digest", "overlays/seal-0001.json line-boundary"], run.ChainProblems());
    }

    [Fact]
    public void BatchDigest_BytesThatNoLongerMatch_EndThePrefix()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")]);
        var events = Encoding.UTF8.GetString(run.ReadBytes("overlays/events.ndjson"));
        run.WriteText("overlays/events.ndjson", events.Replace("ov_1", "ov_9", StringComparison.Ordinal));

        var chain = run.Chain();

        Assert.Equal(["overlays/seal-0001.json batch-digest"], Strings(chain));
        Assert.Equal(0, chain.VerifiedEnd);   // batch 2 verifies on its own, but follows a broken batch
        Assert.Equal(2, chain.UnsealedEvents);
    }

    [Fact]
    public void Previous_ThatDoesNotNameThePreviousSealsBytes()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")], edit: p => p["previous"]!["sha256"] = new string('0', 64));

        Assert.Equal(["overlays/seal-0002.json previous"], run.ChainProblems());
    }

    [Fact]
    public void Uncovered_AnUnsealedTail_IsShownAsUnsealed()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        File.AppendAllText(Path.Combine(run.Dir, "overlays", "events.ndjson"), Encoding.UTF8.GetString(AgentEval.Results.Json.AefJsonWriter.Line(Annotate("ov_2"))));

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson uncovered"], Strings(chain));
        Assert.Equal([true, false], chain.Events.Select(e => e.Verified));
        Assert.Equal(1, chain.UnsealedEvents);
    }

    [Fact]
    public void EventLevelProblems_DoNotEndThePrefix()
    {
        using var run = Sealed();
        run.AppendBatch(
        [
            Annotate("ov_1"),
            Annotate("ov_1"),                                                          // event-id: the later one
            TestRun.Obj("""{"schemaVersion": "1.0", "eventId": "not an id"}"""),     // event-invalid
            TestRun.Event("ov_3", "annotate", new JsonObject { ["run"] = "another-run" }),
            TestRun.Event("ov_4", "annotate", new JsonObject { ["runHash"] = new string('a', 64) }),
            TestRun.Event("ov_5", "annotate", new JsonObject { ["result"] = "r_00000000000000000000000000000000" }),
            TestRun.Event("ov_6", "annotate", new JsonObject { ["result"] = TestRun.R4 }),
        ]);
        run.AppendBatch([Annotate("ov_7")]);

        var chain = run.Chain();

        Assert.Equal(
        [
            "overlays/events.ndjson:2 event-id", "overlays/events.ndjson:3 event-invalid", "overlays/events.ndjson:4 target",
            "overlays/events.ndjson:5 target", "overlays/events.ndjson:6 target",
        ], Strings(chain));
        Assert.Equal(run.ReadBytes("overlays/events.ndjson").Length, chain.VerifiedEnd);
        Assert.Equal(["ov_1", "ov_6", "ov_7"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
    }

    [Fact]
    public void AnInvalidLine_IsNotRecorded_SoALaterLineWithItsIdIsNoDuplicate()
    {
        using var run = Sealed();
        var invalid = Annotate("ov_1");
        invalid.Remove("at");
        run.AppendBatch([invalid, Annotate("ov_1")]);

        Assert.Equal(["overlays/events.ndjson:1 event-invalid"], run.ChainProblems());
    }

    [Fact]
    public void UnexpectedFile_UnderOverlays()
    {
        using var run = Sealed();
        using var signer = EcdsaP256Signer.Generate();
        run.AppendBatch([Annotate("ov_1")], signer);   // a batch signature is expected
        run.WriteText("overlays/notes.txt", "x");
        run.WriteText("overlays/seal-1.json", "{}");

        Assert.Equal(["overlays/notes.txt unexpected-file", "overlays/seal-1.json unexpected-file"], run.ChainProblems());
    }

    [Fact]
    public void Encoding_AnEventsFileWhoseFramingBreaks_IsReportedOnce_AndNothingElseOfTheChainIsChecked()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([Annotate("ov_2")], edit: p => p["batch"] = 7);
        var events = run.ReadBytes("overlays/events.ndjson");
        run.WriteBytes("overlays/events.ndjson", [.. events[..^1], (byte)'\r', (byte)'\n']);

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson encoding"], Strings(chain));
        Assert.Empty(chain.VerifiedEvents);
        Assert.Equal(2, chain.UnsealedEvents);
    }

    [Fact]
    public void Limit_ABatchSealNestedDeeperThan64_IsNotRead_AndEndsThePrefix()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        var seal = Encoding.UTF8.GetString(run.ReadBytes("overlays/seal-0001.json"));
        run.WriteText("overlays/seal-0001.json", seal.Replace("\"predicate\": {", "\"predicate\": {\"deep\": " + Deep(63) + ",", StringComparison.Ordinal));

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson uncovered", "overlays/seal-0001.json limit"], Strings(chain));
        Assert.Empty(chain.VerifiedEvents);
    }

    [Fact]
    public void Limit_AnEventLineNestedDeeperThan64_IsReportedAtTheLine_HasNoEffect_AndDoesNotEndThePrefix()
    {
        using var run = Sealed();
        var deepLine = Annotate("ov_2").ToJsonString()[..^1] + ",\"ext\":" + Deep(64) + "}\n";   // a writer would refuse it
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(AgentEval.Results.Json.AefJsonWriter.Line(Annotate("ov_1"))) + deepLine);
        run.WriteBytes("overlays/events.ndjson", bytes);
        var seal = new JsonObject
        {
            ["_type"] = "https://in-toto.io/Statement/v1",
            ["subject"] = new JsonArray(new JsonObject
            {
                ["name"] = OverlayChain.EventsPath,
                ["digest"] = new JsonObject { ["sha256"] = TestRun.Hex(System.Security.Cryptography.SHA256.HashData(bytes)) },
            }),
            ["predicateType"] = "https://agenteval.dev/aef/1/overlay-batch",
            ["predicate"] = new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["runId"] = TestRun.RunId,
                ["runHash"] = SealVerifier.RunHashOf(AgentEval.Results.Runs.AefRunFolder.Open(run.Dir)).Value,
                ["batch"] = 1,
                ["offset"] = 0,
                ["length"] = bytes.Length,
                ["previous"] = null,
            },
        };
        run.WriteBytes(OverlayChain.SealPath(1), AgentEval.Results.Json.AefJsonWriter.Document(seal, AefLimits.MaxSealBytes));
        run.AppendBatch([Annotate("ov_3")]);

        var chain = run.Chain();

        // [ENC-18]: limit at the line (not event-invalid); the other lines are read, and both batches verify.
        Assert.Equal(["overlays/events.ndjson:2 limit"], Strings(chain));
        Assert.Equal(run.ReadBytes("overlays/events.ndjson").Length, chain.VerifiedEnd);
        Assert.Equal(["ov_1", "ov_3"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
    }

    [Fact]
    public void Limit_AnEventsFileAbove1GiB_IsReportedAtTheFile_AndTheChainIsNotCheckedFurther()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        using (var file = new FileStream(Path.Combine(run.Dir, "overlays", "events.ndjson"), FileMode.Open, FileAccess.Write))
        {
            file.SetLength(AefLimits.MaxNdjsonBytes + 1);   // sparse where the file system allows it: never read
        }

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson limit"], Strings(chain));
        Assert.Empty(chain.VerifiedEvents);
    }

    [Fact]
    public void ChainProblems_NeverChangeTheRunsOutcome()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")], edit: p => p["runHash"] = new string('a', 64));
        run.WriteText("overlays/notes.txt", "x");

        Assert.NotEmpty(run.ChainProblems());
        Assert.Equal(AefOutcome.Intact, run.Verify().Outcome);
        Assert.Empty(run.Problems());
    }

    private static TestRun Sealed() => new TestRun().Write().Seal();

    private static JsonObject Annotate(string id) => TestRun.Event(id, "annotate", new JsonObject());

    // An object nested `depth` deep: {} is 1, {"a":{}} is 2.
    internal static string Deep(int depth) => depth <= 1 ? "{}" : "{\"a\":" + Deep(depth - 1) + "}";

    private static IReadOnlyList<string> Strings(OverlayChain chain) => [.. chain.Problems.Select(p => $"{p.Path} {p.Code}")];

    // After a test edits a seal's range, its digest must be of the new range for only the codes it means to show.
    private static void Resign(TestRun run, int batch)
    {
        var path = OverlayChain.SealPath(batch);
        var seal = JsonNode.Parse(run.ReadBytes(path))!.AsObject();
        var events = run.ReadBytes("overlays/events.ndjson");
        var offset = (int)(long)seal["predicate"]!["offset"]!;
        var length = (int)(long)seal["predicate"]!["length"]!;
        seal["subject"]![0]!["digest"]!["sha256"] = TestRun.Hex(System.Security.Cryptography.SHA256.HashData(events.AsSpan(offset, length)));
        run.WriteBytes(path, AgentEval.Results.Json.AefJsonWriter.Document(seal, AefLimits.MaxSealBytes));
    }
}
