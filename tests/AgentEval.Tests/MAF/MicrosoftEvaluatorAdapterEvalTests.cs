// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Adapters;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;
using MeaiEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MeaiIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Tests.MAF;

/// <summary>
/// ADR-030 Slice 0.4. <c>MicrosoftEvaluatorAdapter</c> entered the library only through
/// <c>IMetric</c> — the second-class contract with no provenance, no cost and no way to say
/// "undecidable" (it buried <c>indeterminate = true</c> in an untyped dictionary). Retargeted to
/// <c>IEval</c> (dual-target; the <c>IMetric</c> path keeps working): a first-party M.E.AI quality
/// evaluator now produces an <see cref="EvalResult"/> with <c>Provenance.Type == "atomic-llm"</c> and a
/// real <c>EstimatedCost</c> derived from the tokens the judge actually spent.
/// </summary>
public class MicrosoftEvaluatorAdapterEvalTests
{
    /// <summary>
    /// An M.E.AI evaluator that behaves like the first-party quality evaluators: it calls the judge
    /// through <c>chatConfiguration.ChatClient</c> (so usage is observable) and returns one metric.
    /// </summary>
    private sealed class JudgeCallingEvaluator(Func<Microsoft.Extensions.AI.Evaluation.EvaluationMetric> metric, string name = "Fluency") : MeaiIEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => new[] { name };

        public async ValueTask<MeaiEvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiEvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            // The real evaluators make one or more judge calls through the supplied configuration.
            if (chatConfiguration is not null)
            {
                await chatConfiguration.ChatClient.GetResponseAsync(
                    new[] { new ChatMessage(ChatRole.User, "grade this") }, cancellationToken: cancellationToken);
            }

