using System.Text.Json.Nodes;
using AgentEval.Results.Conformance;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Integrity;

/// <summary>§4.5: the outcome, signed by, anchored and withheld; and the run, seal, chain and view operations' contract.</summary>
public class AefRunVerifierTests
{
    [Fact]
    public void Outcomes_Unsealed_Intact_Invalid()
    {
        using var run = new TestRun().Write();
        Assert.Equal(AefOutcome.Unsealed, run.Verify().Outcome);

        run.Seal();
        Assert.Equal(AefOutcome.Intact, run.Verify().Outcome);

        run.WriteText("ext/later", "x");
        Assert.Equal(AefOutcome.Invalid, run.Verify().Outcome);
    }

    [Fact]
    public void AnUnsealedRunWithAProblem_IsInvalid()
    {
        using var run = new TestRun();
        run.Results[1]["parentResultId"] = "r_00000000000000000000000000000000";
        run.Write();

        Assert.Equal(AefOutcome.Invalid, run.Verify().Outcome);
    }

    [Fact]
    public void SignedBy_TheIdentitiesTheAttestationVerifiesFor_ForAnIntactRunOnly()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var bob = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal().Attest(alice);
        var policy = new TrustPolicy([new TrustedKey(TestRun.Bob, bob.PublicKey), new TrustedKey(TestRun.Alice, alice.PublicKey)]);

        Assert.Equal([TestRun.Alice], run.Verify(policy).SignedBy);
        Assert.Empty(run.Verify(TestRun.Policy(bob, TestRun.Bob)).SignedBy!);   // a key the policy does not list
        Assert.Null(run.Verify().SignedBy);                                      // no policy, no answer

        run.WriteText("ext/later", "x");                                          // no longer intact
        Assert.Empty(run.Verify(policy).SignedBy!);
    }

    [Fact]
    public void SignedBy_AnAttestationOfOtherBytes_VerifiesForNobody()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal().Attest(alice);
        run.Seal(s => s["predicate"]!["sealedAt"] = "2026-10-01T10:07:00Z");   // re-sealed: the attestation signs the old seal

        var verification = run.Verify(TestRun.Policy(alice));

        Assert.Equal(AefOutcome.Intact, verification.Outcome);   // a seal is not a signature ([SIG-7])
        Assert.Empty(verification.SignedBy!);
    }

    [Fact]
    public void Anchored_WhenAnIntactRunsRunHashIsTrusted()
    {
        using var run = new TestRun().Write().Seal();
        var runHash = AefRunFolder.Open(run.Dir).ComputeRunHash();

        Assert.True(run.Verify(anchors: [runHash]).Anchored);
        Assert.False(run.Verify(anchors: [new string('a', 64)]).Anchored);
        Assert.Null(run.Verify().Anchored);

        run.WriteText("ext/later", "x");
        Assert.False(run.Verify(anchors: [runHash]).Anchored);   // not intact
    }

    [Fact]
    public void Anchored_UsesTheSealedRunHash_WhichAWithheldBlobLeavesTheOnlyOneKnown()
    {
        using var alice = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal();
        var sealedHash = AefRunFolder.Open(run.Dir).ComputeRunHash();
        run.AppendBatch([TestRun.Event("ov_1", "redact", new JsonObject { ["blob"] = TestRun.ReasoningSha }, edit: e => e["reason"] = "erased")], alice);
        run.Delete(TestRun.ReasoningPath);

        var verification = run.Verify(TestRun.Policy(alice, redact: true), [sealedHash]);

        Assert.Equal((AefOutcome.Intact, true, 1), (verification.Outcome, verification.Anchored, verification.Withheld));
        Assert.Equal(new AefRunHash(sealedHash, true), verification.RunHash);
    }

    [Fact]
    public void RunOperation_PrintsWithheldOnlyWhenNot0_SignedByOnlyWithAPolicy_AnchoredOnlyWithAnchors()
    {
        using var run = new TestRun().Write().Seal();

        Assert.Equal("""{"outcome":"intact","problems":[]}""", Run("run", run.Dir).Stdout.TrimEnd('\n'));

        using var alice = EcdsaP256Signer.Generate();
        var policy = Path.Combine(run.Dir, "..", $"aef-policy-{Guid.NewGuid():N}.json");
        var anchors = Path.Combine(run.Dir, "..", $"aef-anchors-{Guid.NewGuid():N}.json");
        File.WriteAllText(policy, new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject { ["identity"] = TestRun.Alice, ["publicKey"] = alice.PublicKey.ToPem() }),
        }.ToJsonString());
        File.WriteAllText(anchors, "[]");
        try
        {
            Assert.Equal(
                """{"outcome":"intact","problems":[],"signedBy":[],"anchored":false}""",
                Run("run", run.Dir, "--policy", policy, "--anchors", anchors).Stdout.TrimEnd('\n'));
        }
        finally
        {
            File.Delete(policy);
            File.Delete(anchors);
        }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "/no/such/run")]
    [InlineData("run", "a", "b")]
    [InlineData("run", ".", "--policy")]
    [InlineData("run", ".", "--policy", "/no/such/policy.json")]
    [InlineData("run", ".", "--nope", "x")]
    [InlineData("seal", "/no/such/run")]
    [InlineData("chain")]
    [InlineData("view", ".", "--at", "2026-02-31T00:00:00Z")]
    [InlineData("view", ".", "--at", "2026-10-08T12:00:00+02:00")]
    public void AUsageOrInputError_ExitsWith2(params string[] args)
    {
        var (code, stdout, stderr) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.NotEqual("", stderr);
    }

    [Fact]
    public void AnAnchorsFileThatIsNotAListOfStrings_IsAnInputError()
    {
        using var run = new TestRun().Write();
        var anchors = Path.Combine(Path.GetTempPath(), $"aef-anchors-{Guid.NewGuid():N}.json");
        File.WriteAllText(anchors, """{"runHash": "x"}""");
        try
        {
            Assert.Equal(2, Run("run", run.Dir, "--anchors", anchors).Code);
        }
        finally
        {
            File.Delete(anchors);
        }
    }

    [Fact]
    public void ViewOperation_DefaultsAtToNow()
    {
        using var run = new TestRun().Write().Seal();
        run.AppendBatch([TestRun.Event("ov_1", "waive", new JsonObject { ["requirement"] = "R" }, edit: e =>
        {
            e["reason"] = "r";
            e["at"] = "2000-01-01T00:00:00Z";
            e["expires"] = "9999-12-31T23:59:59Z";
        })]);

        var view = JsonNode.Parse(Run("view", run.Dir).Stdout)!;

        Assert.True((bool)view["waivers"]![0]!["active"]!);
        Assert.Equal(0, (int)view["unsealedEvents"]!);
    }

    private static (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Dispatch(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
