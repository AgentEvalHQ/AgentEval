using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>The trust policy file of spec 09 §9.2.1 ([SIG-4]).</summary>
public class TrustPolicyTests
{
    private static readonly string PemA = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", "ecdsa-a.pub.pem"));
    private static readonly string PemB = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", "ecdsa-b.pub.pem"));
    private static readonly string PemEd = File.ReadAllText(Path.Combine(SignatureCorpus.Root, "keys", "ed25519-a.pub.pem"));

    [Fact]
    public void ACorpusPolicy_IsReadInPolicyOrder_WithKeyIdsComputedFromTheKeys()
    {
        var policy = TrustPolicy.Parse(SignatureCorpus.Read("verifies-for-two", "policy.json"));

        Assert.Equal(["git:alice@example.com", "spiffe://example.com/ci/sealer"], policy.Keys.Select(k => k.Identity));
        Assert.Equal([SignatureCorpus.EcdsaA, SignatureCorpus.Ed25519A], policy.Keys.Select(k => k.KeyId));
        Assert.Equal([AefKeyAlgorithm.EcdsaP256, AefKeyAlgorithm.Ed25519], policy.Keys.Select(k => k.PublicKey.Algorithm));
        Assert.Same(policy.Keys[1], policy.Find(SignatureCorpus.Ed25519A));
        Assert.Null(policy.Find(SignatureCorpus.EcdsaB));
    }

    [Fact]
    public void AKeyOfAnotherAlgorithm_IsKept_AsUnsupported()
    {
        var policy = TrustPolicy.Parse(SignatureCorpus.Read("unsupported-algorithm", "policy.json"));
        Assert.Equal(AefKeyAlgorithm.Unsupported, policy.Keys[0].PublicKey.Algorithm);
        Assert.Equal(SignatureCorpus.Rsa, policy.Keys[0].KeyId);
    }

    [Fact]
    public void May_GrantsActionsPerIdentity()
    {
        var policy = TrustPolicy.Parse(Policy(
            new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemA, ["may"] = new JsonArray("redact") },
            new JsonObject { ["identity"] = "git:bob@example.com", ["publicKey"] = PemB }));

