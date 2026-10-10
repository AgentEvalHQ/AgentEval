// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Schemas;

namespace AgentEval.Results.Adapters.Tests;

/// <summary>
/// [ENC-13]: how the converters derive a typed reference from free text, so two writers that derive a ref from one name
/// write the same bytes (<c>subject.ref</c> is a comparability axis, [LANE-6]).
/// </summary>
public sealed class AefConverterTests
{
    [Theory]
    [InlineData("Support Agent", "agent:Support%20Agent")]
    [InlineData("café", "agent:caf%C3%A9")]
    [InlineData("50%", "agent:50%25")]
    [InlineData("", "agent:-")]          // an empty name is -
    [InlineData("-", "agent:%2D")]       // and a name that is exactly - is %2D, so the two stay apart (round 5)
    [InlineData("--", "agent:--")]       // only a name that is exactly - is
    [InlineData(" ", "agent:%20")]
    [InlineData("support/triage", "agent:support/triage")]
    public void ANameIsPercentEncoded_AsEnc13Says(string name, string expected)
    {
        var written = AefConverter.TypedRef("agent", name);

        Assert.Equal(expected, written);
        Assert.True(AefSchemas.Writer.IsValid("common#/$defs/ref", System.Text.Json.Nodes.JsonValue.Create(written)), written);
    }

    [Fact]
    public void ALongName_IsCutTo239_ThenTildeAnd16HexOfItsSha256()
    {
        var name = new string('a', 300);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();

        var written = AefConverter.TypedRef("suite", name);

        Assert.Equal($"suite:{new string('a', 239)}~{hash}", written);
        Assert.Equal(256, written.Length - "suite:".Length);
        Assert.NotEqual(written, AefConverter.TypedRef("suite", name + "b"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("labels")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]   // 32 characters: the most a kind has
    public void AKindOfUpTo32Characters_IsAccepted(string kind)
    {
        Assert.StartsWith(kind + ":", AefConverter.TypedRef(kind, "x"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Agent")]
    [InlineData("1agent")]
    [InlineData("agent_x")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]   // 33 characters (common schema, round 5)
    public void AKindTheCommonSchemaRefuses_IsAnArgumentException(string kind)
    {
        Assert.Throws<ArgumentException>(() => AefConverter.TypedRef(kind, "x"));
    }
}
