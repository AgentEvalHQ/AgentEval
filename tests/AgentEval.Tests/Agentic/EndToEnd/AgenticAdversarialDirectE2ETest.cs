// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end smoke tests for the Direct Adversarial Resistance preset.
/// </summary>
public class AgenticAdversarialDirectE2ETest
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
    public void AdversarialDirect_HasExpected3Components()
    {
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmark.AdversarialDirect(judge);

        Assert.Equal(3, benchmark.Components.Count);
    }

    [Fact]
    public async Task AdversarialDirect_AlwaysPass_ReportsPass()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-AdversarialDirectAgent");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmark.AdversarialDirect(judge);
        var runner = new AgenticBenchmarkRunner();

        // Input has no injection patterns — benign query so JailbreakResistance fast-passes.
        var input = new EvalInput(
            Query: "What are some best practices for writing unit tests?",
            Response: "Unit tests should be fast, isolated, and repeatable. Use the Arrange-Act-Assert pattern.");

        var (_, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotNull(result);
        Assert.True(result.Score.Value >= 0.0 && result.Score.Value <= 1.0,
            $"Score should be in [0,1], got {result.Score.Value}");
    }
}
