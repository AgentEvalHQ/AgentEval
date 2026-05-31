// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Models.Serialization;
using Xunit;

namespace AgentEval.Tests.Serialization;

/// <summary>
/// Regression coverage for GAP-17: WorkflowSerializer.FromJson now guards its input like the sibling
/// methods (ArgumentException on null/empty/whitespace) instead of emitting a bare framework
/// ArgumentNullException, and documents that malformed JSON surfaces as JsonException.
/// </summary>
public class WorkflowSerializerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromJson_NullOrWhitespace_ThrowsArgumentException(string? json)
    {
        // null → ArgumentNullException, empty/whitespace → ArgumentException (both ArgumentException-derived).
        Assert.ThrowsAny<ArgumentException>(() => WorkflowSerializer.FromJson(json!));
    }

    [Fact]
    public void FromJson_MalformedJson_ThrowsJsonException()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => WorkflowSerializer.FromJson("{ not valid json"));
    }

    // GAP-15: a node DisplayName carrying Mermaid metacharacters must not corrupt/inject diagram
    // structure. The label is rendered as a quoted Mermaid label with line breaks stripped and
    // backticks/quotes neutralized, so brackets/pipes/newlines/fences become literal label text.
    [Fact]
    public void ToMermaid_MaliciousDisplayName_DoesNotBreakOutOfLabel()
    {
        var malicious = "evil] --> hacked[pwned\n```\nrm -rf \"x\"`";
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "ok",
            Steps = [],
            Graph = new WorkflowGraphSnapshot
            {
                Nodes = [new WorkflowNode { NodeId = "n1", DisplayName = malicious, IsEntryPoint = true }],
                Edges = [],
                EntryNodeId = "n1",
                ExitNodeIds = ["n1"],
            },
        };

        var mermaid = WorkflowSerializer.ToMermaid(result);

        // The injected newline must not appear inside the node-definition line (no extra lines injected).
        var nodeLine = mermaid.Split('\n').Single(l => l.TrimStart().StartsWith("n1"));
        Assert.Contains("\"", nodeLine);                 // label is quoted
        Assert.DoesNotContain("```\nrm", mermaid.Replace("\r", "")); // no raw fence break-out into the label region
        Assert.DoesNotContain("evil] --> hacked[pwned\n", mermaid.Replace("\r", "")); // newline stripped from label
        // The literal double-quote in the payload is escaped to the Mermaid entity, not left raw.
        Assert.Contains("#quot;", nodeLine);
    }
}
