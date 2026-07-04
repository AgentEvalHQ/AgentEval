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
}
