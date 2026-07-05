// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Diagnostics;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Agentic.Diagnostics;

/// <summary>
/// Glass Box Phase 3 (A3) — <see cref="ToolReliabilityEval"/>: min per-tool success rate over ToolExecution
/// trace entries. Skip / pass / fail matrix per the Phase-3 plan §4.5.
/// </summary>
public class ToolReliabilityEvalTests
{
    private static EvalInput Input(AgentTrace? trace)
    {
        var input = new EvalInput(Query: "q");
        return trace is null ? input : input.WithTrace(trace);
    }

    private static AgentTrace TraceWith(params (string name, bool succeeded)[] execs)
    {
        var trace = new AgentTrace();
        var i = 0;
        foreach (var (name, succeeded) in execs)
        {
            trace.Entries.Add(TraceEntry.ForToolExecution(
                index: i++, correlationId: null, toolName: name, arguments: null,
                result: succeeded ? "ok" : null, durationMs: 5, succeeded: succeeded,
                error: succeeded ? null : "boom"));
        }

        return trace;
    }

    [Fact]
    public async Task NoTrace_IsSkipped()
    {
        var result = await new ToolReliabilityEval().EvaluateAsync(Input(trace: null));

        Assert.Equal("skipped", result.Score.Label);
    }

    [Fact]
    public async Task TraceWithoutToolExecutionEntries_IsSkipped()
    {
        // A trace that has only a ChatTurn response — no ToolExecution scope.
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(0, null, "hi", 1, usage: null, toolCalls: null, finishReason: "stop", providerMetadata: null));

        var result = await new ToolReliabilityEval().EvaluateAsync(Input(trace));

        Assert.Equal("skipped", result.Score.Label);
    }

    [Fact]
    public async Task AllToolsSucceed_Passes_SeverityNone()
    {
        var trace = TraceWith(("search", true), ("search", true), ("book", true));

        var result = await new ToolReliabilityEval().EvaluateAsync(Input(trace));

        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("none", result.Score.Severity);
        Assert.Equal(1.0, result.Score.Value);
    }

    [Fact]
    public async Task OneFlakyTool_BelowThreshold_Fails_SeverityHigh()
    {
        // 'book' succeeds 1/3 (0.33) < 0.90 threshold → min success rate fails.
        var trace = TraceWith(
            ("search", true), ("search", true),
            ("book", true), ("book", false), ("book", false));

        var result = await new ToolReliabilityEval().EvaluateAsync(Input(trace));

        Assert.False(result.Score.Passed);
        Assert.Equal("fail", result.Score.Label);
        Assert.Equal("high", result.Score.Severity);
        Assert.Equal(1.0 / 3.0, result.Score.Value, precision: 5);
    }

    [Fact]
    public async Task ToolNameCasingVaries_IsOneTool_NotSkewed()
    {
        // "Search" and "search" are the same tool — case-insensitive grouping must not treat them as two.
        var trace = TraceWith(("Search", true), ("search", true), ("SEARCH", true));

        var result = await new ToolReliabilityEval().EvaluateAsync(Input(trace));

        Assert.True(result.Score.Passed);
        Assert.Equal(1.0, result.Details.Dimensions!["tool_count"]); // one tool, not three
    }

    [Fact]
    public async Task NullToolCallsEntry_IsGuarded_NotThrown()
    {
        // A hand-built ToolExecution entry with null ToolCalls must be skipped, not indexed [0].
        var trace = TraceWith(("search", true));
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.ToolCall, Scope = TraceEntryScope.ToolExecution, ToolCalls = null });

        var result = await new ToolReliabilityEval().EvaluateAsync(Input(trace));

        Assert.True(result.Score.Passed); // the one valid 'search' execution scores 1.0; the null entry is ignored
    }
}
