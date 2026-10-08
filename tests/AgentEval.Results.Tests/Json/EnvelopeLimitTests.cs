using System.Text;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Json;

/// <summary>
/// [ENC-17]: a DSSE envelope may be up to 32 MiB (it holds the seal, which lists every sealed file), above the 4 MiB of
/// other JSON files. An envelope between the two reads and verifies; one above 32 MiB is neither written nor read.
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
    public void AnEnvelopeBetween4And32Mebibytes_IsWritten_Read_AndVerified()
    {
        using var signer = EcdsaP256Signer.Generate();
        var seal = Seal(5 * 1024 * 1024);   // base64 makes the envelope about 6.7 MiB
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, seal, signer).ToJson();
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);

        Assert.InRange(envelope.Length, AefLimits.MaxJsonBytes + 1, AefLimits.MaxSealBytes);
        var result = DsseVerifier.Verify(envelope, seal, Dsse.InTotoPayloadType, policy);
        Assert.Null(result.EnvelopeResult);
        Assert.Equal(["git:test@example.com"], result.VerifiesFor);
    }

    [Fact]
    public void AnEnvelopeAbove32Mebibytes_IsNotWritten_AndReadsAsMalformed()
    {
        using var signer = EcdsaP256Signer.Generate();
        var seal = Seal(25 * 1024 * 1024);  // base64 makes the envelope about 33.3 MiB
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, seal, signer);
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);

        Assert.Throws<ArgumentException>(() => envelope.ToJson());

        // The same envelope written by hand: valid in every way but its size.
        var signature = envelope.Signatures[0];
        var bytes = Encoding.UTF8.GetBytes(
            $"{{\"payloadType\":\"{envelope.PayloadType}\",\"payload\":\"{Convert.ToBase64String(seal)}\"," +
            $"\"signatures\":[{{\"keyid\":\"{signature.KeyId}\",\"sig\":\"{Convert.ToBase64String(signature.Sig)}\"}}]}}");
        Assert.True(bytes.Length > AefLimits.MaxSealBytes);
        Assert.Equal(DsseEnvelopeResult.Malformed, DsseVerifier.Verify(bytes, seal, Dsse.InTotoPayloadType, policy).EnvelopeResult);
        Assert.True(DsseVerifier.Verify(envelope, seal, Dsse.InTotoPayloadType, policy).VerifiesForIdentity("git:test@example.com"));
    }
}
