using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Integrity;

/// <summary>§4.3: the effective view ([OVL-6]–[OVL-10]) from the verified batches, and withheld blobs told from missing ones.</summary>
public class EffectiveViewTests
{
    private static readonly AefTime Now = AefTime.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public void TheLastOverrideOrAdjudicate_SetsTheEffectiveState_ShownWithTheSealedOne_InResultsOrder()
    {
        using var run = Sealed();
        run.AppendBatch(
        [
            State("ov_1", "override", TestRun.R4, "failed"),
            State("ov_2", "override", TestRun.R3, "passed"),
            State("ov_3", "adjudicate", TestRun.R3, "warn", day: -1),   // applies in file order: at is never used to reorder
        ]);

        var view = View(run);

        Assert.Equal(
        [
            new EffectiveResult(TestRun.R3, "failed", "warn", "ov_3"),
            new EffectiveResult(TestRun.R4, "passed", "failed", "ov_1"),
        ], view.Results);
    }

    [Fact]
    public void Reviews_TheRunFirst_ThenResultsInResultsOrder_EachTheLastApproveOrReject()
    {
        using var run = Sealed();
        run.AppendBatch(
        [
            TestRun.Event("ov_1", "approve", new JsonObject { ["result"] = TestRun.R4 }),
            TestRun.Event("ov_2", "reject", new JsonObject { ["result"] = TestRun.R1 }),
            TestRun.Event("ov_3", "approve", new JsonObject()),
            TestRun.Event("ov_4", "reject", new JsonObject()),
            TestRun.Event("ov_5", "acknowledge", new JsonObject()),        // recorded, no effect
            TestRun.Event("ov_6", "a-later-kind", new JsonObject()),       // an annotation (§7.3)
        ]);

        var view = View(run);

        Assert.Equal(
        [
            new EffectiveReview("run", "reject", "ov_4"),
            new EffectiveReview(TestRun.R1, "reject", "ov_2"),
            new EffectiveReview(TestRun.R4, "approve", "ov_1"),
        ], view.Reviews);
        Assert.Empty(view.Results);
    }

    [Fact]
    public void AWaiver_HoldsFromItsAtUntilItsExpires_ComparedWithTheViewsTime()
    {
        using var run = Sealed();
        run.AppendBatch(
        [
            Waive("ov_1", "REQ-1", "2026-10-08T12:00:00Z"),                         // expires now: expired
            Waive("ov_2", "REQ-2", "2026-10-08T12:00:00.000000001Z"),               // a nanosecond later: holds
            Waive("ov_3", "REQ-3", "2026-12-01T00:00:00Z", at: "2026-10-08T12:00:00Z"),   // starts now: holds
            Waive("ov_4", "REQ-4", "2026-12-01T00:00:00Z", at: "2026-10-09T00:00:00Z"),   // not yet
        ]);

        var view = View(run);

        Assert.Equal([false, true, true, false], view.Waivers.Select(w => w.Active));
        Assert.Equal("""{"requirement":"REQ-1"}""", view.Waivers[0].Target.ToJsonString());   // without run and runHash
        Assert.Equal(["ov_1", "ov_2", "ov_3", "ov_4"], view.Waivers.Select(w => w.Event));
    }

    [Fact]
    public void OnlyTheVerifiedPrefixHasEffect_EventsAfterItAreUnsealed()
    {
        using var run = Sealed();
        run.AppendBatch([State("ov_1", "override", TestRun.R3, "passed")]);
        run.AppendBatch([State("ov_2", "override", TestRun.R3, "error")], edit: p => p["runHash"] = new string('a', 64));
        run.AppendBatch([TestRun.Event("ov_3", "approve", new JsonObject())]);   // verifies on its own, but after a broken batch

        var view = View(run);

        Assert.Equal([new EffectiveResult(TestRun.R3, "failed", "passed", "ov_1")], view.Results);
        Assert.Empty(view.Reviews);
        Assert.Equal(2, view.UnsealedEvents);
    }

    [Fact]
    public void AnEventWithAProblem_HasNoEffect_ButTheBatchStillDoes()
    {
        using var run = Sealed();
        run.AppendBatch(
        [
            State("ov_1", "override", TestRun.R3, "passed"),
            State("ov_1", "override", TestRun.R3, "error"),                                  // event-id: the later one has no effect
            State("ov_2", "override", TestRun.R3, "skipped", target: t => t["run"] = "x"),   // target
        ]);

        Assert.Equal([new EffectiveResult(TestRun.R3, "failed", "passed", "ov_1")], View(run).Results);
    }

    [Fact]
    public void Withheld_AnAuthorizedRedaction_SignedByTheEventsIdentity_WhichThePolicyLetsRedact()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = Redacted(alice, by: TestRun.Alice);

        var policy = TestRun.Policy(alice, redact: true);

