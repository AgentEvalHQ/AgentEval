// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Agentic.Safety;

/// <summary>Glass Box Phase 3 (A4) — SafetyIntervention + TruncationDetection over per-turn finish reasons.</summary>
public class SafetyAndTruncationEvalTests
{
    private static AgentTrace TraceWith(params string?[] finishReasons)
    {
        var trace = new AgentTrace();
        var i = 0;
        foreach (var fr in finishReasons)
            trace.Entries.Add(TraceEntry.ForChatResponse(i++, null, "r", 1, usage: null, toolCalls: null, finishReason: fr, providerMetadata: null));
        return trace;
    }

    private static EvalInput With(AgentTrace? t) => t is null ? new EvalInput("q") : new EvalInput("q").WithTrace(t);

    [Fact]
    public async Task SafetyIntervention_NoTrace_Skipped()
        => Assert.Equal("skipped", (await new SafetyInterventionEval().EvaluateAsync(With(null))).Score.Label);

    [Fact]
    public async Task SafetyIntervention_NoResponses_Skipped()
        => Assert.Equal("skipped", (await new SafetyInterventionEval().EvaluateAsync(With(new AgentTrace()))).Score.Label);

    [Fact]
    public async Task SafetyIntervention_NoFilter_Passes()
    {
        var r = await new SafetyInterventionEval().EvaluateAsync(With(TraceWith("stop", "stop")));
        Assert.True(r.Score.Passed);
        Assert.Equal("none", r.Score.Severity);
    }

    [Fact]
    public async Task SafetyIntervention_OneFilter_Fails_Medium()
    {
        var r = await new SafetyInterventionEval().EvaluateAsync(With(TraceWith("stop", "content_filter")));
        Assert.False(r.Score.Passed);
        Assert.Equal("medium", r.Score.Severity);
        Assert.Equal(0.5, r.Score.Value);
    }

    [Fact]
    public async Task Truncation_NoLength_Passes()
    {
        var r = await new TruncationDetectionEval().EvaluateAsync(With(TraceWith("stop", "stop")));
        Assert.True(r.Score.Passed);
    }

    [Fact]
    public async Task Truncation_OneLength_Fails_Low()
    {
        var r = await new TruncationDetectionEval().EvaluateAsync(With(TraceWith("stop", "length")));
        Assert.False(r.Score.Passed);
        Assert.Equal("low", r.Score.Severity);
    }

    [Fact]
    public async Task Truncation_NoTrace_Skipped()
        => Assert.Equal("skipped", (await new TruncationDetectionEval().EvaluateAsync(With(null))).Score.Label);
}
