// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.MAF.Evaluators;
using AgentEval.Testing;   // FakeChatClient
using Xunit;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Pins the key correctness behaviour of <see cref="AgentEvalAgentEvaluator"/>: it forwards the
/// <b>full</b> <c>EvalItem.Conversation</c> (including the assistant/tool turns) to the wrapped
/// evaluator, rather than MAF's built-in query-only split — which is what lets code-based tool metrics
/// see the real calls.
/// </summary>
public class AgentEvalAgentEvaluatorTests
{
    /// <summary>An MEAI evaluator that records the messages it was handed.</summary>
    private sealed class CapturingEvaluator : Microsoft.Extensions.AI.Evaluation.IEvaluator
    {
        public List<ChatMessage>? Captured { get; private set; }
        public List<EvaluationContext> Contexts { get; } = [];

        public IReadOnlyCollection<string> EvaluationMetricNames => ["captured"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse response,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            Captured = messages.ToList();
            Contexts.AddRange(additionalContext ?? []);
            var result = new EvaluationResult();
            result.Metrics["captured"] = new NumericMetric("captured", 5.0, "ok");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task EvaluateAsync_ForwardsFullConversation_IncludingAssistantTurn()
    {
        var capturing = new CapturingEvaluator();
        var adapter = new AgentEvalAgentEvaluator(capturing, new ChatConfiguration(new FakeChatClient("judge")));
        var item = new EvalItem("What is the weather?", "It is sunny.");

        await adapter.EvaluateAsync(new[] { item });

        Assert.NotNull(capturing.Captured);
        // The adapter forwards the WHOLE EvalItem.Conversation, not MAF's query-only split — so the
        // assistant/response turn (which the split would drop) is present, and the forwarded messages
        // match the item's conversation exactly.
        Assert.Equal(item.Conversation.ToList(), capturing.Captured!);
        Assert.Contains(capturing.Captured!, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task EvaluateAsync_ForwardsTheReferenceAndContext_AsMeaisOwnEvaluatorContextsToo()
    {
        // #203 review round 16 (B12n): only AgentEval's carriers were forwarded, so M.E.AI's Groundedness / Equivalence /
        // Completeness evaluators wrapped with AsAgentEvaluator never saw the item's context or reference.
        var capturing = new CapturingEvaluator();
        var adapter = new AgentEvalAgentEvaluator(capturing, new ChatConfiguration(new FakeChatClient("judge")));

        await adapter.EvaluateAsync([new EvalItem("q", "a") { ExpectedOutput = "REF", Context = "CTX" }]);

        Assert.Contains(capturing.Contexts, c => c is Microsoft.Extensions.AI.Evaluation.Quality.GroundednessEvaluatorContext);
        Assert.Contains(capturing.Contexts, c => c is Microsoft.Extensions.AI.Evaluation.Quality.EquivalenceEvaluatorContext);
        Assert.Contains(capturing.Contexts, c => c is AgentEvalGroundTruthContext);
        Assert.Contains(capturing.Contexts, c => c is AgentEvalRAGContext);
    }

    [Fact]
    public async Task EvaluateAsync_AWordlessExpectedOutput_IsNoReference()
    {
        // Review round 18 (L8): "?" is no reference on this path either.
        var capturing = new CapturingEvaluator();
        var adapter = new AgentEvalAgentEvaluator(capturing, new ChatConfiguration(new FakeChatClient("judge")));

        await adapter.EvaluateAsync([new EvalItem("q", "a") { ExpectedOutput = "?" }]);

        Assert.Empty(capturing.Contexts);
    }

    [Fact]
    public async Task EvaluateAsync_AMetricWithNoValueAndNoVerdict_FailsTheItem()
    {
        // Review round 18 (M1): MAF fails an item only on Interpretation.Failed or a false BooleanMetric, so M.E.AI's own
        // Equivalence evaluator without its reference — no value, an error diagnostic, no interpretation — passed it.
        var adapter = new AgentEvalAgentEvaluator(new Microsoft.Extensions.AI.Evaluation.Quality.EquivalenceEvaluator(),
            new ChatConfiguration(new FakeChatClient()));

        var results = await adapter.EvaluateAsync([new EvalItem("What is the capital of France?", "Paris.") { ExpectedOutput = "?" }]);

        Assert.False(results.AllPassed);
        var metric = Assert.Single(results.Items[0].Metrics.Values);
        Assert.True(metric.Interpretation!.Failed);
        Assert.StartsWith("No value, so no verdict", metric.Interpretation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_LeavesAScoredMetric_AndAgentEvalsNotMeasured_AsTheyAre()
    {
        var capturing = new CapturingEvaluator();   // scores 5.0 with no interpretation
        var adapter = new AgentEvalAgentEvaluator(capturing, new ChatConfiguration(new FakeChatClient("judge")));

        var results = await adapter.EvaluateAsync([new EvalItem("q", "a")]);

        Assert.Null(results.Items[0].Metrics["captured"].Interpretation);
        Assert.True(results.AllPassed);
    }
}
