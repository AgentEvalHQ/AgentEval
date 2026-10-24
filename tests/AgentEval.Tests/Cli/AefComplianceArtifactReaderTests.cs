// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Json;

namespace AgentEval.Tests.Cli;

public sealed class AefComplianceArtifactReaderTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_NativeArtifact_PreservesCapturedDocumentAndExplicitAbsence(bool capture)
    {
        using var fixture = new ComplianceRenderCommandTests();
        var expected = ComplianceRenderCommandTests.MakeFixtureEvidence("ReaderAgent");
        var directory = fixture.WriteNativeEvidence(expected, capture);
        var result = new AefComplianceArtifactReader().Read(directory);
        Assert.Equal("intact", result.Run.Integrity);
        Assert.False(result.Run.Imported);
        Assert.Equal("not_checked", result.Run.SignatureTrust);
        Assert.Equal(capture ? "on" : "off", result.Run.ContentCapture);
        if (!capture) { Assert.Empty(result.Documents); return; }
        var artifact = Assert.Single(result.Documents);
        Assert.True(artifact.IsRich);
        var actual = artifact.Document.Deserialize<GdprComplianceEvidence>(Json)!;
        Assert.Equal(expected.CompositeTree.Score, actual.CompositeTree.Score);
        Assert.Equal(expected.Disclaimer, actual.Disclaimer);
        Assert.Equal(expected.Base.SourceRun, actual.Base.SourceRun);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("array")]
    [InlineData("null-base")]
    [InlineData("subject")]
    [InlineData("tree")]
    [InlineData("summary")]
    [InlineData("controls")]
    [InlineData("kind")]
    [InlineData("missing-kind")]
    [InlineData("numeric-kind")]
    [InlineData("missing-count")]
    public void Read_ValidSealButMalformedArtifact_Refuses(string damage)
    {
        using var fixture = new ComplianceRenderCommandTests();
        var expected = ComplianceRenderCommandTests.MakeFixtureEvidence("ReaderAgent");
        var node = JsonSerializer.SerializeToNode(expected, Json)!;
        switch (damage)
        {
            case "subject": node["base"]!["subject"]!["name"] = "OtherAgent"; break;
            case "null-base": node["base"] = null; break;
            case "tree": node["compositeTree"] = null; break;
            case "summary": node["summary"] = null; break;
            case "controls": node["base"]!["controls"] = null; break;
            case "kind": node["base"]!["subject"]!["kind"] = "workflow"; break;
            case "missing-kind": node["base"]!["subject"]!.AsObject().Remove("kind"); break;
            case "numeric-kind": node["base"]!["subject"]!["kind"] = 999; break;
            case "missing-count": node["base"]!["summary"]!.AsObject().Remove("failed"); break;
        }
        var bytes = Encoding.UTF8.GetBytes(damage switch { "json" => "{", "array" => "[]", _ => node.ToJsonString() });
        var directory = fixture.WriteNativeEvidence(expected, true, bytes);
        Assert.Throws<InvalidDataException>(() => new AefComplianceArtifactReader().Read(directory));
    }

    [Theory]
    [InlineData("same-json")]
    [InlineData("conflicting-rich")]
    [InlineData("generic-and-rich")]
    [InlineData("conflicting-base")]
    public void Read_RepeatedArtifactIdentity_DeduplicatesAgreementAndRefusesConflict(string shape)
    {
        using var fixture = new ComplianceRenderCommandTests();
        var expected = ComplianceRenderCommandTests.MakeFixtureEvidence("ReaderAgent");
        var first = JsonSerializer.SerializeToUtf8Bytes(expected, Json);
        var second = JsonSerializer.SerializeToNode(expected, Json)!;
        if (shape == "conflicting-rich") second["disclaimer"] = "A different claim";
        if (shape is "generic-and-rich" or "conflicting-base") second = second["base"]!.DeepClone();
        if (shape == "conflicting-base") second["summary"]!["overallStatus"] = "FAIL";
        var secondBytes = Encoding.UTF8.GetBytes(second.ToJsonString(new() { WriteIndented = true }));
        var directory = fixture.WriteNativeEvidence(expected, true, first, secondBytes);
        var reader = new AefComplianceArtifactReader();
        if (shape.StartsWith("conflicting", StringComparison.Ordinal))
            Assert.Throws<InvalidDataException>(() => reader.Read(directory));
        else
            Assert.Equal(shape == "same-json" ? 1 : 2, reader.Read(directory).Documents.Count);
    }

    [Fact]
    public void Read_DuplicateJsonMembers_RefusesAmbiguousArtifact() => AssertInvalidJsonSuffix(
        ",\"disclaimer\":\"Conflicting disclaimer\"}");

    [Theory]
    [InlineData(",\"extra\":1e400}")]
    [InlineData(",\"extra\":\"\\uD800\"}")]
    public void Read_InvalidIJsonInUnknownField_Refuses(string suffix) => AssertInvalidJsonSuffix(suffix);

    private static void AssertInvalidJsonSuffix(string suffix)
    {
        using var fixture = new ComplianceRenderCommandTests();
        var expected = ComplianceRenderCommandTests.MakeFixtureEvidence("ReaderAgent");
        var json = JsonSerializer.Serialize(expected, Json);
        var directory = fixture.WriteNativeEvidence(expected, true, Encoding.UTF8.GetBytes(json[..^1] + suffix));
        Assert.Throws<InvalidDataException>(() => new AefComplianceArtifactReader().Read(directory));
    }

    [Fact]
    public void Read_ArtifactExceedsConfiguredLimit_RefusesWithoutUnboundedAllocation()
    {
        using var fixture = new ComplianceRenderCommandTests();
        var directory = fixture.WriteNativeEvidence(ComplianceRenderCommandTests.MakeFixtureEvidence("ReaderAgent"), true);
        Assert.Throws<AefLimitException>(() => new AefComplianceArtifactReader(maxArtifactBytes: 1).Read(directory));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1073741825L)]
    public void Construct_InvalidArtifactLimit_Refuses(long limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AefComplianceArtifactReader(limit));
}
