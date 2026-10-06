// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: model comparison, and stochastic runs compared across models.</summary>
public static class ModelComparisonSnippets
{
    public static async Task CompareModels(IEvaluationHarness harness, IReadOnlyList<TestCase> agenticTestSuite)
    {
        // begin-snippet: model-comparison
        var stochasticRunner = new StochasticRunner(harness);
        var comparer = new ModelComparer(stochasticRunner);

        // CreateAgent(deployment) is your code: it returns an IEvaluableAgent for that model
        var results = await comparer.CompareModelsAsync(
            factories: new IAgentFactory[]
            {
                new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
                new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini")),
                new DelegateAgentFactory("gpt-35-turbo", "GPT-3.5 Turbo", () => CreateAgent("gpt-35-turbo"))
            },
            testCases: agenticTestSuite,
            options: new ModelComparisonOptions(RunsPerModel: 5));

        Console.WriteLine(results.ToMarkdown());
        // end-snippet
    }

    public static async Task StochasticComparison(IStochasticRunner stochasticRunner, TestCase testCase)
    {
        // begin-snippet: stochastic-model-comparison
        var factories = new IAgentFactory[]
        {
            new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
            new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini"))
        };

        var modelResults = new List<(string ModelName, StochasticResult Result)>();

        foreach (var factory in factories)
        {
            var result = await stochasticRunner.RunStochasticTestAsync(
                factory, testCase,
                new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
            modelResults.Add((factory.ModelName, result));
        }

        modelResults.PrintComparisonTable();
        // end-snippet
    }

    // Stands in for the reader's own agent construction (for example chatClient.AsEvaluableAgent(...)).
    // These snippets are compiled, never run, so it is never called.
    private static IEvaluableAgent CreateAgent(string deployment) =>
        throw new NotSupportedException($"Build an IEvaluableAgent for '{deployment}' here.");
}
