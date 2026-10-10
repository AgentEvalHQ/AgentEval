using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;
using TestRun = AgentEval.Results.Tests.Integrity.TestRun;

namespace AgentEval.Results.Tests.Json;

/// <summary>
/// [ENC-17]: a DSSE envelope may be up to 56 MiB (it holds the base64 of a seal of up to 40 MiB), above the 4 MiB of
/// other JSON files. An envelope between the two reads and verifies; one above 56 MiB is neither written nor read.
/// </summary>
public class EnvelopeLimitTests
{
    // An in-toto statement padded to the given size (a seal of a run with many files).
    private static byte[] Seal(int bytes)
    {
        const string Head = "{\"_type\":\"https://in-toto.io/Statement/v1\",\"note\":\"";
        return Encoding.UTF8.GetBytes(Head + new string('a', bytes - Head.Length - 2) + "\"}");
    }

    [Fact]
    public void AnEnvelopeBetween4And56Mebibytes_IsWritten_Read_AndVerified()
    {
        using var signer = EcdsaP256Signer.Generate();
        var seal = Seal(30 * 1024 * 1024);   // base64 makes the envelope about 40 MiB: above the old 32 MiB
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, seal, signer).ToJson();
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);

        Assert.InRange(envelope.Length, AefLimits.MaxSealBytes - (1024 * 1024), AefLimits.MaxEnvelopeBytes);
        var result = DsseVerifier.Verify(envelope, seal, Dsse.InTotoPayloadType, policy);
        Assert.Null(result.EnvelopeResult);
        Assert.Equal(["git:test@example.com"], result.VerifiesFor);
    }

    [Fact]
    public void AnEnvelopeAbove56Mebibytes_IsNotWritten_AndReadsAsMalformed()
    {
        using var signer = EcdsaP256Signer.Generate();
        var seal = Seal(43 * 1024 * 1024);  // base64 makes the envelope about 57.3 MiB
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, seal, signer);
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);

        Assert.Throws<ArgumentException>(() => envelope.ToJson());

        // The same envelope written by hand: valid in every way but its size.
        var signature = envelope.Signatures[0];
        var bytes = Encoding.UTF8.GetBytes(
            $"{{\"payloadType\":\"{envelope.PayloadType}\",\"payload\":\"{Convert.ToBase64String(seal)}\"," +
            $"\"signatures\":[{{\"keyid\":\"{signature.KeyId}\",\"sig\":\"{Convert.ToBase64String(signature.Sig)}\"}}]}}");
        Assert.True(bytes.Length > AefLimits.MaxEnvelopeBytes);
        Assert.Equal(DsseEnvelopeResult.Malformed, DsseVerifier.Verify(bytes, seal, Dsse.InTotoPayloadType, policy).EnvelopeResult);
        Assert.True(DsseVerifier.Verify(envelope, seal, Dsse.InTotoPayloadType, policy).VerifiesForIdentity("git:test@example.com"));
    }

    [Fact]
    public void AnEnvelopeFileBeyond56Mebibytes_IsMalformedWithoutBeingRead_AndOneAtTheLimitVerifies()
    {
        // [SIG-1] (round 5): beyond its limit an envelope is malformed without being read, and verifies for no one; a
        // valid envelope padded with whitespace to exactly 56 MiB is within it.
        using var signer = EcdsaP256Signer.Generate();
        var seal = Seal(1024);
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, seal, signer).ToJson();
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);
        var path = Path.Combine(Path.GetTempPath(), $"aef-envelope-{Guid.NewGuid():N}.dsse.json");
        try
        {
            File.WriteAllBytes(path, Padded(envelope, AefLimits.MaxEnvelopeBytes));
            var atTheLimit = DsseVerifier.VerifyFile(path, seal, Dsse.InTotoPayloadType, policy);
            Assert.Null(atTheLimit.EnvelopeResult);
            Assert.Equal(["git:test@example.com"], atTheLimit.VerifiesFor);

            File.WriteAllBytes(path, Padded(envelope, AefLimits.MaxEnvelopeBytes + 1));
            var beyond = DsseVerifier.VerifyFile(path, seal, Dsse.InTotoPayloadType, policy);
            Assert.Equal(DsseEnvelopeResult.Malformed, beyond.EnvelopeResult);
            Assert.Empty(beyond.Signatures);
            Assert.Empty(beyond.VerifiesFor);
            Assert.Contains("not read", beyond.Why, StringComparison.Ordinal);
            Assert.Null(DsseVerifier.ReadEnvelopeFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void AnAttestationBeyond56Mebibytes_LeavesTheRunSignedByNoOne_AndIsNoProblemOfTheRun(int beyond, bool signed)
    {
        // §4.5, [SIG-1] (round 5): an oversized attestation.dsse.json is malformed: the run is intact and signed by no one.
        using var signer = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal().Attest(signer);
        run.WriteBytes(SealVerifierPath, Padded(run.ReadBytes(SealVerifierPath), AefLimits.MaxEnvelopeBytes + beyond));

        var verification = run.Verify(TestRun.Policy(signer));

        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        Assert.Equal(signed ? [TestRun.Alice] : [], verification.SignedBy);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ABatchSignatureBeyond56Mebibytes_LeavesItsBatchUnsigned_SoItsRedactionWithholdsNothing(int beyond, bool withheld)
    {
        // §4.5, [SIG-1], [OVL-10] (round 5): an oversized batch signature verifies for no one: its batch is unsigned, its
        // redaction is not authorized, and the deleted blob is missing. The signature itself is no problem of the chain.
        using var signer = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal();
        run.AppendBatch(
            [TestRun.Event("ov_9", "redact", new JsonObject { ["blob"] = TestRun.ReasoningSha }, edit: e => e["reason"] = "personal data")],
            signer);
        run.Delete(TestRun.ReasoningPath);
        const string signature = "overlays/seal-0001.dsse.json";
        run.WriteBytes(signature, Padded(run.ReadBytes(signature), AefLimits.MaxEnvelopeBytes + beyond));

        var verification = run.Verify(TestRun.Policy(signer, redact: true));

        Assert.Empty(run.ChainProblems());
        Assert.Equal(withheld ? (AefOutcome.Intact, 1) : (AefOutcome.Invalid, 0), (verification.Outcome, verification.Withheld));
        Assert.Contains($"{TestRun.ReasoningPath} {(withheld ? "withheld" : "missing")}", verification.Problems.Select(p => $"{p.Path} {p.Code}"));
    }

    private const string SealVerifierPath = "attestation.dsse.json";

    // A JSON document padded with trailing spaces (whitespace after the value, which JSON allows) to exactly `size` bytes.
    private static byte[] Padded(byte[] json, int size) => [.. json, .. Enumerable.Repeat((byte)' ', size - json.Length)];
}
