// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Decisions;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// <see cref="DecisionEval"/> maps ONE yes/no probability onto an <see cref="EvalResult"/> (ADR-033).
/// These pin the mapping: the probability IS the score, the threshold decides the verdict, the raw
/// value survives in Dimensions, confidence stays null, provenance names the RESOLVED model, and a
/// transport failure is an exception rather than a 0.0.
/// </summary>
public class DecisionEvalTests
{
    // ── Fake transport ───────────────────────────────────────────────────────

    private sealed class FakeDecisionClient : IDecisionClient
    {
        public double TrueProbability { get; set; } = 0.5;
        public string Model { get; set; } = "typesafe/jev-1.13-20260917";
        public DecisionUsage? Usage { get; set; } = new(200, 10, Cost: null);
        public Exception? Throw { get; set; }
        public DecisionRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }

        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            if (Throw is not null) throw Throw;

            var answers = request.Questions.Keys.ToDictionary(
                k => k,
                _ => (DecisionAnswer)new BinaryAnswer(TrueProbability),
                StringComparer.Ordinal);
            return Task.FromResult(new DecisionResponse(Model, answers, Usage));
        }
    }

    private static DecisionEval MakeSut(
        FakeDecisionClient client,
        double passThreshold = 0.70,
        string? failureSeverity = null,
        string? model = null,
        Func<EvalInput, object>? stateProjector = null,
        Func<string?, JudgeCostMap.ModelRate>? rateResolver = null) =>
        new(
            client,
            key: "grounded",
            name: "Grounded in evidence",
            category: "quality",
            version: "1.0.0",
            instructions: "Is every material claim in the response supported by the context?",
            passThreshold: passThreshold,
            trueCriteria: "Every claim is in the context.",
            falseCriteria: "At least one claim is not.",
            model: model,
            failureSeverity: failureSeverity,
            stateProjector: stateProjector,
            rateResolver: rateResolver);

    private static EvalInput MakeInput(string? response = "The invoice was paid on 3 May.") =>
        new(Query: "When was the invoice paid?", Response: response, Context: "Ledger: invoice 41 paid 2026-05-03.");

    // ── The mapping ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ProbabilityAtOrAboveThreshold_PassesWithProbabilityAsScore()
    {
        var client = new FakeDecisionClient { TrueProbability = 0.87 };
        var sut = MakeSut(client, passThreshold: 0.80);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.87, result.Score.Value, precision: 9);
        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("none", result.Score.Severity);
        Assert.Equal(0.80, result.Score.Threshold);
        Assert.Equal(0.87, result.Details.Dimensions![DecisionEval.ProbabilityDimension], precision: 9);
    }

    [Fact]
    public async Task EvaluateAsync_ProbabilityBelowThreshold_FailsButKeepsTheProbability()
    {
        // 0.69 against 0.70 is a fail — and the 0.69 must survive, because a threshold sweep later
        // needs it. This is the "do not round to fail and discard" rule.
        var client = new FakeDecisionClient { TrueProbability = 0.69 };
        var sut = MakeSut(client, passThreshold: 0.70);

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.False(result.Score.Passed);
        Assert.Equal("fail", result.Score.Label);
        Assert.Equal(0.69, result.Score.Value, precision: 9);
        Assert.Equal(0.69, result.Details.Dimensions![DecisionEval.ProbabilityDimension], precision: 9);
        Assert.Equal("medium", result.Score.Severity);   // >= 0.40 and failed
    }

    [Fact]
    public async Task EvaluateAsync_LowProbability_IsHighSeverity_AndFailureSeverityRaisesNotLowers()
    {
        var client = new FakeDecisionClient { TrueProbability = 0.10 };

        var plain = await MakeSut(client).EvaluateAsync(MakeInput());
        Assert.Equal("high", plain.Score.Severity);

        var critical = await MakeSut(client, failureSeverity: "critical").EvaluateAsync(MakeInput());
        Assert.Equal("critical", critical.Score.Severity);

        var lowDeclared = await MakeSut(client, failureSeverity: "low").EvaluateAsync(MakeInput());
        Assert.Equal("high", lowDeclared.Score.Severity);   // the score-derived "high" is not downgraded
    }

    [Fact]
    public async Task EvaluateAsync_ConfidenceIsNull_BecauseABinaryAnswerCarriesNone()
    {
        var result = await MakeSut(new FakeDecisionClient { TrueProbability = 0.99 }).EvaluateAsync(MakeInput());
        Assert.Null(result.Score.Confidence);
    }

    // ── Provenance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ProvenanceNamesTheKindAndTheResolvedModel()
    {
        var client = new FakeDecisionClient { Model = "typesafe/jev-1.13-20260917" };
        var sut = MakeSut(client, model: "~typesafe/jev-latest");   // an alias was REQUESTED…

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(DecisionEval.ProvenanceType, result.Provenance.Type);
        Assert.Equal("atomic-decision", result.Provenance.Type);
        Assert.Equal("typesafe/jev-1.13-20260917", result.Provenance.JudgeModel);   // …the RESOLVED id is recorded
        Assert.Equal("~typesafe/jev-latest", client.LastRequest!.Model);           // and the alias went on the wire
        Assert.NotNull(result.Provenance.PromptHash);
        Assert.Equal(16, result.Provenance.PromptHash!.Length);
        Assert.False(result.Provenance.CacheHit);
    }

    [Fact]
    public async Task EvaluateAsync_ProviderReportedCost_WinsOverEstimate()
    {
        var client = new FakeDecisionClient { Usage = new DecisionUsage(1_000_000, 0, Cost: 0.042) };
        var sut = MakeSut(client, rateResolver: _ => new JudgeCostMap.ModelRate(99.0, 99.0));   // would be wildly different

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.042, result.Provenance.EstimatedCost, precision: 9);
        Assert.Equal(1_000_000, result.Provenance.TokensUsed);
    }

    [Fact]
    public async Task EvaluateAsync_NoProviderCost_UsesJevListPriceFromCostMap()
    {
        // 1M input tokens at OpenRouter's published $0.042/M, output free.
        var client = new FakeDecisionClient { Usage = new DecisionUsage(1_000_000, 50_000, Cost: null), Model = "typesafe/jev-1.13" };

        var result = await MakeSut(client).EvaluateAsync(MakeInput());

        Assert.Equal(0.042, result.Provenance.EstimatedCost, precision: 9);
    }

    [Fact]
    public async Task EvaluateAsync_NoProviderCost_RateResolverWins()
    {
        var client = new FakeDecisionClient { Usage = new DecisionUsage(2_000, 1_000, Cost: null) };
        var sut = MakeSut(client, rateResolver: _ => new JudgeCostMap.ModelRate(InputRatePer1K: 0.001, OutputRatePer1K: 0.002));

        var result = await sut.EvaluateAsync(MakeInput());

        Assert.Equal(0.002 + 0.002, result.Provenance.EstimatedCost, precision: 9);
    }

    [Fact]
    public async Task EvaluateAsync_NoUsage_ZeroCostAndNullTokens()
    {
        var result = await MakeSut(new FakeDecisionClient { Usage = null }).EvaluateAsync(MakeInput());
        Assert.Equal(0.0, result.Provenance.EstimatedCost);
        Assert.Null(result.Provenance.TokensUsed);
    }

    // ── What goes on the wire ────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_AsksOneBinaryQuestionKeyedByEvalKey_WithCriteria()
    {
        var client = new FakeDecisionClient();
        await MakeSut(client).EvaluateAsync(MakeInput());

        var question = Assert.IsType<BinaryQuestion>(Assert.Single(client.LastRequest!.Questions).Value);
        Assert.Equal("grounded", Assert.Single(client.LastRequest.Questions).Key);
        Assert.Equal("Every claim is in the context.", question.TrueCriteria);
        Assert.Equal("At least one claim is not.", question.FalseCriteria);
    }

    [Fact]
    public async Task EvaluateAsync_DefaultState_CarriesQueryResponseContext()
    {
        var client = new FakeDecisionClient();
        await MakeSut(client).EvaluateAsync(MakeInput());

        var state = Assert.IsType<DefaultDecisionState>(client.LastRequest!.State);
        Assert.Equal("When was the invoice paid?", state.Query);
        Assert.Equal("The invoice was paid on 3 May.", state.Response);
        Assert.Equal("Ledger: invoice 41 paid 2026-05-03.", state.Context);
        Assert.Null(state.GroundTruth);
    }

    [Fact]
    public async Task EvaluateAsync_StateProjector_ReplacesTheDefault()
    {
        var client = new FakeDecisionClient();
        await MakeSut(client, stateProjector: i => new { OnlyThis = i.Response }).EvaluateAsync(MakeInput());

        Assert.IsNotType<DefaultDecisionState>(client.LastRequest!.State);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_TransportFailure_Propagates_NeverBecomesAScore()
    {
        var client = new FakeDecisionClient { Throw = new DecisionClientException(DecisionFailureKind.RateLimited, "429", 429) };

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() => MakeSut(client).EvaluateAsync(MakeInput()));
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task EvaluateAsync_NullResponse_Throws()
    {
        var client = new FakeDecisionClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MakeSut(client).EvaluateAsync(MakeInput(response: null)));
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Ctor_RejectsThresholdOutsideUnitInterval(double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MakeSut(new FakeDecisionClient(), passThreshold: threshold));
    }

    [Fact]
    public void Ctor_RejectsNullClientAndBlankInstructions()
    {
        Assert.Throws<ArgumentNullException>(() => new DecisionEval(null!, "k", "n", "c", "1.0.0", "?"));
        Assert.Throws<ArgumentException>(() => new DecisionEval(new FakeDecisionClient(), "k", "n", "c", "1.0.0", " "));
    }

    // ── It composes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DecisionEval_IsAnOrdinaryCompositeLeaf()
    {
        var client = new FakeDecisionClient { TrueProbability = 0.90 };
        var composite = new CompositeEval(
            key: "answer_quality", name: "Answer quality", category: "quality", version: "1.0.0",
            components: [new EvalComponent(MakeSut(client, passThreshold: 0.80), Weight: 1.0)],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.80);

        var result = await composite.EvaluateAsync(MakeInput());

        Assert.True(result.Score.Passed);
        var leaf = Assert.Single(result.Details.SubResults!);
        Assert.Equal("atomic-decision", leaf.Provenance.Type);
        Assert.Equal(0.90, leaf.Score.Value, precision: 9);
    }
}
