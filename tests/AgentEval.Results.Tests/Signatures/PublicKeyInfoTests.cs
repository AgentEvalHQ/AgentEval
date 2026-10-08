using System.Formats.Asn1;
using System.Security.Cryptography;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>SubjectPublicKeyInfo parsing and key ids ([SIG-2], [SIG-3]).</summary>
public class PublicKeyInfoTests
{
    [Theory]
    [InlineData("ecdsa-a", AefKeyAlgorithm.EcdsaP256, SignatureCorpus.EcdsaA)]
    [InlineData("ecdsa-b", AefKeyAlgorithm.EcdsaP256, SignatureCorpus.EcdsaB)]
    [InlineData("ed25519-a", AefKeyAlgorithm.Ed25519, SignatureCorpus.Ed25519A)]
    [InlineData("rsa", AefKeyAlgorithm.Unsupported, SignatureCorpus.Rsa)]
    public void TheCorpusTestKeys_HaveTheKeyIdsTheirReadmeGives(string name, AefKeyAlgorithm algorithm, string keyId)
    {
        var key = SignatureCorpus.Key(name);
        Assert.Equal(algorithm, key.Algorithm);
        Assert.Equal(keyId, key.KeyId);
        Assert.Equal(keyId, KeyId.Of(key.Der));
        Assert.Equal(algorithm == AefKeyAlgorithm.Unsupported, key.UnsupportedReason is not null);
    }

    [Theory]
    [InlineData("ecdsa-a")]
    [InlineData("ed25519-a")]
    [InlineData("rsa")]
    public void ToPem_WritesTheCorpusPemBack(string name)
    {
        var pem = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", name + ".pub.pem"));
        Assert.Equal(pem, PublicKeyInfo.FromPem(pem).ToPem());
    }

    [Fact]
    public void KeyId_IsSha256OfTheDer_InLowerCaseHex()
    {
        Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", KeyId.Of([]));
    }

    [Fact]
    public void AP256Key_EncodesAsThePlatformDoes()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var platform = PublicKeyInfo.FromDer(ecdsa.ExportSubjectPublicKeyInfo());
        var ours = PublicKeyInfo.FromP256(q.X!, q.Y!);

