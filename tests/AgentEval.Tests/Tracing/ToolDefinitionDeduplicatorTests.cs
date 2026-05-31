// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Phase 0 (T0.3): the <see cref="ToolDefinitionDeduplicator"/> that collapses repeated
/// tool-definition schemas across turns (on for Smoke/Standard, off for AuditGrade).
/// </summary>
public class ToolDefinitionDeduplicatorTests
{
    private static List<TraceToolDefinition> OneDef(string name = "Search", string? schema = "{\"type\":\"object\"}")
        => new() { new TraceToolDefinition { Name = name, Description = "d", ParametersSchema = schema } };

    [Fact]
    public void Process_Enabled_FirstOccurrence_ReturnsSchemaVerbatim()
    {
        // Arrange
        var dedup = new ToolDefinitionDeduplicator(enabled: true);

        // Act
        var result = dedup.Process(OneDef());

        // Assert
        Assert.Equal("{\"type\":\"object\"}", result!.Single().ParametersSchema);
    }

    [Fact]
    public void Process_Enabled_SecondIdenticalOccurrence_NullsSchemaButKeepsName()
    {
        // Arrange
        var dedup = new ToolDefinitionDeduplicator(enabled: true);

        // Act
        _ = dedup.Process(OneDef());        // first sight — verbatim
        var second = dedup.Process(OneDef()); // identical (name, schema) — stubbed

        // Assert
        var def = second!.Single();
        Assert.Equal("Search", def.Name);     // name preserved for correlation
        Assert.Null(def.ParametersSchema);     // schema collapsed
    }

    [Fact]
    public void Process_Disabled_AlwaysReturnsSchemaVerbatim()
    {
        // Arrange
        var dedup = new ToolDefinitionDeduplicator(enabled: false);

        // Act
        _ = dedup.Process(OneDef());
        var second = dedup.Process(OneDef());

        // Assert — AuditGrade keeps verbatim payloads every turn
        Assert.Equal("{\"type\":\"object\"}", second!.Single().ParametersSchema);
    }

    [Fact]
    public void Process_Null_ReturnsNull()
    {
        // Arrange
        var dedup = new ToolDefinitionDeduplicator(enabled: true);

        // Act / Assert
        Assert.Null(dedup.Process(null));
    }

    [Fact]
    public void Process_SameNameDifferentSchema_KeepsBothVerbatim()
    {
        // Arrange — a tool whose schema legitimately differs (e.g. overloaded by model) must not be collapsed
        var dedup = new ToolDefinitionDeduplicator(enabled: true);

        // Act
        _ = dedup.Process(OneDef(schema: "{\"v\":1}"));
        var second = dedup.Process(OneDef(schema: "{\"v\":2}"));

        // Assert
        Assert.Equal("{\"v\":2}", second!.Single().ParametersSchema);
    }
}
