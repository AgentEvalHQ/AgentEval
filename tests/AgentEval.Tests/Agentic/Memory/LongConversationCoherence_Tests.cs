// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Memory;

/// <summary>
/// Per-evaluator tests for <c>long_conversation_coherence</c>.
/// Key: long_conversation_coherence | Category: memory | CostTier: HIGH
/// </summary>
public class LongConversationCoherenceEvalTests
{
    private static IReadOnlyList<ConversationTurn> LongHistory() =>
        Enumerable.Range(1, 10)
            .SelectMany(i => new[]
            {
                new ConversationTurn("user", $"Turn {i} user message about project Alpha."),
                new ConversationTurn("assistant", $"Turn {i} assistant response about project Alpha."),
            })
            .ToList();

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("long_conversation_coherence", new FixedScoreEvaluator(100));

        Assert.Equal("long_conversation_coherence", eval.Key);
        Assert.Equal("Long Conversation Coherence", eval.Name);
        Assert.Equal("memory", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("long_conversation_coherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Can you summarize our discussion so far?",
            Response: "We have been discussing project Alpha consistently across all turns.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)LongHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"long_conversation_coherence: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("long_conversation_coherence", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What were we discussing?",
            Response: "I'm not sure what we were talking about.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)LongHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"long_conversation_coherence: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("long_conversation_coherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Summarize our discussion.", Response: "Project Alpha.");
        // No conversation_history metadata

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
