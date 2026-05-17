// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end smoke tests for the Conversational Quality preset.
/// </summary>
public class AgenticConversationalE2ETest
{
    private sealed class StubEvaluator(int score) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult
            {
                OverallScore = score,
                Summary = "stub",
                CriteriaResults = criteria
                    .Select(c => new CriterionResult { Criterion = c, Met = score >= 50, Explanation = "stub" })
                    .ToList()
            });
    }

    private static IReadOnlyList<ConversationTurn> SampleHistory() =>
        new List<ConversationTurn>
        {
            new("user", "My name is Alice and I want to plan a trip to Japan."),
            new("assistant", "Hello Alice! I'd be happy to help you plan your Japan trip."),
            new("user", "I prefer to travel in spring."),
            new("assistant", "Spring is wonderful in Japan — cherry blossom season is typically late March to early April."),
        };

    [Fact]
    public void Conversational_HasExpected5Components()
    {
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmark.Conversational(judge);

        Assert.Equal(5, benchmark.Components.Count);
    }

    [Fact]
    public async Task Conversational_AlwaysPass_ReportsPass()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-ConversationalAgent");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmark.Conversational(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(
            Query: "What is my name and when should I visit Japan?",
            Response: "Your name is Alice and you should visit Japan in spring for the cherry blossoms.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)SampleHistory(),
            });

        var (_, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotNull(result);
        Assert.True(result.Score.Value >= 0.0 && result.Score.Value <= 1.0,
            $"Score should be in [0,1], got {result.Score.Value}");
    }
}
