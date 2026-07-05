// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Diagnostics;
using AgentEval.Evals.Agentic.Security;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Agentic.Diagnostics;

/// <summary>Glass Box Phase 3 (A3 ToolErrorPattern + A6 ArgumentSanitization) over ToolExecution entries.</summary>
public class ToolErrorPatternAndArgSanitizationEvalTests
{
    private static AgentTrace TraceWith(params (string name, bool succeeded, string? error, string? args)[] execs)
    {
        var trace = new AgentTrace();
        var i = 0;
        foreach (var (name, succeeded, error, args) in execs)
            trace.Entries.Add(TraceEntry.ForToolExecution(i++, null, name, args, succeeded ? "ok" : null, 5, succeeded, error));
        return trace;
    }

    private static EvalInput With(AgentTrace? t) => t is null ? new EvalInput("q") : new EvalInput("q").WithTrace(t);

    // ── ToolErrorPattern ──

    [Fact]
    public async Task ErrorPattern_NoTrace_Skipped()
        => Assert.Equal("skipped", (await new ToolErrorPatternEval().EvaluateAsync(With(null))).Score.Label);

    [Fact]
    public async Task ErrorPattern_NoFailures_Passes()
    {
        var r = await new ToolErrorPatternEval().EvaluateAsync(With(TraceWith(("a", true, null, null), ("b", true, null, null))));
        Assert.True(r.Score.Passed);
        Assert.Equal(1.0, r.Score.Value);
    }

    [Fact]
    public async Task ErrorPattern_ToolNameCasingVaries_ClustersAsOne_Fails()
    {
        // "Search"/"search" failing the same way are ONE cluster (case-insensitive) — 3 of 4 → fail.
        var trace = TraceWith(
            ("Search", false, "timeout", null),
            ("search", false, "timeout", null),
            ("SEARCH", false, "timeout", null),
            ("book", true, null, null));
        var r = await new ToolErrorPatternEval().EvaluateAsync(With(trace));
        Assert.False(r.Score.Passed);
    }

    [Fact]
    public async Task ErrorPattern_LoneOneOffFailure_IsNotAPattern_Passes()
    {
        // A single isolated failure (cluster size 1) is NOT a concentrated pattern → pass (ToolReliability
        // covers lone failures). Regression for the short-trace false-positive.
        var trace = TraceWith(("search", false, "timeout", null), ("book", true, null, null));
        var r = await new ToolErrorPatternEval().EvaluateAsync(With(trace));
        Assert.True(r.Score.Passed);
        Assert.Equal(1.0, r.Score.Value);
    }

    [Fact]
    public async Task ErrorPattern_ConcentratedFailures_Fails_Medium()
    {
        // 3 of 4 calls fail the same way on 'search' → largest cluster 3/4 → score 0.25 < 0.80.
        var trace = TraceWith(
            ("search", false, "timeout", null), ("search", false, "timeout", null),
            ("search", false, "timeout", null), ("book", true, null, null));
        var r = await new ToolErrorPatternEval().EvaluateAsync(With(trace));
        Assert.False(r.Score.Passed);
        Assert.Equal("medium", r.Score.Severity);
    }

    // ── ArgumentSanitization ──

    [Fact]
    public async Task ArgSanitization_NoTrace_Skipped()
        => Assert.Equal("skipped", (await new ArgumentSanitizationEval().EvaluateAsync(With(null))).Score.Label);

    [Fact]
    public async Task ArgSanitization_CleanArgs_Passes()
    {
        var r = await new ArgumentSanitizationEval().EvaluateAsync(With(TraceWith(
            ("search", true, null, "{\"query\":\"weather\"}"))));
        Assert.True(r.Score.Passed);
        Assert.Equal("none", r.Score.Severity);
    }

    [Fact]
    public async Task ArgSanitization_LeakedSecret_Fails_High()
    {
        var r = await new ArgumentSanitizationEval().EvaluateAsync(With(TraceWith(
            ("send", true, null, "{\"email\":\"alice@example.com\"}"))));
        Assert.False(r.Score.Passed);
        Assert.Equal("high", r.Score.Severity);
    }

    // ── Shared guards: wrong-scope skip + null-ToolCalls guard (both tool-execution evaluators) ──

    private static AgentTrace ChatOnlyTrace()
    {
        var t = new AgentTrace();
        t.Entries.Add(TraceEntry.ForChatResponse(0, null, "hi", 1, null, null, "stop", null));
        return t;
    }

    private static AgentTrace NullToolCallsTrace()
    {
        var t = TraceWith(("search", true, null, null));
        t.Entries.Add(new TraceEntry { Type = TraceEntryType.ToolCall, Scope = TraceEntryScope.ToolExecution, ToolCalls = null });
        return t;
    }

    [Fact]
    public async Task ErrorPattern_NoToolExecutionScope_Skipped()
        => Assert.Equal("skipped", (await new ToolErrorPatternEval().EvaluateAsync(With(ChatOnlyTrace()))).Score.Label);

    [Fact]
    public async Task ErrorPattern_NullToolCalls_IsGuarded_NotThrown()
    {
        var r = await new ToolErrorPatternEval().EvaluateAsync(With(NullToolCallsTrace()));
        Assert.True(r.Score.Passed); // the one valid success scores 1.0; the null-ToolCalls entry is ignored
    }

    [Fact]
    public async Task ArgSanitization_NoToolExecutionScope_Skipped()
        => Assert.Equal("skipped", (await new ArgumentSanitizationEval().EvaluateAsync(With(ChatOnlyTrace()))).Score.Label);

    [Fact]
    public async Task ArgSanitization_NullToolCalls_IsGuarded_NotThrown()
    {
        var r = await new ArgumentSanitizationEval().EvaluateAsync(With(NullToolCallsTrace()));
        Assert.True(r.Score.Passed); // clean single arg; the null-ToolCalls entry is ignored
    }
}