        Assert.True(policy.Allows("git:alice@example.com", TrustPolicy.Redact));
        Assert.False(policy.Allows("git:bob@example.com", TrustPolicy.Redact));
        Assert.False(policy.Allows("git:carol@example.com", TrustPolicy.Redact));
        Assert.Equal(["redact"], policy.Keys[0].May);
        Assert.Empty(policy.Keys[1].May);
    }

    [Fact]
    public void AnIdentityWithSeveralKeys_MayWhatAnyOfThemMay()
    {
        // [SIG-4]: a rotation; what the identity may do is the union of its keys' "may".
        var policy = TrustPolicy.Parse(Policy(
            new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemA },
            new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemEd, ["may"] = new JsonArray("redact") }));

        Assert.Equal(2, policy.Keys.Count);
        Assert.True(policy.Allows("git:alice@example.com", TrustPolicy.Redact));
    }

    [Fact]
    public void AKeyListedTwice_RefusesThePolicy_EvenUnderOneIdentity()
    {
        // [SIG-3]: two keys of a policy never have one key id.
        foreach (var second in new[] { "git:bob@example.com", "git:alice@example.com" })
        {
            var e = Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Policy(
                new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemA },
                new JsonObject { ["identity"] = second, ["publicKey"] = PemA })));
            Assert.Contains(SignatureCorpus.EcdsaA, e.Message, StringComparison.Ordinal);
        }

        var key = SignatureCorpus.Key("ecdsa-a");
        Assert.Throws<ArgumentException>(() => new TrustPolicy([new TrustedKey("a", key), new TrustedKey("b", key)]));
    }

    [Fact]
    public void MembersTheFormatDoesNotDefine_AreIgnored_AndAnEmptyPolicyTrustsNothing()
    {
        var policy = TrustPolicy.Parse(Encoding.UTF8.GetBytes(
            new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["identity"] = "x", ["publicKey"] = PemA, ["keyid"] = "sha256:00" }), ["note"] = 1 }.ToJsonString()));
        Assert.Equal(SignatureCorpus.EcdsaA, Assert.Single(policy.Keys).KeyId);   // the key id is computed, never read
        Assert.Empty(TrustPolicy.Parse("""{"keys":[]}"""u8).Keys);
        Assert.Empty(TrustPolicy.Empty.Keys);
    }

    public static TheoryData<string> NotPolicies() => new()
    {
        "",
        "[]",
        "{}",
        """{"keys":{}}""",
        """{"keys":[1]}""",
        """{"keys":[{"publicKey":"x"}]}""",
        """{"keys":[{"identity":"x"}]}""",
        """{"keys":[{"identity":7,"publicKey":"x"}]}""",
        """{"keys":[{"identity":"x","publicKey":"not a pem"}]}""",
        """{"keys":[],"keys":[]}""",
    };

    [Theory]
    [MemberData(nameof(NotPolicies))]
    public void ABrokenPolicy_IsATrustPolicyException(string json)
    {
        Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("\"redact\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    public void AMayThatIsNotAnArrayOfStrings_IsATrustPolicyException(string may)
    {
        var json = $$"""{"keys":[{"identity":"x","publicKey":{{JsonValue.Create(PemA).ToJsonString()}},"may":{{may}}}]}""";
        Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json)));
    }

    public static TheoryData<string> UnusableKeys() =>
        ["compressed P-256", "P-256 off the curve", "Ed25519 that is no point", "Ed25519 of small order", "BER, not DER", "RSA with an unused bit"];

    [Theory]
    [MemberData(nameof(UnusableKeys))]
    public void AKeyThatCannotBeUsed_RefusesThePolicyAsAWhole(string which)
    {
        // [SIG-3]: a verifier does not verify against part of a policy, even when another key of it would verify.
        var good = SignatureCorpus.Key("ecdsa-a").Der.ToArray();
        var point = good[^64..];
        byte[] bad = which switch
        {
            "compressed P-256" => [0x30, 0x39, .. good[2..23], 0x03, 0x22, 0x00, (byte)(0x02 | (point[^1] & 1)), .. point[..32]],
            "P-256 off the curve" => [.. good[..^1], (byte)(good[^1] ^ 1)],
            "Ed25519 that is no point" => [.. Convert.FromHexString("302a300506032b6570032100"), 0x02, .. new byte[31]],
            "Ed25519 of small order" => [.. Convert.FromHexString("302a300506032b6570032100"), 0x01, .. new byte[31]],
            "BER, not DER" => [0x30, 0x81, .. good[1..]],
            "RSA with an unused bit" => RsaWithAnUnusedBit(),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        var policy = Policy(
            new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemA },
            new JsonObject { ["identity"] = "git:carol@example.com", ["publicKey"] = Pem(bad) });
        var e = Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(policy));
        Assert.Contains("keys[1]", e.Message, StringComparison.Ordinal);
    }

    /// <summary>The corpus RSA key with its BIT STRING's unused-bit count set to 1 (the bit itself zero, as DER asks).</summary>
    private static byte[] RsaWithAnUnusedBit()
    {
        var der = SignatureCorpus.Key("rsa").Der.ToArray();
        // 30 82 01 22 | 30 0d <rsaEncryption, NULL> | 03 82 01 0f 00 <key>: the unused-bit count is the byte after the BIT STRING's length.
        Assert.Equal(0x03, der[19]);
        Assert.Equal(0x00, der[23]);
        der[^1] &= 0xFE;
        der[23] = 0x01;
        return der;
    }

    private static string Pem(byte[] der)
    {
        var base64 = Convert.ToBase64String(der);
        var lines = Enumerable.Range(0, (base64.Length + 63) / 64).Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64)));
        return "-----BEGIN PUBLIC KEY-----\n" + string.Join('\n', lines) + "\n-----END PUBLIC KEY-----\n";
    }

    private static byte[] Policy(params JsonObject[] keys) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray([.. keys]) }.ToJsonString());
}