            var result = new MeaiEvaluationResult();
            result.Metrics[name] = metric();
            return result;
        }
    }

    private static Microsoft.Extensions.AI.Evaluation.NumericMetric Numeric(double? value, string? reason = null) =>
        new("Fluency", value, reason);

    private static ScriptedChatClient JudgeWithUsage(long inTok = 1000, long outTok = 200) =>
        new ScriptedChatClient().AddText("{\"score\": 4}", inTok, outTok);

    private static readonly EvalInput Input = new(Query: "Describe the weather.", Response: "It is sunny and mild today.");

    [Fact]
    public async Task MicrosoftEvaluator_ProducesEvalResult()
    {
        // The §8 acceptance test: atomic-llm provenance and a real, non-zero cost.
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(4, "Fluent prose.")), JudgeWithUsage(), name: "Fluency");
        IEval eval = adapter;

        var result = await eval.EvaluateAsync(Input);

        Assert.Equal("atomic-llm", result.Provenance.Type);
        Assert.Equal(1200, result.Provenance.TokensUsed);
        Assert.True(result.Provenance.EstimatedCost > 0, "cost must be derived from the tokens the judge spent");
        Assert.Equal(JudgeCostMap.EstimateCost("scripted", 1000, 200), result.Provenance.EstimatedCost, precision: 12);
        Assert.Equal("scripted", result.Provenance.JudgeModel);
        Assert.False(result.Provenance.CacheHit);

        // 4 on the 1..5 scale is 0.75 on 0..1; default threshold 70 → pass.
        Assert.Equal(0.75, result.Score.Value, precision: 10);
        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("none", result.Score.Severity);
        Assert.Equal(0.70, result.Score.Threshold);
        Assert.Equal("Fluent prose.", result.Details.Summary);
        Assert.Equal("Fluency", result.Metric.Name);
        Assert.Equal("meai_fluency", result.Metric.Key);
    }

    [Fact]
    public async Task MicrosoftEvaluator_IsAnIEval_WithStableIdentity()
    {
        IEval eval = MicrosoftEvaluatorAdapter.CreateCoherenceEvaluator(new FakeChatClient());

        Assert.Equal("meai_coherence", eval.Key);
        Assert.Equal("Coherence", eval.Name);
        Assert.Equal("quality.meai", eval.Category);
        Assert.Equal("1.0.0", eval.Version);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NullNumericScore_IsAnErrorLeaf_NotAZeroGrade()
    {
        // The MEAI first-class "no value produced" (unparseable judge output, content filter).
        // On the IMetric path this became Fail(score: 0) + Details["indeterminate"]. On the IEval
        // path it is the same shape AtomicLlmEval uses for a judge that could not speak.
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(null)), JudgeWithUsage(), name: "Fluency");

        var result = await ((IEval)adapter).EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal("none", result.Score.Severity);
        Assert.Equal("atomic-llm", result.Provenance.Type);
        Assert.True(result.Provenance.EstimatedCost > 0, "the judge still spent tokens on a verdict it did not give");
        Assert.Contains(result.Details.Evidence!, e => e.Source == "evaluation-error");
    }

    [Fact]
    public async Task LowNumericScore_Fails_WithScoreDerivedSeverity()
    {
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(2, "Choppy.")), JudgeWithUsage(), name: "Fluency");

        var result = await ((IEval)adapter).EvaluateAsync(Input);

        Assert.Equal(0.25, result.Score.Value, precision: 10);
        Assert.False(result.Score.Passed);
        Assert.Equal("fail", result.Score.Label);
        Assert.Equal("high", result.Score.Severity);
    }

    [Fact]
    public async Task BooleanMetric_MapsToOneOrZero()
    {
        var pass = new MicrosoftEvaluatorAdapter(
            new JudgeCallingEvaluator(() => new Microsoft.Extensions.AI.Evaluation.BooleanMetric("Safe", true), "Safe"),
            JudgeWithUsage(), name: "Safe");
        var fail = new MicrosoftEvaluatorAdapter(
            new JudgeCallingEvaluator(() => new Microsoft.Extensions.AI.Evaluation.BooleanMetric("Safe", false), "Safe"),
            JudgeWithUsage(), name: "Safe");

        var passResult = await ((IEval)pass).EvaluateAsync(Input);
        var failResult = await ((IEval)fail).EvaluateAsync(Input);

        Assert.Equal(1.0, passResult.Score.Value);
        Assert.True(passResult.Score.Passed);
        Assert.Equal(0.0, failResult.Score.Value);
        Assert.False(failResult.Score.Passed);
    }

    [Fact]
    public async Task NoUsageReported_CostIsZero_AndTokensAreNull_NotFabricated()
    {
        // A judge that reports no usage yields no tokens and no cost — the same convention as
        // AtomicLlmEval. Never invent a spend.
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(5)), new ScriptedChatClient().AddText("ok"), name: "Fluency");

        var result = await ((IEval)adapter).EvaluateAsync(Input);

        Assert.Null(result.Provenance.TokensUsed);
        Assert.Equal(0, result.Provenance.EstimatedCost);
        Assert.Equal("atomic-llm", result.Provenance.Type);
    }

    [Fact]
    public async Task JudgeThrows_IsAnErrorLeaf_NotAnUnhandledException()
    {
        var client = new FakeChatClient { ThrowOnNextCall = true, ThrowMessage = "quota exceeded" };
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(5)), client, name: "Fluency");

        var result = await ((IEval)adapter).EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains(result.Details.Evidence!, e => e.Message.Contains("quota exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingResponse_Throws_LikeAtomicLlmEval()
    {
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(5)), JudgeWithUsage());

        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IEval)adapter).EvaluateAsync(new EvalInput(Query: "q")));
    }

    [Fact]
    public async Task WorksAsALeafInsideAComposite()
    {
        // The point of the retarget: a first-party M.E.AI evaluator can now sit in a CompositeEval
        // beside AgentEval's own leaves, with its cost rolled up.
        var leaf = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(4)), JudgeWithUsage(), name: "Fluency");
        var composite = new CompositeEval("c", "C", "test", "1.0.0", new EvalComponent[] { new(leaf) }, WeightedSumAggregation.Instance, threshold: 0.5);

        var result = await composite.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(0.75, result.Score.Value, precision: 10);
        Assert.Equal(JudgeCostMap.EstimateCost("scripted", 1000, 200), result.Provenance.EstimatedCost, precision: 12);
        Assert.Equal("atomic-llm", result.Details.SubResults![0].Provenance.Type);
    }

    [Fact]
    public void CreateAllQualityEvals_ReturnsTheSixFirstPartyEvaluators_AsIEval()
    {
        var evals = MicrosoftEvaluatorExtensions.CreateAllQualityEvals(new FakeChatClient()).ToList();

        Assert.Equal(6, evals.Count);
        Assert.All(evals, e => Assert.StartsWith("meai_", e.Key, StringComparison.Ordinal));
        Assert.Equal(6, evals.Select(e => e.Key).Distinct().Count());
    }

    [Fact]
    public async Task IMetricPath_StillWorks_Unchanged()
    {
        // Non-breaking: the IMetric contract is retained and its result shape is untouched.
        var adapter = new MicrosoftEvaluatorAdapter(new JudgeCallingEvaluator(() => Numeric(4, "ok")), JudgeWithUsage(), name: "Fluency");
        IMetric metric = adapter;

        var result = await metric.EvaluateAsync(new EvaluationContext { Input = "q", Output = "a" });

        Assert.True(result.Passed);
        Assert.Equal(75, result.Score);
        Assert.Equal("Fluency", result.MetricName);
    }
}
