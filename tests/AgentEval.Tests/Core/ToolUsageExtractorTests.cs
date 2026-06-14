// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/Core/ToolUsageExtractorTests.cs
//
// L8: results are paired to calls WITHIN message order (one result per call), not via a global last-wins map. A
// CallId reused across turns must not let a later result back-fill an earlier emitted-only call (a Behavioral over-claim).
using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Core;

public class ToolUsageExtractorTests
{
    private static ChatMessage Assistant(params AIContent[] contents) => new(ChatRole.Assistant, contents);
    private static ChatMessage Tool(params AIContent[] contents) => new(ChatRole.Tool, contents);

    [Fact]
    public void Extract_ReusedCallIdAcrossTurns_DoesNotOverClaimEmittedOnlyCall()
    {
        var messages = new List<object>
        {
            Assistant(new FunctionCallContent("c1", "exfil")),       // turn 1: emitted, NO result
            Assistant(new FunctionCallContent("c1", "exfil")),       // turn 2: emitted (reuses CallId)
            Tool(new FunctionResultContent("c1", "ok")),             // turn 2: result
        };

        var report = ToolUsageExtractor.Extract(messages);

        Assert.Equal(2, report.Calls.Count);
        Assert.False(report.Calls[0].WasExecuted);   // turn-1 emitted-only — must not be back-filled
        Assert.True(report.Calls[1].WasExecuted);     // turn-2 executed
    }

    [Fact]
    public void Extract_UniqueCallIds_PairsEachIndependently()
    {
        var messages = new List<object>
        {
            Assistant(new FunctionCallContent("c1", "read")),
            Tool(new FunctionResultContent("c1", "data")),
            Assistant(new FunctionCallContent("c2", "delete")),      // emitted only — no result
        };

        var report = ToolUsageExtractor.Extract(messages);

        Assert.Equal(2, report.Calls.Count);
        Assert.True(report.Calls[0].WasExecuted);    // c1 executed
        Assert.False(report.Calls[1].WasExecuted);   // c2 emitted-only
    }

    [Fact]
    public void Extract_LoneResult_DoesNotFabricateACall()
    {
        var report = ToolUsageExtractor.Extract([Tool(new FunctionResultContent("orphan", "x"))]);
        Assert.Empty(report.Calls);
    }
}
