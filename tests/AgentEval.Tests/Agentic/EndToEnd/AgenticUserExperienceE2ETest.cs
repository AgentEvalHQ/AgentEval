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
/// End-to-end smoke tests for the User Experience preset.
/// </summary>
public class AgenticUserExperienceE2ETest
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
    public void UserExperience_HasExpected5Components()
    {
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.UserExperience(judge);

        Assert.Equal(5, benchmark.Components.Count);
    }

    [Fact]
    public async Task UserExperience_AlwaysPass_ReportsPass()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-UserExperienceAgent");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.UserExperience(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(
            Query: "Can you explain what an API is?",
            Response: "An API (Application Programming Interface) is a set of rules that allows different " +
                      "software programs to communicate with each other. Think of it like a waiter in a " +
                      "restaurant — you (the client) give an order, the waiter (API) takes it to the kitchen " +
                      "(server), and brings back the result.");

        var (_, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotNull(result);
        Assert.True(result.Score.Value >= 0.0 && result.Score.Value <= 1.0,
            $"Score should be in [0,1], got {result.Score.Value}");
    }
}
