// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Adapters;
using AgentEval.Evals;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Xunit;
using MeaiEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MeaiIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Tests.MAF;

/// <summary>
/// The reference-based M.E.AI evaluators (Groundedness, Equivalence, Completeness) return no value without their
/// evaluator context. The adapter used to pass an empty context list on both paths, so through it those three could
/// never score: every result was an <c>error</c> leaf or an indeterminate <c>Fail</c>.
/// </summary>
public class MicrosoftEvaluatorAdapterContextTests
{
    private sealed class ContextCapturingEvaluator : MeaiIEvaluator
    {
        public List<MeaiEvaluationContext> Received { get; } = [];

        public IReadOnlyCollection<string> EvaluationMetricNames => ["Groundedness"];

        public ValueTask<MeaiEvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiEvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            Received.AddRange(additionalContext ?? []);
            var result = new MeaiEvaluationResult();
            result.Metrics["Groundedness"] = new Microsoft.Extensions.AI.Evaluation.NumericMetric("Groundedness", 4);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task TheIEvalPath_PassesTheGroundingContextAndTheGroundTruth()
    {
        var evaluator = new ContextCapturingEvaluator();
        var adapter = new MicrosoftEvaluatorAdapter(evaluator, new ScriptedChatClient());

        await adapter.EvaluateAsync(new EvalInput(
            Query: "When was invoice 4471 paid?",
            Response: "On 3 May 2026.",
            Context: "Ledger: invoice 4471 paid 2026-05-03.",
            GroundTruth: "3 May 2026"));

        Assert.Contains(evaluator.Received, c => c is GroundednessEvaluatorContext);
        Assert.Contains(evaluator.Received, c => c is EquivalenceEvaluatorContext);
        Assert.Contains(evaluator.Received, c => c is CompletenessEvaluatorContext);
    }

    [Fact]
    public async Task TheIMetricPath_PassesThemToo()
    {
        var evaluator = new ContextCapturingEvaluator();
        var adapter = new MicrosoftEvaluatorAdapter(evaluator, new ScriptedChatClient());

        await adapter.EvaluateAsync(new AgentEval.Core.EvaluationContext
        {
            Input = "q",
            Output = "a",
            Context = "the source",
            GroundTruth = "the answer",
        });

        Assert.Contains(evaluator.Received, c => c is GroundednessEvaluatorContext);
        Assert.Contains(evaluator.Received, c => c is EquivalenceEvaluatorContext);
    }

    [Fact]
    public void NothingToCompareAgainst_PassesNoContext()
    {
        Assert.Empty(MicrosoftEvaluatorAdapter.BuildAdditionalContext(null, "  "));
    }

    public static TheoryData<string, MeaiIEvaluator, string?, string?> ReferenceEvaluatorsWithoutTheirInput() => new()
    {
        { "groundedness, no context", new GroundednessEvaluator(), null, "3 May 2026" },
        { "equivalence, no reference", new EquivalenceEvaluator(), "the ledger", "  " },
        { "completeness, no reference", new CompletenessEvaluator(), "the ledger", null },
    };

    [Theory]
    [MemberData(nameof(ReferenceEvaluatorsWithoutTheirInput))]
    public async Task AReferenceEvaluatorWithoutItsInput_IsNotMeasured_OnBothPaths(
        string because, MeaiIEvaluator evaluator, string? context, string? groundTruth)
    {
        // #203 review round 16 (B12n): it failed at 0 (IMetric) or read "error" (IEval) for an input the caller did not give.
        var judge = new FakeChatClient();
        var adapter = new MicrosoftEvaluatorAdapter(evaluator, judge);

        var eval = await adapter.EvaluateAsync(new EvalInput(Query: "q", Response: "a", Context: context, GroundTruth: groundTruth));
        var metric = await adapter.EvaluateAsync(new AgentEval.Core.EvaluationContext
        {
            Input = "q", Output = "a", Context = context, GroundTruth = groundTruth,
        });

        Assert.Equal("skipped", eval.Score.Label);
        Assert.False(metric.Measured, because);
        Assert.False(metric.Passed, because);
        Assert.Empty(judge.ReceivedMessages);
    }
}
