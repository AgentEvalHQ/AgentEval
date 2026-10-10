using System.Numerics;
using System.Security.Cryptography;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>ECDSA P-256 as [SIG-2] has it: a DER <c>SEQUENCE { r, s }</c>, read strictly; no low-S rule.</summary>
public class EcdsaP256Tests
{
    private static readonly BigInteger N = BigInteger.Parse("0ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture); // DevSkim: ignore DS173237 — public curve constant

    private static readonly PublicKeyInfo KeyA = SignatureCorpus.Key("ecdsa-a");
    private static readonly DsseEnvelope Valid = SignatureCorpus.Envelope("ecdsa-valid");
    private static readonly byte[] Message = Valid.PreAuthenticationEncoding();
    private static readonly byte[] Der = Valid.Signatures[0].Sig.ToArray();

    [Fact]
    public void TheCorpusSignature_Verifies_AndIsMinimalDer()
    {
        Assert.Equal(AefKeyAlgorithm.EcdsaP256, KeyA.Algorithm);
        Assert.True(KeyA.Verify(Message, Der));

        var rs = new byte[64];
        Assert.True(EcdsaP256.TryDecodeSignature(Der, rs));
        Assert.Equal(Der, EcdsaP256.EncodeSignature(rs));
    }

    [Fact]
    public void NMinusS_Verifies_Too()
    {
        // No low-S rule ([SIG-2]): (r, n - s) is as valid as (r, s).
        var (r, s) = Scalars(Der);
        var other = Sequence(Integer(r), Integer(N - s));

        Assert.True(KeyA.Verify(Message, other));
        Assert.True(KeyA.Verify(SignatureCorpus.Envelope("ecdsa-other-s").PreAuthenticationEncoding(), SignatureCorpus.Envelope("ecdsa-other-s").Signatures[0].Sig));
    }

    public static TheoryData<string> Variants => new()
    {
        "raw r‖s",
        "an INTEGER with a needless leading zero",
        "a long-form length that fits the short form",
        "a byte after the SEQUENCE",
        "a third INTEGER",
        "only one INTEGER",
        "an indefinite length (BER)",
        "a SET, not a SEQUENCE",
        "a negative r",
        "r = 0",
        "s = n",
        "s = n + 1",
        "r of 33 significant bytes",
        "empty",
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void ANonStrictEncodingOfTheSameSignature_IsRefused(string variant)
    {
        var (r, s) = Scalars(Der);
        byte[] bytes = variant switch
        {
            "raw r‖s" => [.. Fixed(r), .. Fixed(s)],
            "an INTEGER with a needless leading zero" => Sequence([0x02, (byte)(Minimal(r).Length + 1), 0x00, .. Minimal(r)], Integer(s)),
            "a long-form length that fits the short form" => [0x30, 0x81, .. Der[1..]],
            "a byte after the SEQUENCE" => [.. Der, 0x00],
            "a third INTEGER" => Sequence(Integer(r), Integer(s), Integer(1)),
            "only one INTEGER" => Sequence(Integer(r)),
            "an indefinite length (BER)" => [0x30, 0x80, .. Der[2..], 0x00, 0x00],
            "a SET, not a SEQUENCE" => [0x31, .. Der[1..]],
            "a negative r" => Sequence([0x02, 0x01, 0xFF], Integer(s)),
            "r = 0" => Sequence(Integer(0), Integer(s)),
            "s = n" => Sequence(Integer(r), Integer(N)),
            "s = n + 1" => Sequence(Integer(r), Integer(N + 1)),
            "r of 33 significant bytes" => Sequence(Integer(r + (BigInteger.One << 256)), Integer(s)),
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        Assert.False(EcdsaP256.TryDecodeSignature(bytes, new byte[64]), variant);
        Assert.False(KeyA.Verify(Message, bytes), variant);
    }

    [Fact]
    public void AHighBitScalar_TakesItsSignByte_AndIsRead()
    {
        // n - 1 begins with 0xFF: minimal DER writes it as 00 FF … (33 content bytes), which is valid.
        var der = Sequence(Integer(N - 1), Integer(N - 1));
        var rs = new byte[64];

        Assert.Equal(33, der[3]);
        Assert.True(EcdsaP256.TryDecodeSignature(der, rs));
        Assert.Equal(Fixed(N - 1), rs[..32]);
        Assert.Equal(der, EcdsaP256.EncodeSignature(rs));
    }

    [Fact]
    public void AChangedSignatureOrMessage_DoesNotVerify()
    {
        var (r, s) = Scalars(Der);
        Assert.False(KeyA.Verify(Message, Sequence(Integer(r), Integer(s + 1))));
        Assert.False(KeyA.Verify([.. Message, 0x20], Der));
        Assert.False(SignatureCorpus.Key("ecdsa-b").Verify(Message, Der));
    }

    [Fact]
    public void IsOnCurve_TakesTheBasePoint_AndRefusesItsNeighbour()
    {
        var gx = Convert.FromHexString("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"); // DevSkim: ignore DS173237 — public base point
        var gy = Convert.FromHexString("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5"); // DevSkim: ignore DS173237 — public base point
        var gyPlusOne = (byte[])gy.Clone();
        gyPlusOne[^1]++;

        Assert.True(EcdsaP256.IsOnCurve(gx, gy));
        Assert.False(EcdsaP256.IsOnCurve(gx, gyPlusOne));
        Assert.True(EcdsaP256.IsValidPublicKey([0x04, .. gx, .. gy]));
        Assert.False(EcdsaP256.IsValidPublicKey([0x02, .. gx]));
        Assert.False(EcdsaP256.IsValidPublicKey([0x04, .. gx, .. gyPlusOne]));
    }

    [Fact]
    public void TheCurveConstants_AreThePlatforms()
    {
        // The managed on-curve check must agree with the curve the platform verifies on.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); // DevSkim: ignore DS440100 — the curve under test
        var q = key.ExportParameters(false).Q;
        Assert.True(EcdsaP256.IsOnCurve(q.X!, q.Y!));
    }

    private static (BigInteger R, BigInteger S) Scalars(byte[] der)
    {
        var rs = new byte[64];
        Assert.True(EcdsaP256.TryDecodeSignature(der, rs));
        return (new BigInteger(rs.AsSpan(0, 32), isUnsigned: true, isBigEndian: true), new BigInteger(rs.AsSpan(32), isUnsigned: true, isBigEndian: true));
    }

    /// <summary>The minimal two's-complement contents of a non-negative INTEGER.</summary>
    private static byte[] Minimal(BigInteger value) => value.ToByteArray(isUnsigned: false, isBigEndian: true);

    private static byte[] Integer(BigInteger value)
    {
        var contents = Minimal(value);
        return [0x02, (byte)contents.Length, .. contents];
    }

    private static byte[] Fixed(BigInteger value)
    {
        var bytes = new byte[32];
        Assert.True(value.TryWriteBytes(bytes.AsSpan(32 - value.GetByteCount(isUnsigned: true)), out _, isUnsigned: true, isBigEndian: true));
        return bytes;
    }

    private static byte[] Sequence(params byte[][] elements)
    {
        var body = elements.SelectMany(e => e).ToArray();
        return [0x30, (byte)body.Length, .. body];
    }
}
