using System.Security.Cryptography;
using System.Text;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>Verifying envelopes ([SIG-5]) and signing them with <see cref="EcdsaP256Signer"/>.</summary>
public class DsseVerifierTests
{
    private static readonly byte[] Seal = Encoding.UTF8.GetBytes("""{"_type":"https://in-toto.io/Statement/v1","note":"ünïcode, and a CRLF\r\n"}""");

    [Fact]
    public void SignThenVerify_WithEcdsaP256Signer()
    {
        using var signer = EcdsaP256Signer.Generate();
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, Seal, signer);
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);

        var result = DsseVerifier.Verify(envelope.ToJson(), Seal, Dsse.InTotoPayloadType, policy);

        Assert.Null(result.EnvelopeResult);
        Assert.Equal([new DsseSignatureCheck(signer.KeyId, DsseSignatureResult.Verified, "git:test@example.com")], result.Signatures);
        Assert.Equal(["git:test@example.com"], result.VerifiesFor);
        Assert.True(result.VerifiesForIdentity("git:test@example.com"));
    }

    [Fact]
    public void TheSignersDer_IsStrictAndMinimal_ForEveryShapeOfROrS()
    {
        // Enough signatures that r and s take every DER shape: a sign byte before a high bit, and (now and then) fewer
        // than 32 significant bytes.
        using var signer = EcdsaP256Signer.Generate();
        var rs = new byte[64];
        for (var i = 0; i < 200; i++)
        {
            var message = BitConverter.GetBytes(i);
            var der = signer.Sign(message);
            Assert.True(EcdsaP256.TryDecodeSignature(der, rs));
            Assert.Equal(der, EcdsaP256.EncodeSignature(rs));
            Assert.True(signer.PublicKey.Verify(message, der));
        }
    }

    [Fact]
    public void TheSignersKeyId_IsThatOfThePlatformsSubjectPublicKeyInfo()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = new EcdsaP256Signer(ecdsa);
        Assert.Equal(KeyId.Of(ecdsa.ExportSubjectPublicKeyInfo()), signer.KeyId);
        Assert.Equal(signer.PublicKey.KeyId, signer.KeyId);
    }

    [Fact]
    public void ASignerFromPem_SignsWhatItsPublicKeyVerifies()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = EcdsaP256Signer.FromPem(ecdsa.ExportPkcs8PrivateKeyPem());
        Assert.True(PublicKeyInfo.FromDer(ecdsa.ExportSubjectPublicKeyInfo()).Verify("m"u8, signer.Sign("m"u8)));
    }

    [Fact]
    public void ASignerRefusesAKeyNotOnP256()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => new EcdsaP256Signer(p384));
    }

    [Fact]
    public void ASignatureWithoutKeyId_IsTriedInPolicyOrder_AndNamedByTheFirstKeyThatVerifies()
    {
        using var first = EcdsaP256Signer.Generate();
        using var second = EcdsaP256Signer.Generate();
        var envelope = Unnamed(DsseEnvelope.Create(Dsse.InTotoPayloadType, Seal, second));
        var policy = new TrustPolicy(
        [
            new TrustedKey("git:first@example.com", first.PublicKey),
            new TrustedKey("git:second@example.com", second.PublicKey),
        ]);

        var result = DsseVerifier.Verify(envelope, Seal, Dsse.InTotoPayloadType, policy);
        Assert.Equal([new DsseSignatureCheck(second.KeyId, DsseSignatureResult.Verified, "git:second@example.com")], result.Signatures);

        var untrusted = DsseVerifier.Verify(envelope, Seal, Dsse.InTotoPayloadType, new TrustPolicy([new TrustedKey("git:first@example.com", first.PublicKey)]));
        Assert.Equal([new DsseSignatureCheck("", DsseSignatureResult.UntrustedKey, null)], untrusted.Signatures);
        Assert.Empty(untrusted.VerifiesFor);
    }

    [Fact]
    public void AKeyOfAnotherAlgorithm_IsSkipped_ForASignatureWithoutKeyId()
    {
        // [SIG-5]: a signature without a key id that no key verifies is untrusted-key under the empty key id, even when
        // a policy key it could not be checked against is of an unsupported algorithm.
        using var signer = EcdsaP256Signer.Generate();
        var envelope = Unnamed(DsseEnvelope.Create(Dsse.InTotoPayloadType, Seal, signer));
        var policy = new TrustPolicy([new TrustedKey("git:rsa@example.com", SignatureCorpus.Key("rsa"))]);

        Assert.Equal([new DsseSignatureCheck("", DsseSignatureResult.UntrustedKey, null)],
            DsseVerifier.Verify(envelope, Seal, Dsse.InTotoPayloadType, policy).Signatures);
    }

    [Fact]
    public void VerifiesFor_IsInPolicyOrder_EachIdentityOnce()
    {
        using var a1 = EcdsaP256Signer.Generate();
        using var b = EcdsaP256Signer.Generate();
        using var a2 = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy(
        [
            new TrustedKey("git:a@example.com", a1.PublicKey),
            new TrustedKey("git:b@example.com", b.PublicKey),
            new TrustedKey("git:a@example.com", a2.PublicKey),
        ]);
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, Seal, a2, b, a1);

        var result = DsseVerifier.Verify(envelope, Seal, Dsse.InTotoPayloadType, policy);

        Assert.Equal([a2.KeyId, b.KeyId, a1.KeyId], result.Signatures.Select(s => s.KeyId));
        Assert.All(result.Signatures, s => Assert.Equal(DsseSignatureResult.Verified, s.Result));
        Assert.Equal(["git:a@example.com", "git:b@example.com"], result.VerifiesFor);
    }

    [Fact]
    public void AKeyListedTwice_IsNoPolicy()
    {
        // [SIG-3]: a policy in which two keys have one key id is refused, so no signature is ever checked against it.
        using var signer = EcdsaP256Signer.Generate();
        Assert.Throws<ArgumentException>(() => new TrustPolicy(
        [
            new TrustedKey("git:first@example.com", signer.PublicKey),
            new TrustedKey("git:second@example.com", signer.PublicKey),
        ]));
    }

    [Fact]
    public void VerifiesFor_FollowsThePolicyPositionOfTheFirstKeyThatVerifiedForEachIdentity()
    {
        // [SIG-5]: alice holds keys at positions 0 and 2, bob at 1. Signed by alice's second key and bob's: alice's
        // first verifying key is at position 2, after bob's, so bob comes first.
        using var alice1 = EcdsaP256Signer.Generate();
        using var bob = EcdsaP256Signer.Generate();
        using var alice2 = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy(
        [
            new TrustedKey("git:alice@example.com", alice1.PublicKey),
            new TrustedKey("git:bob@example.com", bob.PublicKey),
            new TrustedKey("git:alice@example.com", alice2.PublicKey),
        ]);

        var result = DsseVerifier.Verify(DsseEnvelope.Create(Dsse.InTotoPayloadType, Seal, alice2, bob), Seal, Dsse.InTotoPayloadType, policy);
        Assert.Equal(["git:bob@example.com", "git:alice@example.com"], result.VerifiesFor);
    }

    [Fact]
    public void APayloadMismatch_StillReportsEachSignature_ButVerifiesForNoOne()
    {
        using var signer = EcdsaP256Signer.Generate();
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, Seal, signer);
        var policy = new TrustPolicy([new TrustedKey("git:test@example.com", signer.PublicKey)]);

        foreach (var (file, type) in new (byte[] File, string Type)[] { ([.. Seal, (byte)'\n'], Dsse.InTotoPayloadType), (Seal, Dsse.CheckpointPayloadType) })
        {
            var result = DsseVerifier.Verify(envelope, file, type, policy);
            Assert.Equal(DsseEnvelopeResult.PayloadMismatch, result.EnvelopeResult);
            Assert.Equal(DsseSignatureResult.Verified, Assert.Single(result.Signatures).Result);
            Assert.Empty(result.VerifiesFor);
            Assert.NotNull(result.Why);
        }
    }

    [Fact]
    public void AMalformedEnvelope_HasNoSignatureResults()
    {
        var result = DsseVerifier.Verify("{}"u8, Seal, Dsse.InTotoPayloadType, TrustPolicy.Empty);
        Assert.Equal(DsseEnvelopeResult.Malformed, result.EnvelopeResult);
        Assert.Empty(result.Signatures);
        Assert.Empty(result.VerifiesFor);
        Assert.NotNull(result.Why);
    }

    [Fact]
    public void TheWireNames_AreThoseOfSig5()
    {
        Assert.Equal(["verified", "untrusted-key", "invalid", "unsupported-algorithm"],
            Enum.GetValues<DsseSignatureResult>().Select(r => DsseVerifier.Name(r)));
        Assert.Equal(["malformed", "payload-mismatch"], Enum.GetValues<DsseEnvelopeResult>().Select(r => DsseVerifier.Name(r)));
    }

    /// <summary>The envelope with every signature's key id removed.</summary>
    private static DsseEnvelope Unnamed(DsseEnvelope envelope) =>
        new(envelope.PayloadType, envelope.Payload, [.. envelope.Signatures.Select(s => new DsseSignature("", s.Sig))]);
}
