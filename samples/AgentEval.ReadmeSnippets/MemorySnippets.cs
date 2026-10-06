// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Microsoft.Extensions.AI;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: the native memory benchmark, a saved baseline and its HTML report.</summary>
public static class MemorySnippets
{
    public static async Task MemoryBenchmarkRun(IChatClient chatClient)
    {
        // begin-snippet: memory
        // One-line benchmark with grade
        var runner = MemoryBenchmarkRunner.Create(chatClient);
        var agent  = chatClient.AsEvaluableAgent(name: "MemoryAgent", includeHistory: true);

        var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);
        Console.WriteLine($"Memory: {result.OverallScore:F1}% ({result.Grade})");

        // Save a baseline. SaveAsync also places the interactive HTML pentagon report
        // (report.html) in the same folder; it reads every baseline saved there.
        var store = new JsonFileBaselineStore();
        await store.SaveAsync(result.ToBaseline("GPT-4o", new AgentBenchmarkConfig { AgentName = "MemoryAgent" }));
        Console.WriteLine($"Report folder: {store.GetReportDirectory("MemoryAgent")}");
        // end-snippet
    }
}
