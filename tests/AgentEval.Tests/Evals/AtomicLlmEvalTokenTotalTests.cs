// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// T82: <see cref="AtomicLlmEval"/>'s token total (<see cref="EvalProvenance.TokensUsed"/>) is complete or absent, like
/// its cost. With one of the judge's two counts unreported the cost was already not priced, but the total was the other
/// count alone, read as the call's whole spend.
/// </summary>
public sealed class AtomicLlmEvalTokenTotalTests
{
    // Reports the token counts it is given; only the usage reporting is under test, never the grade.
    private sealed class TokenReportingEvaluator(long? inputTokens, long? outputTokens) : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 85,
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            });
    }

    private static Task<EvalResult> EvaluateAsync(long? inputTokens, long? outputTokens) =>
        new AtomicLlmEval(
            new TokenReportingEvaluator(inputTokens, outputTokens),
            key: "test-llm-tokens",
            name: "Test LLM Tokens",
            category: "quality",
            version: "1.0.0",
            criteria: ["accuracy"],
            judgeModel: "gpt-4o-mini")
            .EvaluateAsync(new EvalInput(Query: "q", Response: "r"));

    [Theory]
    [InlineData(1000L, null)]
    [InlineData(null, 1000L)]
    public async Task OneTokenCountMissing_HasNoTokenTotal(long? inputTokens, long? outputTokens)
    {
        var result = await EvaluateAsync(inputTokens, outputTokens);

        Assert.Null(result.Provenance.TokensUsed);      // was 1000: the reported count alone
        Assert.Null(result.Provenance.EstimatedCost);
    }

    [Fact]
    public async Task BothCountsReported_TotalIsTheirSum()
    {
        var result = await EvaluateAsync(1000, 500);

        Assert.Equal(1500, result.Provenance.TokensUsed);
    }

    [Fact]
    public async Task BothCountsReportedAsZero_IsAMeasuredZeroTotal()
    {
        // Guard: 0 + 0 reported is a real total (nothing spent), not an absent one.
        var result = await EvaluateAsync(0, 0);

        Assert.Equal(0, result.Provenance.TokensUsed);
        Assert.Equal(0.0, result.Provenance.EstimatedCost);
    }
}
