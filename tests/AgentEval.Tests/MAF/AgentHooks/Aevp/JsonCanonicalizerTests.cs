// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.AgentHooks.Aevp;
using AgentEval.Tests.MAF.AgentHooks.Ctk;
using Xunit;

namespace AgentEval.Tests.MAF.AgentHooks.Aevp;

/// <summary>
/// An AEVP content address resolves for someone else only if both sides hash the same bytes. These pin the RFC 8785
/// (JCS) rules the profile is canonicalized with, and check them against the AGENT-HOOKS reference core.
/// </summary>
public class JsonCanonicalizerTests
{
    // The profile omits null members; serialize the same way so the only difference left is member order.
    private static readonly JsonSerializerOptions IgnoreNulls =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(-0d, "0")]
    [InlineData(1d, "1")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(4.5, "4.5")]
    [InlineData(0.002, "0.002")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.23e-18, "1.23e-18")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e30, "1e+30")]
    [InlineData(333333333.3333333, "333333333.3333333")]
    [InlineData(9007199254740992d, "9007199254740992")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(0.8125, "0.8125")]
    public void Numbers_FollowECMAScriptNumberToString(double value, string expected) =>
        Assert.Equal(expected, JsonCanonicalizer.FormatNumber(value));

    [Fact]
    public void NonFiniteNumbers_AreRefused() =>
        Assert.Throws<ArgumentException>(() => JsonCanonicalizer.FormatNumber(double.NaN));

    [Fact]
    public void TheRfc8785Example_CanonicalizesToTheRfcBytes()
    {
        // RFC 8785 §3.2.2 / Appendix: member sorting, number normalisation and string escaping together.
        const string input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        const string expected =
            """{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}""";

        Assert.Equal(expected, JsonCanonicalizer.Canonicalize(input));
    }

    [Fact]
    public void Members_SortByUtf16CodeUnits_NotDeclarationOrder()
    {
        // Ordinal UTF-16 order: "B" (0x42) < "a" (0x61) < "€" (0x20AC) < "😀" (surrogate pair starting 0xD83D).
        Assert.Equal("""{"B":1,"a":2,"€":3,"😀":4}""",
            JsonCanonicalizer.Canonicalize("""{"😀":4,"a":2,"€":3,"B":1}"""));
    }

    [Fact]
    public void TheProfile_HashesItsCanonicalBytes_SoDeclarationOrderCannotMoveTheAddress()
    {
        var profile = new AgentEvidenceProfile
        {
            Evaluated = true,
            EnforcementCapability = AevpEnforcementCapability.Transform,
            EvidenceTier = EvidenceTier.IntentToAct,
            Judge = new JudgeIdentity("ForbiddenTool"),
            Calibration = new CalibrationEvidence(0.8125, 0.9, 0, 46, true),
        };

        var canonical = profile.ToCanonicalJson();

        Assert.Equal(canonical, JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(profile, IgnoreNulls)));
        Assert.StartsWith("""{"aevp":"aevp/0.1","calibration":{""", canonical, StringComparison.Ordinal);
        Assert.Equal(AgentEvidenceProfile.AddressOf(canonical), profile.ToContentAddress());
    }

    [Fact]
    public void OurCanonicalBytes_MatchTheReferenceCore()
    {
        Assert.True(CtkNative.IsAvailable,
            "agent_hooks_ffi did not load; the reference core is the oracle for this check and without it it measures nothing.");

        var profile = new AgentEvidenceProfile
        {
            Evaluated = true,
            EnforcementCapability = AevpEnforcementCapability.Block,
            EvidenceTier = EvidenceTier.Behavioral,
            Judge = new JudgeIdentity("judge:over-refusal", "1.0", "zai-org/GLM-5.3-Flash"),
            Calibration = new CalibrationEvidence(0.73, 0.875, 1, 46, true),
        };

        // The core canonicalizes the serializer's (declaration-order) output; it must land on our exact bytes.
        Assert.Equal(CtkNative.CanonicalJson(JsonSerializer.Serialize(profile, IgnoreNulls)), profile.ToCanonicalJson());
    }
}