        Assert.Equal([TestRun.ReasoningSha], View(run, policy).Withheld);
        var verification = run.Verify(policy);
        Assert.Equal((AefOutcome.Intact, 1), (verification.Outcome, verification.Withheld));
        Assert.Equal([$"{TestRun.ReasoningPath} withheld"], verification.Problems.Select(p => $"{p.Path} {p.Code}"));
    }

    [Fact]
    public void Missing_AnUnsignedRedaction()
    {
        using var run = new TestRun().Write().Seal();
        run.AppendBatch([Redact()]);
        run.Delete(TestRun.ReasoningPath);
        using var alice = EcdsaP256Signer.Generate();

        AssertMissing(run, TestRun.Policy(alice, redact: true));
    }

    [Fact]
    public void Missing_ARedactionByAnIdentityThePolicyDoesNotLetRedact()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = Redacted(alice, by: TestRun.Alice);

        AssertMissing(run, TestRun.Policy(alice, redact: false));
    }

    [Fact]
    public void Missing_ARedactionWhoseBatchIsSignedByAnotherIdentity()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var bob = EcdsaP256Signer.Generate();
        using var run = Redacted(alice, by: TestRun.Bob);   // the event says bob; alice signed the batch
        var policy = new TrustPolicy(
        [
            new TrustedKey(TestRun.Alice, alice.PublicKey, [TrustPolicy.Redact]),
            new TrustedKey(TestRun.Bob, bob.PublicKey, [TrustPolicy.Redact]),
        ]);

        AssertMissing(run, policy);
    }

    [Fact]
    public void Missing_ARedactionWithoutATrustPolicy()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = Redacted(alice, by: TestRun.Alice);

        AssertMissing(run, policy: null);
    }

    [Fact]
    public void Missing_ARedactionInABatchAfterTheVerifiedPrefix()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal();
        run.AppendBatch([TestRun.Event("ov_0", "annotate", new JsonObject())], edit: p => p["runId"] = "x");
        run.AppendBatch([Redact()], alice);
        run.Delete(TestRun.ReasoningPath);

        AssertMissing(run, TestRun.Policy(alice, redact: true));
    }

    [Fact]
    public void InAnUnsealedRun_AnAuthorizedRedaction_DoesNotExemptAMissingBlob()
    {
        // W3-8 (ruled 10-08), crafted case (i): the blob is deleted first and the batch then sealed against the run hash
        // recomputed without it, so the chain verifies and the redaction is authorized. §3.9 blob exempts a missing blob
        // only when the run is sealed.
        using var alice = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write();
        run.Delete(TestRun.ReasoningPath);
        run.AppendBatch([Redact()], alice);
        var policy = TestRun.Policy(alice, redact: true);

        Assert.Empty(run.ChainProblems());
        Assert.Equal([TestRun.ReasoningSha], View(run, policy).Withheld);   // the view lists what the redaction names
        var verification = run.Verify(policy);
        Assert.Equal((AefOutcome.Invalid, 0), (verification.Outcome, verification.Withheld));
        Assert.Equal(["evidence.ndjson:1 blob", "results.ndjson:3 blob"], verification.Problems.Select(p => $"{p.Path} {p.Code}"));
    }

    [Fact]
    public void ARedactionOfABlobStillPresent_WithholdsNothingThatIsMissing()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal();
        run.AppendBatch([Redact()], alice);

        var policy = TestRun.Policy(alice, redact: true);

        Assert.Equal([TestRun.ReasoningSha], View(run, policy).Withheld);   // authorized: the view lists it
        var verification = run.Verify(policy);
        Assert.Equal((AefOutcome.Intact, 0), (verification.Outcome, verification.Withheld));   // nothing is gone
    }

    private static TestRun Sealed() => new TestRun().Write().Seal();

    private static EffectiveView View(TestRun run, TrustPolicy? policy = null) => EffectiveView.Compute(run.Dir, Now, policy);

    private static JsonObject State(string id, string kind, string result, string state, int day = 0, Action<JsonObject>? target = null)
    {
        var t = new JsonObject { ["result"] = result };
        var e = TestRun.Event(id, kind, t, day: day, edit: x =>
        {
            x["state"] = state;
            x["reason"] = "reviewed";
        });
        target?.Invoke(t);
        return e;
    }

    private static JsonObject Waive(string id, string requirement, string expires, string at = "2026-10-01T00:00:00Z") =>
        TestRun.Event(id, "waive", new JsonObject { ["requirement"] = requirement }, edit: e =>
        {
            e["reason"] = "accepted risk";
            e["expires"] = expires;
            e["at"] = at;
        });

    private static JsonObject Redact(string by = TestRun.Alice) =>
        TestRun.Event("ov_9", "redact", new JsonObject { ["blob"] = TestRun.ReasoningSha }, by, edit: e => e["reason"] = "personal data");

    // A sealed run whose reasoning blob a redaction by `by` removed, in a batch `signer` signed.
    private static TestRun Redacted(IAefSigner signer, string by)
    {
        var run = new TestRun().Write().Seal();
        run.AppendBatch([Redact(by)], signer);
        run.Delete(TestRun.ReasoningPath);
        return run;
    }

    private static void AssertMissing(TestRun run, TrustPolicy? policy)
    {
        Assert.Empty(View(run, policy).Withheld);
        var verification = run.Verify(policy);
        Assert.Equal((AefOutcome.Invalid, 0), (verification.Outcome, verification.Withheld));
        Assert.Equal(
            [$"{TestRun.ReasoningPath} missing", "evidence.ndjson:1 blob", "results.ndjson:3 blob"],
            verification.Problems.Select(p => $"{p.Path} {p.Code}"));
    }
}
