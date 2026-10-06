// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Diagnostics;
using AgentEval.Tracing;
using Xunit;

namespace AgentEval.Tests.Agentic.Diagnostics;

/// <summary>
/// A tool call with a recorded error is a failure, whatever its <c>succeeded</c> flag says (#203 review round 3, B10f).
/// The flag defaults to true, so a trace written without it — the error recorded, the flag absent — read every errored
/// call as a success. B6c-5 fixed that in the tool-call projection; the Glass Box tool checks and the workflow replayer
/// still read the flag alone: three "permission denied" calls scored tool reliability 1.0.
/// </summary>
public class RecordedToolErrorTests
{
    // A trace as a writer that records errors but not the flag produces it: "error" present, "succeeded" absent.
    private static AgentTrace ErroredWithoutTheFlag(int calls)
    {
        var trace = new AgentTrace();
        for (var i = 0; i < calls; i++)
            trace.Entries.Add(TraceEntry.ForToolExecution(i, null, "delete_record", "{}", null, 5, succeeded: true, error: null));
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(trace))!;
        foreach (var call in node["entries"]!.AsArray()
                     .SelectMany(e => e!["toolCalls"]!.AsArray()))
        {
            call!.AsObject().Remove("succeeded");
            call.AsObject()["error"] = "permission denied";
        }
        var read = JsonSerializer.Deserialize<AgentTrace>(node.ToJsonString())!;
        Assert.All(read.Entries.SelectMany(e => e.ToolCalls!), c =>
        {
            Assert.True(c.Succeeded);                    // the flag's default
            Assert.Equal("permission denied", c.Error);   // and the error it did record
        });
        return read;
    }

    [Fact]
    public void ACallWithARecordedError_HasFailed_WhateverItsFlagSays()
    {
        Assert.True(new TraceToolCall { Succeeded = true, Error = "boom" }.Failed);
        Assert.True(new TraceToolCall { Succeeded = false }.Failed);
        Assert.False(new TraceToolCall { Succeeded = true }.Failed);
    }

    [Fact]
    public async Task ToolReliability_CountsAnErroredCall_AsAFailure()
    {
        var result = await new ToolReliabilityEval().EvaluateAsync(new EvalInput(Query: "q").WithTrace(ErroredWithoutTheFlag(3)));

        Assert.Equal("fail", result.Score.Label);
        Assert.Equal(0.0, result.Score.Value, 6);
    }

    [Fact]
    public async Task ToolErrorPattern_SeesTheRepeatedError()
    {
        var result = await new ToolErrorPatternEval().EvaluateAsync(new EvalInput(Query: "q").WithTrace(ErroredWithoutTheFlag(3)));

        Assert.NotEqual("pass", result.Score.Label);
        Assert.Contains(result.Details.Evidence ?? [], e => e.Message.Contains("permission denied", StringComparison.Ordinal));
    }

    [Fact]
    public void TheProjection_StillReadsTheErrorAsAFailure()
    {
        var calls = new EvalInput(Query: "q").WithTrace(ErroredWithoutTheFlag(2)).ToolCalls!;

        Assert.All(calls, c => Assert.False(c.Succeeded));
    }
}
