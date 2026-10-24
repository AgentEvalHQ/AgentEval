// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// v1.1 task 1.7 / 6-plan F-002: <see cref="AtomicLlmEval"/> must populate
/// <see cref="EvalProvenance.EstimatedCost"/> from real judge token usage. Pre-1.7 the field
/// hard-coded 0, making composite cost rollups meaningless. Not priced is null, never $0 and never
/// a made-up rate (T07): an unknown or null judge model, no usage, or one count missing.
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

    private static double Priced(EvalResult result)
    {
        Assert.NotNull(result.Provenance.EstimatedCost);
        return result.Provenance.EstimatedCost!.Value;
    }

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
        Assert.Equal(0.00045, Priced(result), precision: 8);
        Assert.Equal(1500, result.Provenance.TokensUsed);
    }

    [Theory]
    [InlineData("llama3.1:8b")]          // an Ollama judge: no row in the table
    [InlineData("unicorn-model-9000")]
    public async Task EvaluateAsync_WithTokenUsage_UnknownModel_IsNotPriced(string judgeModel)
    {
        // Before T07 the cost came out at the gpt-4o-mini fallback rate: 0.00075 for 1000 + 1000 tokens, a price
        // nobody had set for this model, which then flowed into composite totals, AEF usage and reports.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 1000, outputTokens: 1000);
        var sut = MakeSut(evaluator, judgeModel: judgeModel);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Null(result.Provenance.EstimatedCost);
        Assert.Equal(2000, result.Provenance.TokensUsed); // the tokens are still reported; only the price is unknown
    }

    [Fact]
    public async Task EvaluateAsync_WithTokenUsage_NullJudgeModel_IsNotPriced()
    {
        // judgeModel defaults to null (the ctor default, used by most constructions in src): no model, no rate.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 2000, outputTokens: 0);
        var sut = MakeSut(evaluator, judgeModel: null);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Null(result.Provenance.EstimatedCost);
        Assert.Equal(2000, result.Provenance.TokensUsed);
    }

    [Fact]
    public async Task EvaluateAsync_NoTokenUsage_IsNotPriced()
    {
        // Mirrors test-fake / non-chat IEvaluator implementations: a judge ran, and what it cost is unknown.
        // It used to read 0 (free), which a rollup could not tell from a measured zero.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: null, outputTokens: null);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini");

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Null(result.Provenance.EstimatedCost);
        Assert.Null(result.Provenance.TokensUsed);
    }

    [Theory]
    [InlineData(1000L, null)]
    [InlineData(null, 1000L)]
    public async Task EvaluateAsync_OneTokenCountMissing_IsNotPriced(long? inputTokens, long? outputTokens)
    {
        // A missing count is not a zero count: pricing it as 0 under-priced the call and read as complete.
        var evaluator = new TokenReportingEvaluator(85, inputTokens, outputTokens);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini");

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Null(result.Provenance.EstimatedCost);
    }

    [Fact]
    public async Task EvaluateAsync_ReportedZeroTokens_IsAMeasuredZero()
    {
        // Guard: both counts reported as 0 is a real result (nothing spent), not "not priced".
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 0, outputTokens: 0);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini");

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.0, Priced(result));
    }

    [Fact]
    public async Task EvaluateAsync_DatedModelSuffix_MatchesBaseRate()
    {
        // Substring match: "gpt-4o-mini-2024-07-18" should match the "gpt-4o-mini" entry.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 1000, outputTokens: 1000);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini-2024-07-18");

        var result = await sut.EvaluateAsync(MakeInput());

        // 0.00015 + 0.00060 = 0.00075
        Assert.Equal(0.00075, Priced(result), precision: 8);
        Assert.False(JudgeCostMap.IsRegistered("gpt-4o-mini-2024-07-18")); // exact-key only: priced, yet not a key
    }

    [Fact]
    public async Task EvaluateAsync_WithRateResolver_OverridesStaticMap()
    {
        // LR7-F1: a per-instance rate resolver wins over JudgeCostMap.GetRate.
        // Useful for tenants with negotiated rates that differ from list price.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 1000, outputTokens: 1000);
        Func<string?, JudgeCostMap.ModelRate> tenantResolver =
            _ => new JudgeCostMap.ModelRate(InputRatePer1K: 0.00100, OutputRatePer1K: 0.00200);
        var sut = new AtomicLlmEval(
            evaluator: evaluator,
            key: "test.cost",
            name: "Test",
            category: "test",
            version: "1.0.0",
            criteria: new[] { "scored well" },
            passThreshold: 0.70,
            judgeModel: "gpt-4o-mini",
            rateResolver: tenantResolver);

        var result = await sut.EvaluateAsync(MakeInput());

        // Tenant rate: 0.00100 + 0.00200 = 0.00300 (NOT the static map's 0.00075)
        Assert.Equal(0.00300, Priced(result), precision: 8);
    }

    [Fact]
    public async Task EvaluateAsync_NoRateResolver_UsesStaticMap()
    {
        // Confirms backward-compat: when no resolver is supplied the static map governs.
        var evaluator = new TokenReportingEvaluator(85, inputTokens: 1000, outputTokens: 1000);
        var sut = MakeSut(evaluator, judgeModel: "gpt-4o-mini");

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.00075, Priced(result), precision: 8);
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
        Assert.Equal(0.01000, Priced(result), precision: 8);
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
        var leafCosts = result.Details.SubResults!.Select(Priced).ToList();
        var leafSum = leafCosts.Sum();
        Assert.True(leafSum > 0, "Leaf costs must be non-zero when judge tokens are reported.");

        // CostRollup.Aggregate over the leaves equals the manual sum, and so does the composite's own cost.
        var (rolled, _, unpriced) = CostRollup.Aggregate(result.Details.SubResults!);
        Assert.Empty(unpriced);
        Assert.Equal(leafSum, rolled!.Value, precision: 8);
        Assert.Equal(leafSum, Priced(result), precision: 8);

        // Sanity-check the leaf-by-leaf math too:
        // (1000/1000)*0.00015 + (500/1000)*0.00060 = 0.00045
        // (2000/1000)*0.00015 + (1000/1000)*0.00060 = 0.00090
        // (500/1000)*0.00015 + (250/1000)*0.00060 = 0.000225
        // expected total = 0.001575
        Assert.Equal(0.001575, leafSum, precision: 8);
    }
}
