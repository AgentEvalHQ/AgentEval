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
    public void AMemberThisVersionDoesNotKnow_RefusesThePolicyAsAWhole_AndAnEmptyPolicyTrustsNothing()
    {
        // [SIG-4], [VER-9] (round 4): a trust policy is closed for readers too: a verifier cannot honour a restriction it
        // does not know. A key id is computed from the key, never read, so a policy that writes one is refused too.
        Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(
            new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["identity"] = "x", ["publicKey"] = PemA, ["keyid"] = "sha256:00" }) }.ToJsonString())));
        Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(
            new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["identity"] = "x", ["publicKey"] = PemA }), ["note"] = 1 }.ToJsonString())));
        Assert.Equal(SignatureCorpus.EcdsaA, Assert.Single(TrustPolicy.Parse(Policy(new JsonObject { ["identity"] = "x", ["publicKey"] = PemA })).Keys).KeyId);
        Assert.Empty(TrustPolicy.Parse("""{"keys":[]}"""u8).Keys);
        Assert.Empty(TrustPolicy.Empty.Keys);
    }

    [Theory]
    [InlineData("\"1.0\"", true)]     // this version
    [InlineData("\"1.1\"", false)]    // a later minor: refused, as a member this version does not know is
    [InlineData("\"2.0\"", false)]
    [InlineData("\"1\"", false)]
    [InlineData("\"1.00\"", false)]   // the schema's const is the string 1.0
    [InlineData("1.0", false)]        // a number
    [InlineData("null", false)]
    public void ADeclaredSchemaVersion_IsAcceptedOnlyWhenItIs10(string schemaVersion, bool accepted)
    {
        // [SIG-4] (round 5): a policy may carry schemaVersion 1.0; one that declares a later version is refused.
        var json = $$"""{"schemaVersion":{{schemaVersion}},"keys":[{"identity":"x","publicKey":{{JsonValue.Create(PemA).ToJsonString()}}}]}""";
        if (accepted)
        {
            Assert.Equal(SignatureCorpus.EcdsaA, Assert.Single(TrustPolicy.Parse(Encoding.UTF8.GetBytes(json)).Keys).KeyId);
        }
        else
        {
            Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [Fact]
    public void KeylessSigning_IsNotPartOf10sPolicy_AndAPolicyUsingItIsRefused()
    {
        // [SIG-4] (round 5): a later minor may add keyless signing (Sigstore) to the policy; a 1.0 verifier refuses a policy
        // that uses it, at the top level or on a key.
        var pem = JsonValue.Create(PemA).ToJsonString();
        foreach (var json in new[]
                 {
                     $$"""{"schemaVersion":"1.0","keys":[{"identity":"x","publicKey":{{pem}}}],"keyless":[{"issuer":"https://token.actions.githubusercontent.com","subject":"repo:o/r"}]}""",
                     $$"""{"keys":[{"identity":"oidc:issuer/subject","certificateIdentity":{"issuer":"i","subject":"s"},"publicKey":{{pem}}}]}""",
                 })
        {
            Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [Theory]
    [InlineData("""["redact","redact"]""")]   // a value twice
    [InlineData("""[""]""")]                   // an empty value
    public void AMayTheSchemaRefuses_RefusesThePolicy(string may)
    {
        var json = $$"""{"keys":[{"identity":"x","publicKey":{{JsonValue.Create(PemA).ToJsonString()}},"may":{{may}}}]}""";
        Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void AnEmptyIdentity_RefusesThePolicy()
    {
        Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Policy(new JsonObject { ["identity"] = "", ["publicKey"] = PemA })));
    }

    [Fact]
    public void AMayValueThisVersionDoesNotKnow_GrantsNothing_AndIsMatchedByExactValue()
    {
        // [SIG-4] (round 4): an unknown value is kept, and grants nothing; "redact" is matched exactly, never by case or prefix.
        var policy = TrustPolicy.Parse(Policy(
            new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemA, ["may"] = new JsonArray("never-redact", "Redact", "redact ") },
            new JsonObject { ["identity"] = "git:bob@example.com", ["publicKey"] = PemB, ["may"] = new JsonArray("approve-releases", "redact") }));

        Assert.False(policy.Allows("git:alice@example.com", TrustPolicy.Redact));
        Assert.Equal(["never-redact", "Redact", "redact "], policy.Keys[0].May);
        Assert.True(policy.Allows("git:bob@example.com", TrustPolicy.Redact));
        Assert.False(policy.Allows("git:bob@example.com", "approve"));
    }

    [Fact]
    public void APolicyBuiltInCode_IsHeldToTheSchemaToo()
    {
        var key = SignatureCorpus.Key("ecdsa-a");
        Assert.Throws<ArgumentException>(() => new TrustPolicy([new TrustedKey("", key)]));
        Assert.Throws<ArgumentException>(() => new TrustPolicy([new TrustedKey("a", key, ["redact", "redact"])]));
        Assert.Throws<ArgumentException>(() => new TrustPolicy([new TrustedKey("a", key, [""])]));

        var policy = new TrustPolicy([new TrustedKey("a", key, [TrustPolicy.Redact])]);
        Assert.Equal(policy.Keys[0].KeyId, TrustPolicy.Parse(Encoding.UTF8.GetBytes(policy.ToJson().ToJsonString())).Keys[0].KeyId);
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

    [Fact]
    public void APolicyBeyondTheLimitsOfAJsonFile_IsRefusedAsAWhole()
    {
        // [SIG-4] (R4N-10): a trust policy is a JSON document within [ENC-17]'s limits for a JSON file: 4 MiB, 64 deep. A
        // valid policy padded with whitespace to exactly 4 MiB is read; one byte more refuses it as a whole.
        var policy = Policy(new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = PemA });
        byte[] AtSize(int size) => [.. policy, .. Enumerable.Repeat((byte)' ', size - policy.Length)];

        Assert.Single(TrustPolicy.Parse(AtSize(AefLimits.MaxJsonBytes)).Keys);
        var e = Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(AtSize(AefLimits.MaxJsonBytes + 1)));
        Assert.Contains("limit", e.Message, StringComparison.Ordinal);

        var deep = $$"""{"keys":[{"identity":"x","publicKey":{{JsonValue.Create(PemA).ToJsonString()}}}],"x":{{new string('[', 64)}}{{new string(']', 64)}}}""";
        Assert.Contains("limit", Assert.Throws<TrustPolicyException>(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(deep))).Message, StringComparison.Ordinal);
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
