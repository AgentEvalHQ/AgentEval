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
    public void ABlankLineInsideABatch_IsEventInvalidAtItsLine_AndTheBatchStillVerifies()
    {
        // [OVL-5] (R4N-9): the events file is judged line by line inside the batches too. Batch 1 holds a blank line: it
        // is event-invalid at its line, and nothing else; batch 1 verifies and its other events count. The problems of
        // batch 2 and of the listing are reported as ever.
        using var run = Sealed();
        run.AppendBatch([.. Line(Annotate("ov_1")), (byte)'\n', .. Line(Annotate("ov_2"))]);
        run.AppendBatch([Annotate("ov_3")], edit: p => p["batch"] = 7);
        run.WriteText("overlays/notes.txt", "x");

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson:2 event-invalid", "overlays/notes.txt unexpected-file", "overlays/seal-0002.json batch-number"], Strings(chain));
        Assert.Equal(["ov_1", "ov_2"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal([true, false], chain.Batches.Select(b => b.Verified));
        Assert.Equal(chain.Batches[0].End, chain.VerifiedEnd);
        Assert.Equal(1, chain.UnsealedEvents);
        Assert.DoesNotContain(chain.Problems, p => p.Code == "encoding");
    }

    [Theory]
    [InlineData("cr")]
    [InlineData("bom")]
    public void ACrOrALeadingByteOrderMarkInsideABatch_IsEventInvalidAtItsLine_Too(string kind)
    {
        // [OVL-5] (R4N-9): a line holding a CR, or one that begins with a byte-order mark, is a problem of its line alone,
        // wherever it is; the batch holding it verifies, and the next event still counts.
        using var run = Sealed();
        var line = Line(Annotate("ov_1"));
        byte[] broken = kind == "cr" ? [.. line[..^1], (byte)'\r', (byte)'\n'] : [0xEF, 0xBB, 0xBF, .. line];
        run.AppendBatch([.. broken, .. Line(Annotate("ov_2"))]);

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson:1 event-invalid"], Strings(chain));
        Assert.True(chain.Batches.Single().Verified);
        Assert.Equal(["ov_2"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
    }

    [Fact]
    public void ACrashedLineClaimedByTheNextBatch_CostsOneLine_NeverTheChain()
    {
        // [OVL-5] (R4N-9): half a line a crash left after batch 1, ended with an LF by the next writer, and sealed with its
        // event by batch 2: the half line is event-invalid, both batches verify, and the next event counts.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch([.. Line(Annotate("ov_2"))[..20], (byte)'\n', .. Line(Annotate("ov_3"))]);

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson:2 event-invalid"], Strings(chain));
        Assert.All(chain.Batches, b => Assert.True(b.Verified));
        Assert.Equal(["ov_1", "ov_3"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal(0, chain.UnsealedEvents);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("cr")]
    [InlineData("bom")]
    public void Tail_ABlankLine_OrALineHoldingACrOrAByteOrderMark_IsEventInvalidAtItsLine(string kind)
    {
        // [OVL-5]: after the verified batches, lines are judged one by one, so what an appender writes there never
        // changes which batches verify. A CR before the LF of a valid event makes it invalid too: a JSON reader alone
        // would skip it as whitespace. A byte-order mark before a valid event, likewise.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        byte[] line = kind switch
        {
            "blank" => [(byte)'\n'],
            "cr" => [(byte)'\r', (byte)'\n'],
            _ => [0xEF, 0xBB, 0xBF, .. Line(Annotate("ov_9"))],
        };
        var withCr = Line(Annotate("ov_2"));
        Append(run, [.. line, .. withCr[..^1], (byte)'\r', (byte)'\n', .. Line(Annotate("ov_3"))]);

        var chain = run.Chain();

        Assert.Equal(
            ["overlays/events.ndjson uncovered", "overlays/events.ndjson:2 event-invalid", "overlays/events.ndjson:3 event-invalid"],
            Strings(chain));
        Assert.Equal(["ov_1"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal(3, chain.UnsealedEvents);
        Assert.Equal("ov_3", (string)chain.Events[^1].Event!["eventId"]!);
    }

    [Fact]
    public void Tail_AU_FEFFInsideAString_IsContent_NotAByteOrderMark()
    {
        // [OVL-5] (round 4 clarification): a line that begins with EF BB BF is event-invalid; U+FEFF inside a JSON string
        // is ordinary content, after the verified batches as within them.
        // The raw bytes EF BB BF inside the string (a writer may escape it instead, so they are put in by hand).
        static byte[] WithFeff(string id, string reason) =>
            [.. Line(TestRun.Event(id, "annotate", new JsonObject(), edit: e => e["reason"] = reason)).SelectMany(b => b == (byte)'~' ? new byte[] { 0xEF, 0xBB, 0xBF } : [b])];
        using var run = Sealed();
        run.AppendBatch(WithFeff("ov_1", "a~b"));
        Append(run, WithFeff("ov_2", "~"));
        Assert.Equal(2, run.ReadBytes("overlays/events.ndjson").AsSpan().Count((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson uncovered"], Strings(chain));
        Assert.Equal(["ov_1"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal("ov_2", (string)chain.Events[^1].Event!["eventId"]!);
    }

    [Fact]
    public void Tail_AnUnfinishedLastLine_IsStillBeingWritten_NeitherShownNorReported()
    {
        // [OVL-5]: half a line after the last batch (an append in progress, or a crash). Its bytes are claimed by no batch.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        Append(run, [.. Line(Annotate("ov_2")), .. Line(Annotate("ov_3"))[..20]]);

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson uncovered"], Strings(chain));
        Assert.Equal(2, chain.Events.Count);
        Assert.Equal(1, chain.UnsealedEvents);
        Assert.Equal(["ov_1"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
    }

    [Fact]
    public void Tail_ABatchOverAnUnfinishedLine_IsLineBoundary_AndThePrefixBeforeItStillVerifies()
    {
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch(Line(Annotate("ov_2"))[..^1]);   // a batch over half a line: no LF at its end

        var chain = run.Chain();

        Assert.Equal(["overlays/seal-0002.json line-boundary"], Strings(chain));
        Assert.Equal(["ov_1"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal(0, chain.UnsealedEvents);
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
    public void Limit_AnEventsFileAbove1GiB_IsReportedOnce_ItsBatchesWithinWhatWasReadStillVerify()
    {
        // [OVL-5]: read up to its last LF within the first 1 GiB (scanned, not held); beyond it, nothing is read.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        using (var file = new FileStream(Path.Combine(run.Dir, "overlays", "events.ndjson"), FileMode.Open, FileAccess.Write))
        {
            file.SetLength(AefLimits.MaxNdjsonBytes + 1);   // sparse zeros where the file system allows it
        }

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson limit", "overlays/events.ndjson uncovered"], Strings(chain));
        Assert.Equal(["ov_1"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal(0, chain.UnsealedEvents);
    }

    [Fact]
    public void Limit_AnEventsFileOfMoreThanAMillionLines_IsReportedOnce_AndNoLineAfterTheVerifiedBatchesIsRead()
    {
        // [OVL-5]: a million blank lines after the sealed batches would each be event-invalid; beyond the limit, none is
        // read, and the batches before them verify.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1"), Annotate("ov_2")]);
        Append(run, Enumerable.Repeat((byte)'\n', AefLimits.MaxLines).ToArray());

        var chain = run.Chain();

        Assert.Equal(["overlays/events.ndjson limit", "overlays/events.ndjson uncovered"], Strings(chain));
        Assert.Equal(["ov_1", "ov_2"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
        Assert.Equal(0, chain.UnsealedEvents);
    }

    [Fact]
    public void Limit_AFileAtTheLineLimit_IsNotALimit()
    {
        // [ENC-18]: a reader refuses nothing within the limits: 1,000,000 lines, then an unfinished one (no LF to count).
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        Append(run, [.. Enumerable.Repeat((byte)'\n', AefLimits.MaxLines - 1), (byte)'{']);

        var chain = run.Chain();

        Assert.DoesNotContain(chain.Problems, p => p.Code == "limit");
        Assert.Equal(AefLimits.MaxLines, chain.Events.Count);
    }

    [Fact]
    public void Limit_ABatchWhoseRangeEndsBeyondWhatWasRead_IsLimitAtItsSeal_AndEndsThePrefix()
    {
        // [OVL-5]: batch 2 seals the 1,000,000th line and the one after it, which a reader does not read: limit at its
        // seal, a seal refused as the table of OVL-5 defines limit at a seal (R4N-2): not checked further (its runId is
        // another run's, unreported; its bytes are not read), claiming no bytes, so they are uncovered. Batch 3, after
        // it, too.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        run.AppendBatch(Enumerable.Repeat((byte)'\n', AefLimits.MaxLines).ToArray(), edit: p => p["runId"] = "another-run");
        run.AppendBatch([Annotate("ov_3")]);

        var chain = run.Chain();

        Assert.Equal(
            ["overlays/events.ndjson limit", "overlays/events.ndjson uncovered", "overlays/seal-0002.json limit", "overlays/seal-0003.json limit"],
            Strings(chain));
        Assert.Equal([true, false, false], chain.Batches.Select(b => b.Verified));
        Assert.Equal(["ov_1"], chain.VerifiedEvents.Select(e => (string)e.Event!["eventId"]!));
    }

    [Fact]
    public void Limit_MoreThan19999FilesUnderOverlays_IsReportedAtOverlays_AndTheChainIsNotChecked()
    {
        // [ENC-17], [ENC-18], [OVL-5]: one events file and two per batch at most. The corpus has no vector of its own
        // (too many files); here 19,999 files, and then 20,000.
        using var run = Sealed();
        run.AppendBatch([Annotate("ov_1")]);
        var overlays = Path.Combine(run.Dir, "overlays");
        for (var i = 3; i <= AefLimits.MaxOverlayFiles; i++)
        {
            File.WriteAllBytes(Path.Combine(overlays, $"x{i}"), []);
        }

        var atTheLimit = run.Chain();
        Assert.Equal(AefLimits.MaxOverlayFiles - 2, atTheLimit.Problems.Count(p => p.Code == "unexpected-file"));
        Assert.DoesNotContain(atTheLimit.Problems, p => p.Code == "limit");
        Assert.Single(atTheLimit.VerifiedEvents);

        Directory.CreateDirectory(Path.Combine(overlays, "deeper"));
        File.WriteAllBytes(Path.Combine(overlays, "deeper", "one-more"), []);   // a file in a folder under overlays/ counts too
        var folder = AgentEval.Results.Runs.AefRunFolder.Open(run.Dir);
        var over = OverlayChain.Verify(folder, AgentEval.Results.Runs.AefRunDocuments.Read(folder));

        Assert.Equal(["overlays limit"], Strings(over));
        Assert.Empty(over.VerifiedEvents);
        Assert.Equal(0, over.UnsealedEvents);
        Assert.True(folder.OverlaysOverLimit);
        Assert.Equal(AefOutcome.Intact, run.Verify().Outcome);   // an overlay problem never changes the run's outcome

        // The run verifier still lists them, and checks their paths ([RUN-3], R4N-8): a link among them is a path problem.
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(overlays, "link"), "../run.json");
            Assert.Equal(["overlays/link path"], run.Problems());
            Assert.Equal(["overlays limit"], run.ChainProblems());
        }
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

    private static byte[] Line(JsonObject e) => AgentEval.Results.Json.AefJsonWriter.Line(e);

    // Appends bytes to the events file, sealing none of them.
    private static void Append(TestRun run, byte[] bytes)
    {
        using var file = new FileStream(Path.Combine(run.Dir, "overlays", "events.ndjson"), FileMode.Append, FileAccess.Write);
        file.Write(bytes);
    }

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
