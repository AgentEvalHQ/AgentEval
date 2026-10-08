using System.Security.Cryptography;
using System.Text;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Writing;

/// <summary>
/// The overlay writer (spec 04 §4.2–§4.3): batches that chain and verify in <see cref="OverlayChain"/>, the effective
/// view they give, and a redaction that withholds a deleted blob ([OVL-10]).
/// </summary>
public class AefOverlayWriterTests
{
    private static readonly byte[] Personal = Encoding.UTF8.GetBytes("the user's address: 1 Example Street\n");

    [Fact]
    public void Batches_ChainAndVerify_AndGiveTheEffectiveView()
    {
        using var run = SealedRun(out var k1, out var k2);
        var overlay = AefOverlayWriter.Open(run.Dir);
        using var alice = EcdsaP256Signer.Generate();

        overlay.Append(Approve(WriterRun.Alice));
        overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Passed, reason: "the judge misread the answer"));
        var first = overlay.SealBatch();
        overlay.Append(Event(AefOverlayKind.Waive, new AefOverlayTarget { Result = k2 }, reason: "known", expires: WriterRun.Start.AddDays(10)));
        overlay.Append(Event(AefOverlayKind.Reject, new AefOverlayTarget { Result = k2 }));
        overlay.Append(Event(AefOverlayKind.Annotate, new AefOverlayTarget { Requirement = "REQ-1" }, reason: "noted"));
        var second = overlay.SealBatch(alice);
        overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Warn, reason: "on second thought"));
        var third = overlay.SealBatch();

        Assert.Equal((1, 0L, 2), (first.Number, first.Offset, first.Events));
        Assert.Equal((2, first.Length, 3, "overlays/seal-0002.dsse.json"), (second.Number, second.Offset, second.Events, second.SignaturePath));
        Assert.Equal(3, overlay.Batches);
        var chain = Chain(run);
        Assert.Empty(chain.Problems);
        Assert.All(chain.Batches, b => Assert.True(b.Verified));
        Assert.Equal(third.Offset + third.Length, chain.VerifiedEnd);

        // [OVL-4]: batch n names seal n-1 and the SHA-256 of its bytes; every batch names the run and its run hash.
        var predicate = run.Json("overlays/seal-0002.json")["predicate"]!;
        Assert.Equal("overlays/seal-0001.json", predicate["previous"]!["path"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(run.Read("overlays/seal-0001.json"))).ToLowerInvariant(), predicate["previous"]!["sha256"]!.GetValue<string>());
        Assert.Equal(AefRunFolder.Open(run.Dir).ComputeRunHash(), predicate["runHash"]!.GetValue<string>());
        Assert.Null(run.Json("overlays/seal-0001.json")["predicate"]!["previous"]);

        var view = EffectiveView.Compute(run.Dir, AefTime.Parse("2026-10-05T00:00:00Z"), WriterRun.Policy(alice));
        var result = Assert.Single(view.Results);
        Assert.Equal((k1, "failed", "warn"), (result.ResultId, result.SealedState, result.EffectiveState));   // the last override ([OVL-7])
        Assert.Equal([("run", "approve"), (k2, "reject")], view.Reviews.Select(r => (r.Target, r.Status)));
        var waiver = Assert.Single(view.Waivers);
        Assert.True(waiver.Active);
        Assert.Empty(view.Withheld);
        Assert.Equal(0, view.UnsealedEvents);
    }

    [Fact]
    public void AnOverlay_NeverChangesASealedFile()
    {
        using var run = SealedRun(out var k1, out _);
        var before = run.Snapshot();
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Passed, reason: "r"));
        overlay.SealBatch();

        var after = run.Snapshot();
        Assert.All(before, f => Assert.Equal(f.Value, after[f.Key]));
        Assert.All(after.Keys.Except(before.Keys), p => Assert.StartsWith("overlays/", p, StringComparison.Ordinal));
        Assert.Equal(AefOutcome.Intact, run.Verify().Outcome);
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void EventsAppendedAndNotSealed_HaveNoEffect_UntilTheNextWriterSealsThem()
    {
        using var run = SealedRun(out var k1, out _);
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Passed, reason: "r"));

        Assert.Equal(["overlays/events.ndjson uncovered"], Chain(run).Problems.Select(p => $"{p.Path} {p.Code}"));
        Assert.Empty(EffectiveView.Compute(run.Dir, AefTime.Parse("2026-10-05T00:00:00Z"), null).Results);

        var next = AefOverlayWriter.Open(run.Dir);
        Assert.Equal(1, next.UnsealedAtOpen);
        Assert.Equal(1, next.SealBatch().Events);
        Assert.Empty(Chain(run).Problems);
        Assert.Single(EffectiveView.Compute(run.Dir, AefTime.Parse("2026-10-05T00:00:00Z"), null).Results);
    }

    [Fact]
    public void ARedactionSignedByAnIdentityThePolicyLetsRedact_WithholdsTheDeletedBlob()
    {
        using var run = SealedRun(out _, out _);
        using var alice = EcdsaP256Signer.Generate();
        using var bob = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey(WriterRun.Alice, alice.PublicKey), new TrustedKey(WriterRun.Bob, bob.PublicKey, [TrustPolicy.Redact])]);
        var blob = Sha(Personal);
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Redact, new AefOverlayTarget { Blob = blob }, by: WriterRun.Bob, reason: "personal data"));
        overlay.SealBatch(bob);

        AefOverlayWriter.DeleteRedactedBlob(run.Dir, blob, policy);

        Assert.False(File.Exists(run.Full(AefRunFolder.BlobPath(blob))));
        Assert.False(Directory.Exists(run.Full("blobs")));   // no empty folder left behind
        var verification = run.Verify(policy);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Equal(1, verification.Withheld);
        Assert.Equal([$"{AefRunFolder.BlobPath(blob)} withheld"], verification.Problems.Select(p => $"{p.Path} {p.Code}"));

        // Without the policy (or under one that does not let bob redact) the same blob is missing: the run is invalid.
        Assert.Equal(AefOutcome.Invalid, run.Verify().Outcome);
        Assert.Contains($"{AefRunFolder.BlobPath(blob)} missing", run.Problems());
        Assert.Contains($"{AefRunFolder.BlobPath(blob)} missing", run.Problems(WriterRun.Policy(bob, WriterRun.Bob)));
    }

    [Fact]
    public void ARedactionThePolicyDoesNotAuthorize_DeletesNothing()
    {
        using var run = SealedRun(out _, out _);
        using var bob = EcdsaP256Signer.Generate();
        var blob = Sha(Personal);
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Redact, new AefOverlayTarget { Blob = blob }, by: WriterRun.Bob, reason: "personal data"));
        overlay.SealBatch();   // unsigned: whoever can append an unsigned event cannot suppress evidence

        Assert.Throws<InvalidOperationException>(() => AefOverlayWriter.DeleteRedactedBlob(run.Dir, blob, WriterRun.Policy(bob, WriterRun.Bob, redact: true)));

        var signed = AefOverlayWriter.Open(run.Dir);
        signed.Append(Event(AefOverlayKind.Redact, new AefOverlayTarget { Blob = blob }, by: WriterRun.Bob, reason: "again, signed"));
        signed.SealBatch(bob);
        Assert.Throws<InvalidOperationException>(() => AefOverlayWriter.DeleteRedactedBlob(run.Dir, blob, WriterRun.Policy(bob, WriterRun.Bob, redact: false)));
        Assert.True(File.Exists(run.Full(AefRunFolder.BlobPath(blob))));
    }

    [Fact]
    public void OnlyASealedBlob_IsWithheld()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        var blob = writer.PutBlob(Personal);
        writer.AddResult(WriterRun.Leaf("k1", state: AefState.Failed, m: 0.1) with { Reasoning = blob });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        using var bob = EcdsaP256Signer.Generate();
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Redact, new AefOverlayTarget { Blob = blob.Sha256 }, by: WriterRun.Bob, reason: "personal data"));
        overlay.SealBatch(bob);

        Assert.Throws<InvalidOperationException>(() => AefOverlayWriter.DeleteRedactedBlob(run.Dir, blob.Sha256, WriterRun.Policy(bob, WriterRun.Bob, redact: true)));
    }

    [Fact]
    public void Open_RefusesARunningRun()
    {
        using var run = new WriterRun();
        run.Create();

        Assert.Throws<InvalidOperationException>(() => AefOverlayWriter.Open(run.Dir));
    }

    [Fact]
    public void Open_RefusesABrokenChain()
    {
        using var run = SealedRun(out var k1, out _);
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Passed, reason: "r"));
        overlay.SealBatch();
        File.WriteAllText(run.Full("overlays/events.ndjson"), File.ReadAllText(run.Full("overlays/events.ndjson")).Replace("\"r\"", "\"x\"", StringComparison.Ordinal));

        var refused = Assert.Throws<AefWriteException>(() => AefOverlayWriter.Open(run.Dir));
        Assert.Contains(refused.Problems, p => p is { Path: "overlays/seal-0001.json", Code: "batch-digest" });
    }

    [Fact]
    public void SealBatch_RefusesNothingToSeal_AndAFileChangedBehindItsBack()
    {
        using var run = SealedRun(out var k1, out _);
        var overlay = AefOverlayWriter.Open(run.Dir);
        Assert.Throws<InvalidOperationException>(() => overlay.SealBatch());

        overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Passed, reason: "r"));
        AefOverlayWriter.Open(run.Dir).Append(Event(AefOverlayKind.Annotate, new AefOverlayTarget { Result = k1 }, reason: "someone else's"));

        Assert.Throws<InvalidOperationException>(() => overlay.SealBatch());
        Assert.False(File.Exists(run.Full("overlays/seal-0001.json")));
    }

    [Fact]
    public void Append_RefusesWhatTheEventSchemaOrOvl2Refuses()
    {
        using var run = SealedRun(out var k1, out _);
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(Event(AefOverlayKind.Annotate, new AefOverlayTarget { Result = k1 }, reason: "first", id: "ov_1"));

        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Annotate, new AefOverlayTarget { Result = k1 }, reason: "again", id: "ov_1")));      // event-id
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, reason: "no state")));               // schema
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Override, new AefOverlayTarget { Result = k1 }, state: AefState.Pending, reason: "r")));
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Waive, new AefOverlayTarget { Result = k1 }, reason: "no expiry")));
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Approve, new AefOverlayTarget { Requirement = "REQ-1" })));
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Annotate, new AefOverlayTarget { Result = "r_" + new string('0', 32) }, reason: "x")));   // no result of the run
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Redact, new AefOverlayTarget { Blob = new string('0', 64) }, reason: "x")));               // no blob of the run
        Assert.Throws<ArgumentException>(() => overlay.Append(Event(AefOverlayKind.Annotate, AefOverlayTarget.TheRun, reason: "x", id: "not-ov")));
        Assert.Single(File.ReadAllLines(run.Full("overlays/events.ndjson")));
    }

    [Fact]
    public void AnUnsealedClosedRun_TakesOverlaysBoundToItsRecomputedRunHash()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var overlay = AefOverlayWriter.Open(run.Dir);

        overlay.Append(Approve(WriterRun.Alice));
        overlay.SealBatch();

        Assert.Equal(AefRunFolder.Open(run.Dir).ComputeRunHash(), overlay.RunHash);
        Assert.Empty(Chain(run).Problems);
        Assert.Equal(AefOutcome.Unsealed, run.Verify().Outcome);
    }

    internal static AefOverlayEvent Approve(string by) => Event(AefOverlayKind.Approve, AefOverlayTarget.TheRun, by: by);

    internal static AefOverlayEvent Event(
        AefOverlayKind kind, AefOverlayTarget target, AefState? state = null, string? reason = null, DateTimeOffset? expires = null, string by = WriterRun.Alice, string? id = null) => new()
    {
        EventId = id,
        Kind = kind,
        Target = target,
        State = state,
        Reason = reason,
        Expires = expires,
        By = new AefIdentity(by, AefAssurance.SelfAttested),
        At = WriterRun.Start.AddDays(1),
    };

    // A sealed run with two leaves (k1 failed, k2 passed) and a blob holding personal data, cited as k1's reasoning.
    private static WriterRun SealedRun(out string k1, out string k2)
    {
        var run = new WriterRun();
        var writer = run.Create();
        var blob = writer.PutBlob(Personal);
        k1 = writer.AddResult(WriterRun.Leaf("k1", m: 0.4, state: AefState.Failed) with { Reasoning = blob }).ResultId;
        k2 = writer.AddResult(WriterRun.Leaf("k2")).ResultId;
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(5));
        AefSealer.Seal(run.Dir, WriterRun.Options());
        return run;
    }

    private static OverlayChain Chain(WriterRun run)
    {
        var folder = AefRunFolder.Open(run.Dir);
        return OverlayChain.Verify(folder, AefRunDocuments.Read(folder));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
