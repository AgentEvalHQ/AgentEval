// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H2: Performance — measure latency, throughput, and cost of a
/// real model-backed agent using <see cref="PerformanceBenchmark"/>.
/// Skips gracefully when credentials are missing.
///
/// Demonstrates:
///   1. The Convention-2 <c>EvaluateAsync(EvalInput) → EvalResult</c> adapter
///      on <see cref="PerformanceBenchmark"/> (added in v0.10.0).
///   2. Generic HTML rendering of the resulting 3-leaf composite tree (PDF is opt-in)
///      (latency / throughput / cost).
///   3. Preset-driven scaling (Smoke / Standard / AuditGrade) without code
///      changes — see <see cref="BenchmarkSampleHelpers.ResolvePreset"/>.
/// </summary>
/// <remarks>
/// Requires a model provider (see AIConfig). Skips gracefully when missing.
/// Default preset is <see cref="SamplePreset.Smoke"/> (3 latency iters + 2s
/// throughput) — set <c>AGENTEVAL_SAMPLES_PRESET=standard</c> for ~10× the
/// workload, or <c>audit-grade</c> for the full audit-grade configuration.
/// </remarks>
public static class PerformanceBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H2: Performance (latency / throughput / cost)",
            "Real model-backed agent. Skips gracefully without credentials.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H2 — PERFORMANCE");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        var (latencyIters, warmup, concurrent, throughputDur) = preset switch
        {
            SamplePreset.Standard => (10, 2, 4, TimeSpan.FromSeconds(10)),
            SamplePreset.AuditGrade => (50, 3, 8, TimeSpan.FromSeconds(30)),
            _ => (3, 1, 4, TimeSpan.FromSeconds(2)),
        };

        var agent = BenchmarkSampleHelpers.CreateAzureAgent(
            name: "PerformanceSampleAgent",
            systemPrompt: "You are a concise assistant. Keep replies under three sentences.");

        var options = new PerformanceBenchmarkOptions
        {
            EvaluateOptions = new PerformanceBenchmarkEvaluateOptions
            {
                LatencyIterationsPerPrompt = latencyIters,
                WarmupIterations = warmup,
                ThroughputConcurrentRequests = concurrent,
                ThroughputDuration = throughputDur,
                // A known operator-supplied key selects pricing. Otherwise each returned model
                // is priced individually; unavailable usage/rates remain unmeasured, never $0/PASS.
                // Reported cached input is discounted only when a cached rate is available.
                CostModelName = AIConfig.ModelDeployment,
            }
        };
        var bench = new PerformanceBenchmark(agent, options);

        var input = new EvalInput(Query: "Briefly: what is GDPR Article 17?");
        Console.WriteLine(
            $"Running PerformanceBenchmark ({latencyIters} latency iter(s), {warmup} warmup, "
            + $"{concurrent}x concurrent for {throughputDur.TotalSeconds:F0}s)...");
        var result = await bench.EvaluateAsync(input);

        var subject = new SubjectIdentity(
            SubjectKind.Agent,
            agent.Name,
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
            result, subject,
            benchmarkName: "performance",
            regulationOrBenchmark: $"AgentEval Performance Benchmark — {preset} preset",
            includePdf: false,
            regulationCodeForEvidence: null,
            presetLabel: preset.ToString().ToLowerInvariant(),
            judgeModel: AIConfig.ModelDeployment);

        BenchmarkSampleHelpers.PrintReportPaths(result, paths);
        SampleOutcome.RecordVerdict("performance benchmark", result);
        BenchmarkSampleHelpers.OfferToOpenReports(paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - Numbers reflect real model latency / throughput against your Azure deployment.");
        Console.WriteLine("   - PerformanceBenchmark.EvaluateAsync returns a 3-leaf CompositeEval");
        Console.WriteLine("     (perf_latency / perf_throughput / perf_cost) — same shape every renderer expects.");
        Console.WriteLine("   - Scale up with AGENTEVAL_SAMPLES_PRESET=standard or audit-grade,");
        Console.WriteLine("     or `--preset audit-grade` on the command line.");
    }
}
