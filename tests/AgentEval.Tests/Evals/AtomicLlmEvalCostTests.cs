// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// v1.1 task 1.7 / 6-plan F-002: <see cref="AtomicLlmEval"/> must populate
/// <see cref="EvalProvenance.EstimatedCost"/> from real judge token usage. Pre-1.7 the field
/// hard-coded 0, making composite cost rollups meaningless.
/// </summary>
public class AtomicLlmEvalCostTests
{
    // ── Stubs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="AgentEval.Core.IEvaluator"/> that returns an <see cref="AgentEval.Core.EvaluationResult"/>
    /// carrying explicit token-usage values — mimicking what <c>ChatClientEvaluator</c> does
    /// after lifting <c>ChatResponse.Usage</c>.
    /// </summary>
    private sealed class TokenReportingEvaluator(int score, long? inputTokens, long? outputTokens) : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = score,
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            });
    }

    private static AtomicLlmEval MakeSut(
        AgentEval.Core.IEvaluator evaluator,
        string? judgeModel = null) =>
        new(
            evaluator,
            key: "test-llm-cost",
            name: "Test LLM Cost",
            category: "quality",
            version: "1.0.0",
            criteria: ["accuracy"],
            passThreshold: 0.70,
            judgeModel: judgeModel);

    private static EvalInput MakeInput() => new(Query: "q", Response: "r");

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WithTokenUsage_KnownModel_EstimatedCostMatchesRateMap()
    {
        // gpt-4o-mini: $0.00015 input / $0.00060 output per 1K tokens.
        const long inputTokens = 1000;
        const long outputTokens = 500;
        var evaluator = new TokenReportingEvaluator(85, inputTokens, outputTokens);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini");

        var result = await sut.EvaluateAsync(MakeInput());

        // (1000 / 1000) * 0.00015 + (500 / 1000) * 0.00060 = 0.00015 + 0.00030 = 0.00045
        Assert.True(result.Provenance.EstimatedCost > 0, "EstimatedCost must be > 0 when tokens are reported.");
        Assert.Equal(0.00045, result.Provenance.EstimatedCost, precision: 8);
        Assert.Equal(1500, result.Provenance.TokensUsed);
    }

    [Fact]
    public async Task EvaluateAsync_WithTokenUsage_UnknownModel_FallsBackToDefaultRate()
    {
        const long inputTokens = 1000;
        const long outputTokens = 1000;
        var evaluator = new TokenReportingEvaluator(85, inputTokens, outputTokens);
        var sut = MakeSut(evaluator, judgeModel: "unicorn-model-9000");

        var result = await sut.EvaluateAsync(MakeInput());

        // Defaults: 0.00015 in + 0.00060 out per 1K → 0.00015 + 0.00060 = 0.00075
        var expected = JudgeCostMap.DefaultInputRatePer1K + JudgeCostMap.DefaultOutputRatePer1K;
        Assert.Equal(expected, result.Provenance.EstimatedCost, precision: 8);
        Assert.False(JudgeCostMap.IsRegistered("unicorn-model-9000"));
    }

    [Fact]
    public async Task EvaluateAsync_WithTokenUsage_NullJudgeModel_UsesDefaultRate()
    {
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 2000, outputTokens: 0);
        var sut = MakeSut(evaluator, judgeModel: null);

        var result = await sut.EvaluateAsync(MakeInput());

        // 2 * defaultInputRate
        Assert.Equal(2 * JudgeCostMap.DefaultInputRatePer1K, result.Provenance.EstimatedCost, precision: 8);
    }

    [Fact]
    public async Task EvaluateAsync_NoTokenUsage_EstimatedCostStaysZero()
    {
        // Mirrors test-fake / non-chat IEvaluator implementations.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: null, outputTokens: null);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini");

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0, result.Provenance.EstimatedCost);
        Assert.Null(result.Provenance.TokensUsed);
    }

    [Fact]
    public async Task EvaluateAsync_DatedModelSuffix_MatchesBaseRate()
    {
        // Substring match: "gpt-4o-mini-2024-07-18" should match the "gpt-4o-mini" entry.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 1000, outputTokens: 1000);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini-2024-07-18");

        var result = await sut.EvaluateAsync(MakeInput());

        // 0.00015 + 0.00060 = 0.00075
        Assert.Equal(0.00075, result.Provenance.EstimatedCost, precision: 8);
    }

    [Fact]
    public async Task EvaluateAsync_Gpt41_UsesItsOwnRateNotLegacyGpt4()
    {
        // Regression guard for the LR7 finding: substring lookup would otherwise misprice
        // "gpt-4.1" against the legacy "gpt-4" rates ($0.030/$0.060 per 1K). The map now
        // carries an explicit "gpt-4.1" row ($0.002/$0.008 per 1K) so this divergence is locked in.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 1000, outputTokens: 1000);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4.1");

        var result = await sut.EvaluateAsync(MakeInput());

        // 0.00200 + 0.00800 = 0.01000 (not 0.09000 from legacy gpt-4 rates).
        Assert.Equal(0.01000, result.Provenance.EstimatedCost, precision: 8);
        Assert.NotEqual(0.09000, result.Provenance.EstimatedCost);
    }

    // ── Integration: CompositeEval cost rollup ───────────────────────────────

    [Fact]
    public async Task CompositeEval_OverThreeAtomicLlmEvals_CostRollupSumsToLeafTotal()
    {
        // F-002 acceptance: composite cost rollup over AtomicLlmEval children matches the
        // sum of leaf costs.
        var leaves = new EvalComponent[]
        {
            new(MakeSut(new TokenReportingEvaluator(80, 1000, 500), "gpt-4o-mini"),  1.0),
            new(MakeSut(new TokenReportingEvaluator(85, 2000, 1000), "gpt-4o-mini"), 1.0),
            new(MakeSut(new TokenReportingEvaluator(90, 500, 250), "gpt-4o-mini"),   1.0),
        };
        var composite = new CompositeEval(
            key: "composite-cost-rollup-test",
            name: "Composite Cost Rollup Test",
            category: "quality",
            version: "1.0.0",
            components: leaves,
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.70);

        var result = await composite.EvaluateAsync(new EvalInput(Query: "q", Response: "r"));

        Assert.NotNull(result.Details.SubResults);
        var leafCosts = result.Details.SubResults!.Select(r => r.Provenance.EstimatedCost).ToList();
        var leafSum = leafCosts.Sum();
        Assert.True(leafSum > 0, "Leaf costs must be non-zero when judge tokens are reported.");

        // CostRollup.Aggregate over the leaves equals the manual sum.
        var (rolled, _) = CostRollup.Aggregate(result.Details.SubResults!);
        Assert.Equal(leafSum, rolled, precision: 8);

        // Sanity-check the leaf-by-leaf math too:
        // (1000/1000)*0.00015 + (500/1000)*0.00060 = 0.00045
        // (2000/1000)*0.00015 + (1000/1000)*0.00060 = 0.00090
        // (500/1000)*0.00015 + (250/1000)*0.00060 = 0.000225
        // expected total = 0.001575
        Assert.Equal(0.001575, leafSum, precision: 8);
    }
}
