// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;

namespace AgentEval.Evals.Performance;

/// <summary>
/// Module-initializer hook that registers <see cref="PerformanceBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// <para>
/// Shape B registration. <see cref="PerformanceBenchmark"/> is an instance class
/// (it owns an <see cref="IEvaluableAgent"/> reference), so the registry's
/// <c>CompositeFactory</c> shape (which materialises a <c>CompositeEval</c> on demand)
/// does not fit cleanly. Instead, the registry exposes a Shape B
/// <see cref="BenchmarkFamily.RunnerFactory"/> that returns the typed
/// <see cref="PerformanceBenchmark"/> instance bound to a stub agent provided by the
/// caller — see <see cref="CreateBoundRunner(IEvaluableAgent, string)"/> for the canonical
/// runner-binding factory used by the CLI.
/// </para>
/// <para>
/// The Convention-2 <see cref="BenchmarkFamily.EvaluateAsync"/> adapter pulls the agent
/// from <c>EvalInput.Metadata["agent"]</c> — same convention used by the OWASP/MITRE
/// runs — and delegates to <see cref="PerformanceBenchmark.EvaluateAsync(EvalInput, CancellationToken)"/>.
/// </para>
/// </remarks>
internal static class PerformanceBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "perf",
            description: "Performance benchmark — latency, throughput, cost (Phase-3 EvaluateAsync adapter)",
            defaultCostTier: CostTier.Low,
            presets:
            [
                new("latency", "P99 latency measurement (3 iterations × N prompts + warmup)", CostTier.Low),
                new("throughput", "Concurrent throughput measurement (default 2 workers × 5s)", CostTier.Low),
                new("cost", "Per-prompt token + cost estimate (pricing-table-backed)", CostTier.Low),
            ],
            // No CompositeFactory: PerformanceBenchmark.EvaluateAsync needs a bound IEvaluableAgent.
            runnerType: typeof(PerformanceBenchmark),
            runnerFactory: preset => CreateUnboundConfig(preset),
            evaluateAsync: async (input, judge, ct) =>
            {
                // Agent comes via Metadata["agent"]. Same convention as OWASP/MITRE.
                IEvaluableAgent? agent = null;
                if (input.Metadata?.TryGetValue("agent", out var rawAgent) == true)
                    agent = rawAgent as IEvaluableAgent;
                if (agent is null)
                {
                    return BuildSkippedResult(
                        "perf",
                        "PerformanceBenchmark requires an IEvaluableAgent at EvalInput.Metadata[\"agent\"].");
                }
                var bench = new PerformanceBenchmark(agent, new PerformanceBenchmarkOptions { Verbose = false });
                return await bench.EvaluateAsync(input, ct);
            },
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/perf-benchmark.md",
            owningAssemblyName: typeof(PerformanceBenchmark).Assembly.GetName().Name));
    }

    /// <summary>
    /// Convenience factory that creates a <see cref="PerformanceBenchmark"/> bound to
    /// <paramref name="agent"/>, with options preconfigured for the given preset.
    /// </summary>
    public static PerformanceBenchmark CreateBoundRunner(IEvaluableAgent agent, string preset)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        var opts = OptionsForPreset(preset);
        return new PerformanceBenchmark(agent, opts);
    }

    /// <summary>Returns a config object documenting the preset's shape without an agent attached.</summary>
    private static object CreateUnboundConfig(string preset) => OptionsForPreset(preset);

    private static PerformanceBenchmarkOptions OptionsForPreset(string preset)
    {
        var opts = new PerformanceBenchmarkOptions { Verbose = false };
        return preset.Trim().ToLowerInvariant() switch
        {
            "latency"    => opts,  // Latency uses the defaults (P99 threshold 5000 ms).
            "throughput" => opts,
            "cost"       => opts,
            _ => throw new ArgumentException(
                $"Unknown perf preset '{preset}'. Known presets: latency, throughput, cost.")
        };
    }

    private static EvalResult BuildSkippedResult(string key, string reason) =>
        new EvalResult(
            Metric: new EvalMetadata(key, "Performance Benchmark", "performance", "1.0.0"),
            Score: new EvalScore(0.0, null, "skipped", false, null, "none", null),
            Details: new EvalDetails(null, null, new[] { reason }, null, null),
            Provenance: new EvalProvenance("skipped", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
}
