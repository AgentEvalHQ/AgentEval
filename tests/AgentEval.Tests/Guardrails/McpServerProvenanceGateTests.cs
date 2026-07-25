// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class McpServerProvenanceGateTests
{
    [Fact]
    public void McpToolDefinition_ServerId_PreservesThreeArgumentConstructorAndDeconstruction()
    {
        var tool = new McpToolDefinition("search", "description", """{"type":"object"}""")
        {
            ServerId = "catalog",
        };

        var (name, description, schema) = tool;

        Assert.Equal("catalog", tool.ServerId);
        Assert.Equal("search", name);
        Assert.Equal("description", description);
        Assert.Equal("""{"type":"object"}""", schema);
        Assert.NotNull(typeof(McpToolDefinition).GetConstructor([typeof(string), typeof(string), typeof(string)]));
    }

    [Fact]
    public void Fingerprint_ChangedServerId_DifferentHash()
    {
        var first = Tool("catalog-a", "search");
        var second = Tool("catalog-b", "search");

        Assert.NotEqual(
            McpToolDescriptionPoisoningGate.Fingerprint(first),
            McpToolDescriptionPoisoningGate.Fingerprint(second));
    }

    [Fact]
    public void Fingerprint_FieldSeparatorInsideIdentity_DoesNotCollide()
    {
        var first = Tool("catalog␞region", "search");
        var second = Tool("catalog", "region␞search");

        Assert.NotEqual(
            McpToolDescriptionPoisoningGate.Fingerprint(first),
            McpToolDescriptionPoisoningGate.Fingerprint(second));
    }

    [Fact]
    public void CanonicalContent_ServerIdAbsent_PreservesLegacyRendering()
    {
        var tool = new McpToolDefinition("search", "description", null);

        Assert.Equal("search␞description␞", tool.CanonicalContent());
    }

    [Fact]
    public void CaptureBaseline_SameToolNameFromDifferentServers_PreservesBothIdentities()
    {
        var first = Tool("catalog-a", "search");
        var second = Tool("catalog-b", "search");

        var baseline = McpServerProvenanceGate.CaptureBaseline([first, second]);

        Assert.Equal(2, baseline.Count);
        Assert.Contains(McpServerProvenanceGate.ManifestKey(first), baseline.Keys);
        Assert.Contains(McpServerProvenanceGate.ManifestKey(second), baseline.Keys);
    }

    [Fact]
    public void CheckDrift_OneOfDuplicateNamesChanges_ReportsOnlyQualifiedIdentity()
    {
        var first = Tool("catalog-a", "search", "original");
        var second = Tool("catalog-b", "search", "original");
        var baseline = McpServerProvenanceGate.CaptureBaseline([first, second]);

        var changedSecond = Tool("catalog-b", "search", "changed");
        var findings = McpServerProvenanceGate.CheckDrift([first, changedSecond], baseline);

        Assert.Equal(
            ManifestDriftKind.Unchanged,
            Assert.Single(findings, finding => finding.Key == McpServerProvenanceGate.ManifestKey(first)).Kind);
        Assert.Equal(
            ManifestDriftKind.Changed,
            Assert.Single(findings, finding => finding.Key == McpServerProvenanceGate.ManifestKey(second)).Kind);
    }

    [Fact]
    public void CheckDrift_ToolMovesServer_ReportsRemovedAndNew()
    {
        var original = Tool("catalog-a", "search");
        var moved = Tool("catalog-b", "search");
        var baseline = McpServerProvenanceGate.CaptureBaseline([original]);

        var findings = McpServerProvenanceGate.CheckDrift([moved], baseline);

        Assert.Equal(
            ManifestDriftKind.Removed,
            Assert.Single(findings, finding => finding.Key == McpServerProvenanceGate.ManifestKey(original)).Kind);
        Assert.Equal(
            ManifestDriftKind.New,
            Assert.Single(findings, finding => finding.Key == McpServerProvenanceGate.ManifestKey(moved)).Kind);
    }

    [Fact]
    public void CaptureBaseline_DuplicateServerAndToolName_ThrowsExplicitly()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            McpServerProvenanceGate.CaptureBaseline([Tool("catalog", "search"), Tool("catalog", "search")]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be pinned unambiguously", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void CaptureBaseline_MissingServerId_FailsWithoutNamePrefixInference(string? serverId)
    {
        var tool = new McpToolDefinition("catalog__search", "description", null) { ServerId = serverId };

        var exception = Assert.Throws<ArgumentException>(() =>
            McpServerProvenanceGate.CaptureBaseline([tool]));

        Assert.Contains("no authoritative server identity", exception.Message);
        Assert.Contains("must never be inferred", exception.Message);
    }

    private static McpToolDefinition Tool(string serverId, string name, string description = "description") =>
        new(name, description, null) { ServerId = serverId };
}
