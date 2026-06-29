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

        public IReadOnlyCollection<string> EvaluationMetricNames => ["captured"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse response,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            Captured = messages.ToList();
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
}
