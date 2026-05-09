// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Output;
using Xunit;
using AgenticBenchmarkFactory = AgentEval.Evals.Agentic.Composition.AgenticBenchmark;

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end tests for the RAG Quality preset.
/// Validates composite tree shape and that an always-pass stub judge produces a passing result.
/// </summary>
public class AgenticRagQualityE2ETest
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
    public void RagQuality_HasExpectedComponents()
    {
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.RagQuality(judge);

        // RagQuality preset defines 7 top-level component evaluators (flat tree, no nested QaComposite).
        Assert.Equal(7, benchmark.Components.Count);
    }

    [Fact]
    public async Task RagQuality_AlwaysPass_ReportsPass()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-RagQualityAgent");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.RagQuality(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(
            Query: "What are the main causes of climate change?",
            Response: "Climate change is primarily caused by fossil fuel combustion, deforestation, and industrial emissions.",
            Context: "IPCC reports identify these as the primary drivers of climate change.",
            GroundTruth: "Fossil fuel combustion, deforestation, and industrial emissions are the primary causes.");

        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotEmpty(runId);
        Assert.NotNull(result);
        Assert.True(result.Score.Passed,
            $"RagQuality with always-pass stub should produce Passed==true, got score={result.Score.Value}");
    }
}
