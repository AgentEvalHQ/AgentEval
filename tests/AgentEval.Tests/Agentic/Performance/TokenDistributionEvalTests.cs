// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Performance;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Agentic.Performance;

/// <summary>Glass Box Phase 3 (A7) — TokenDistribution skew over per-turn completion tokens.</summary>
public class TokenDistributionEvalTests
{
    private static AgentTrace TraceWith(params int[] completionTokensPerTurn)
    {
        var trace = new AgentTrace();
        var i = 0;
        foreach (var ct in completionTokensPerTurn)
            trace.Entries.Add(TraceEntry.ForChatResponse(i++, null, "r", 1,
                usage: new TraceTokenUsage { PromptTokens = 0, CompletionTokens = ct },
                toolCalls: null, finishReason: "stop", providerMetadata: null));
        return trace;
    }

    private static EvalInput With(AgentTrace? t) => t is null ? new EvalInput("q") : new EvalInput("q").WithTrace(t);

    [Fact]
    public async Task NoTrace_Skipped()
        => Assert.Equal("skipped", (await new TokenDistributionEval().EvaluateAsync(With(null))).Score.Label);

    [Fact]
    public async Task SingleTurn_Skipped()
        => Assert.Equal("skipped", (await new TokenDistributionEval().EvaluateAsync(With(TraceWith(100)))).Score.Label);

    [Fact]
    public async Task TwoTurns_Skipped()
        // Below 3 turns skew is undefined (max/sum >= 0.5 by construction) → skip, not a false FAIL.
        => Assert.Equal("skipped", (await new TokenDistributionEval().EvaluateAsync(With(TraceWith(50, 80)))).Score.Label);

    [Fact]
    public async Task AllZeroTokens_ThreeTurns_Skipped()
        => Assert.Equal("skipped", (await new TokenDistributionEval().EvaluateAsync(With(TraceWith(0, 0, 0)))).Score.Label);

    [Fact]
    public async Task BalancedThreeTurns_Passes()
    {
        // 3 balanced turns: max/sum = 1/3 → score 0.67 >= 0.5 → pass.
        var r = await new TokenDistributionEval().EvaluateAsync(With(TraceWith(50, 50, 50)));
        Assert.True(r.Score.Passed);
    }

    [Fact]
    public async Task SkewedTurn_Fails_Low()
    {
        // One turn dominates: 900/1000 → score 0.1 < 0.5 → fail.
        var r = await new TokenDistributionEval().EvaluateAsync(With(TraceWith(50, 50, 900)));
        Assert.False(r.Score.Passed);
        Assert.Equal("low", r.Score.Severity);
    }
}