        Assert.Equal(AefKeyAlgorithm.EcdsaP256, platform.Algorithm);
        Assert.Equal(platform.Der.ToArray(), ours.Der.ToArray());
        Assert.Equal(platform.KeyId, ours.KeyId);
    }

    [Fact]
    public void AnEd25519Key_EncodesAsRfc8410Says()
    {
        var key = SignatureCorpus.Key("ed25519-a");
        var raw = key.Der[^32..].ToArray();
        Assert.Equal(key.Der.ToArray(), PublicKeyInfo.FromEd25519(raw).Der.ToArray());
    }

    [Fact]
    public void ACompressedP256Point_CannotBeUsed()
    {
        // [SIG-2]: a compressed point makes the key unusable; [SIG-3]: a policy holding it is refused.
        var q = UncompressedPoint();
        var compressed = Spki("1.2.840.10045.2.1", Oid("1.2.840.10045.3.1.7"), [(byte)(0x02 | (q[^1] & 1)), .. q[1..33]]);
        Assert.Contains("compressed", Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(compressed)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AP256PointOffTheCurve_OrAnUnusedBit_CannotBeUsed()
    {
        var q = UncompressedPoint();
        var off = (byte[])q.Clone();
        off[^1] ^= 1;
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Spki("1.2.840.10045.2.1", Oid("1.2.840.10045.3.1.7"), off)));
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Spki("1.2.840.10045.2.1", Oid("1.2.840.10045.3.1.7"), q[..64])));
        var even = (byte[])q.Clone();
        even[^1] &= 0xFE;   // DER leaves an unused bit zero
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Spki("1.2.840.10045.2.1", Oid("1.2.840.10045.3.1.7"), even, unusedBits: 1)));
    }

    [Fact]
    public void AnEcKeyOnAnotherCurve_IsUnsupported()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var key = PublicKeyInfo.FromDer(p384.ExportSubjectPublicKeyInfo());
        Assert.Equal(AefKeyAlgorithm.Unsupported, key.Algorithm);
        Assert.Contains("another curve", key.UnsupportedReason, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => key.Verify([], []));
    }

    [Fact]
    public void EcParametersOtherThanANamedCurve_AreUnsupported()
    {
        var q = UncompressedPoint();
        Assert.Equal(AefKeyAlgorithm.Unsupported, PublicKeyInfo.FromDer(Spki("1.2.840.10045.2.1", null, q)).Algorithm);
        Assert.Equal(AefKeyAlgorithm.Unsupported, PublicKeyInfo.FromDer(Spki("1.2.840.10045.2.1", [0x05, 0x00], q)).Algorithm);   // implicitCurve NULL
    }

    [Fact]
    public void AnEd25519KeyThatIsNoPoint_CannotBeUsed_AndOneWithParametersIsUnsupported()
    {
        var raw = SignatureCorpus.Key("ed25519-a").Der[^32..].ToArray();
        Assert.Equal(AefKeyAlgorithm.Ed25519, PublicKeyInfo.FromDer(Spki("1.3.101.112", null, raw)).Algorithm);
        Assert.Equal(AefKeyAlgorithm.Unsupported, PublicKeyInfo.FromDer(Spki("1.3.101.112", [0x05, 0x00], raw)).Algorithm);
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Spki("1.3.101.112", null, [0x02, .. new byte[31]])));   // y = 2: no point
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Spki("1.3.101.112", null, raw[..31])));
    }

    [Fact]
    public void AnEd25519KeyOfSmallOrder_CannotBeUsed()
    {
        // [SIG-2]: the neutral point decodes, but eight times it is neutral: it would verify forged signatures.
        byte[] neutral = [0x01, .. new byte[31]];
        Assert.Contains("small order", Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Spki("1.3.101.112", null, neutral))).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PublicKeyInfo.FromEd25519(neutral));
    }

    [Fact]
    public void UnusedBitsInTheKey_AreNoDer_WhateverTheAlgorithm()
    {
        // [SIG-3]: a key BIT STRING with unused bits is refused for RSA too, not read as another algorithm.
        var rsa = SignatureCorpus.Key("rsa").Der.ToArray();
        var reader = new AsnReader(rsa, AsnEncodingRules.DER).ReadSequence();
        var algorithm = reader.ReadEncodedValue().ToArray();
        var key = reader.ReadBitString(out _);
        key[^1] &= 0xFE;   // DER leaves an unused bit zero
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(algorithm);
            writer.WriteBitString(key, unusedBitCount: 1);
        }

        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(writer.Encode()));
    }

    [Fact]
    public void BerThatIsNotDer_IsNoSubjectPublicKeyInfo()
    {
        // [SIG-3]: the key id is over the DER bytes as given, never re-encoded, so BER is refused rather than re-encoded.
        var der = SignatureCorpus.Key("ecdsa-a").Der.ToArray();
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer([0x30, 0x81, .. der[1..]]));
        var rsa = SignatureCorpus.Key("rsa").Der.ToArray();
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer([0x30, 0x83, 0x00, .. rsa[2..]]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("3000")]                              // an empty SEQUENCE
    [InlineData("300506032b6570")]                    // an AlgorithmIdentifier's contents only
    [InlineData("300a300506032b65700301")]            // truncated
    [InlineData("04020000")]                          // an OCTET STRING
    public void BytesThatAreNoSubjectPublicKeyInfo_AreAFormatError(string hex)
    {
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer(Convert.FromHexString(hex)));
    }

    [Fact]
    public void ABytePastTheSubjectPublicKeyInfo_IsAFormatError()
    {
        var der = SignatureCorpus.Key("ecdsa-a").Der.ToArray();
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromDer([.. der, 0x00]));
    }

    public static TheoryData<string> BadPems()
    {
        var good = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", "ecdsa-a.pub.pem"));
        var lines = good.TrimEnd('\n').Split('\n');
        return new TheoryData<string>
        {
            "",
            "not a key",
            "text before\n" + good,
            good + "text after\n",
            good.Replace("PUBLIC KEY", "PRIVATE KEY", StringComparison.Ordinal),
            string.Join('\n', lines[0], "", lines[1], lines[2], lines[3]) + "\n",          // a blank line
            string.Join('\n', lines[0], " " + lines[1], lines[2], lines[3]) + "\n",         // an indented line
            string.Join('\n', lines[0], lines[1].Replace('/', '_').Replace('+', '-'), lines[2], lines[3]) + "\n",   // URL-safe
            string.Join('\n', lines[0], lines[1] + lines[2].TrimEnd('='), lines[3]) + "\n",                         // unpadded
            "-----BEGIN PUBLIC KEY-----\n-----END PUBLIC KEY-----\n",
            "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n",                // base64, but no SPKI
            Wrap(lines, 76),                                                                // RFC 7468: 64 characters a line
            Wrap(lines, 200),                                                               // the body on one line
            Wrap(lines, 60),                                                                // short lines
        };

        // The body rewrapped at `width` characters a line.
        static string Wrap(string[] lines, int width)
        {
            var body = string.Concat(lines[1..^1]);
            var wrapped = Enumerable.Range(0, (body.Length + width - 1) / width).Select(i => body.Substring(i * width, Math.Min(width, body.Length - (i * width))));
            return string.Join('\n', [lines[0], .. wrapped, lines[^1]]) + "\n";
        }
    }

    [Fact]
    public void APemsBase64Lines_Are64CharactersButTheLast()
    {
        var good = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", "ecdsa-a.pub.pem"));
        var lines = good.TrimEnd('\n').Split('\n');

        Assert.All(lines[1..^2], l => Assert.Equal(64, l.Length));   // the corpus key, wrapped as RFC 7468 says
        Assert.Equal(SignatureCorpus.EcdsaA, PublicKeyInfo.FromPem(good).KeyId);
        Assert.Equal(good, PublicKeyInfo.FromPem(good).ToPem());
    }

    [Theory]
    [MemberData(nameof(BadPems))]
    public void APemThatIsNotAPublicKeyBlock_IsAFormatError(string pem)
    {
        Assert.Throws<FormatException>(() => PublicKeyInfo.FromPem(pem));
    }

    [Fact]
    public void APem_MayEndInCrlfOrNoLineEnd()
    {
        var good = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", "ecdsa-a.pub.pem"));
        Assert.Equal(SignatureCorpus.EcdsaA, PublicKeyInfo.FromPem(good.Replace("\n", "\r\n", StringComparison.Ordinal)).KeyId);
        Assert.Equal(SignatureCorpus.EcdsaA, PublicKeyInfo.FromPem(good.TrimEnd('\n')).KeyId);
    }

    private static byte[] UncompressedPoint()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    private static byte[] Oid(string oid)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteObjectIdentifier(oid);
        return writer.Encode();
    }

    private static byte[] Spki(string algorithm, byte[]? parameters, byte[] key, int unusedBits = 0)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(algorithm);
                if (parameters is not null)
                {
                    writer.WriteEncodedValue(parameters);
                }
            }
            writer.WriteBitString(key, unusedBits);
        }
        return writer.Encode();
    }
}
