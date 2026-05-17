// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B2: Performance — measure latency, throughput, and cost of an
/// agent using <see cref="PerformanceBenchmark"/>. This sample uses an
/// in-process <c>EchoAgent</c> stub so it runs without Azure OpenAI
/// credentials.
///
/// Demonstrates:
///   1. The Convention-2 <c>EvaluateAsync(EvalInput) → EvalResult</c> adapter
///      on <see cref="PerformanceBenchmark"/> (added in v0.10.0).
///   2. Generic HTML rendering of the resulting 3-leaf composite tree
///      (latency / throughput / cost).
///   3. The unified output layout: <c>JSON</c> + <c>HTML</c> side-by-side.
/// </summary>
/// <remarks>
/// No Azure OpenAI credentials required — uses an in-process EchoAgent stub.
/// </remarks>
public static class PerformanceBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B2: Performance (latency / throughput / cost)",
            "Uses an in-process EchoAgent — no Azure credentials required.");

        var agent = new EchoAgent("EchoStub");
        var options = new PerformanceBenchmarkOptions
        {
            EvaluateOptions = new PerformanceBenchmarkEvaluateOptions
            {
                LatencyIterationsPerPrompt = 3,
                WarmupIterations = 1,
                ThroughputConcurrentRequests = 4,
                ThroughputDuration = TimeSpan.FromSeconds(2),
            }
        };
        var bench = new PerformanceBenchmark(agent, options);

        var input = new EvalInput(Query: "Hello, world!");
        Console.WriteLine("Running PerformanceBenchmark (3 latency iterations, 2s throughput)...");
        var result = await bench.EvaluateAsync(input);

        var subject = new SubjectIdentity(SubjectKind.Agent, agent.Name, Framework: "EchoStub");
        var paths = await BenchmarkSampleHelpers.WriteReportsAsync(
            result, subject,
            benchmarkName: "performance",
            regulationOrBenchmark: "AgentEval Performance Benchmark",
            includePdf: false);

        BenchmarkSampleHelpers.PrintReportPaths(result, paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - PerformanceBenchmark.EvaluateAsync produces a CompositeEval-shaped EvalResult");
        Console.WriteLine("     with three leaves: perf_latency, perf_throughput, perf_cost.");
        Console.WriteLine("   - The same shape flows through the HTML renderer that any other benchmark uses.");
        Console.WriteLine("   - For richer scenarios pass a real Azure-backed agent + EvalInput.Metadata[\"prompts\"].");
    }

    // ── In-process EchoAgent stub ─────────────────────────────────────────────

    /// <summary>Echoes the prompt back after a tiny synthetic delay so latency is measurable.</summary>
    private sealed class EchoAgent : IStreamableAgent
    {
        public string Name { get; }
        public EchoAgent(string name) => Name = name;

        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            return new AgentResponse
            {
                Text = $"echo: {prompt}",
                ModelId = "echo-stub",
                TokenUsage = new TokenUsage { PromptTokens = prompt.Length / 4, CompletionTokens = prompt.Length / 4 + 4 },
            };
        }

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(2, cancellationToken);
            yield return new AgentResponseChunk { Text = $"echo: {prompt}" };
            yield return new AgentResponseChunk { IsComplete = true };
        }
    }
}
