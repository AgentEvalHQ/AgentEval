// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.RedTeam.Core;

public class ProbeToolCallsTests
{
    private static AgentResponse ResponseWithToolCall(string toolName)
    {
        var callId = $"call-{toolName}";
        var raw = new List<object>
        {
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("working on it"),
                new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { ["path"] = "config.json" })
            }),
            new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent(callId, "deleted") })
        };
        return new AgentResponse { Text = "Done.", RawMessages = raw };
    }

    [Fact] public void Extract_NullResponse_ReturnsEmpty() => Assert.Equal(0, ProbeToolCalls.Extract(null).Count);

    [Fact] public void Extract_NoRawMessages_ReturnsEmpty()
        => Assert.Equal(0, ProbeToolCalls.Extract(new AgentResponse { Text = "hi" }).Count);

    [Fact]
    public void Extract_WalksFunctionCallContent_AndPairsResult()
    {
        var report = ProbeToolCalls.Extract(ResponseWithToolCall("admin_delete"));
        Assert.Equal(1, report.Count);
        Assert.True(report.WasToolCalled("admin_delete"));
        Assert.Equal("deleted", report.Calls[0].Result?.ToString());
    }

    [Fact]
    public void InvokedForbiddenTool_CaseInsensitiveMatch()
    {
        var resp = ResponseWithToolCall("Admin_Delete");
        Assert.True(ProbeToolCalls.InvokedForbiddenTool(resp, new[] { "admin_delete" }));
        Assert.False(ProbeToolCalls.InvokedForbiddenTool(resp, new[] { "read_file" }));
    }

    [Fact]
    public void ForbiddenCalls_ReturnsOnlyMatchingRecords()
    {
        var hits = ProbeToolCalls.ForbiddenCalls(ResponseWithToolCall("admin_delete"), new[] { "admin_delete", "wire_transfer" });
        Assert.Single(hits);
        Assert.Equal("admin_delete", hits[0].Name);
    }
}
