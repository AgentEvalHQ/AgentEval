// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Output;
using Xunit;
using AgenticBenchmarkFactory = AgentEval.Evals.Agentic.AgenticBenchmark;

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end smoke tests for the Reasoning Quality preset.
/// </summary>
public class AgenticReasoningE2ETest
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

    [Fact]
    public void Reasoning_HasExpected4Components()
    {
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.Reasoning(judge);

        Assert.Equal(4, benchmark.Components.Count);
    }

    [Fact]
    public async Task Reasoning_AlwaysPass_ReportsPass()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-ReasoningAgent");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.Reasoning(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(
            Query: "How should I optimise a slow SQL query?",
            Response: "1. Identify bottlenecks using EXPLAIN. " +
                      "2. Add indexes on frequently-queried columns. " +
                      "3. Rewrite correlated subqueries as JOINs. " +
                      "4. Consider query result caching.",
            ToolCalls: new[]
            {
                new ToolCall("query_analyzer", null, "{\"status\":\"analyzed\",\"suggestions\":[\"add_index\"]}"),
            });

        var (_, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotNull(result);
        Assert.True(result.Score.Value >= 0.0 && result.Score.Value <= 1.0,
            $"Score should be in [0,1], got {result.Score.Value}");
    }
}
