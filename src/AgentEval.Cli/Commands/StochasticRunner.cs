// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// CLI-internal helper for stochastic (multi-run) benchmarking via <c>--runs N</c>.
/// Runs a <see cref="CompositeEval"/> benchmark N times sequentially against the same
/// <see cref="EvalInput"/> (temperature variance provides the stochastic spread),
/// then aggregates the N composite results via <see cref="MajorityVoteAggregation"/>.
/// Each individual run is persisted to the output store; the returned outer result
/// contains all N run results as sub-results.
/// </summary>
/// <remarks>
/// v1 tradeoff: runs are sequential, not parallel, to keep cost predictable and avoid
/// hitting rate limits. Parallel runs are a future enhancement.
/// Each run produces its own full manifest in the output store, so the store will contain
/// N separate run manifests plus the stochastic aggregate result (returned to the caller
/// but not separately persisted — the caller is responsible for reporting).
/// </remarks>
internal static class StochasticBenchRunner
{
    /// <summary>
    /// Executes <paramref name="benchmark"/> <paramref name="runs"/> times sequentially,
    /// aggregates via <see cref="MajorityVoteAggregation"/>, and returns a synthetic
    /// top-level <see cref="EvalResult"/> whose sub-results are the N individual composite results.
    /// </summary>
    public static async Task<EvalResult> RunNAsync(
        IOutputStore store,
        SubjectIdentity subject,
        CompositeEval benchmark,
        EvalInput input,
        int runs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(input);
        if (runs < 1) throw new ArgumentOutOfRangeException(nameof(runs), "runs must be >= 1.");

        var results = new List<EvalResult>(runs);
        var components = new List<EvalComponent>(runs);
        var runner = new GdprBenchmarkRunner();

        for (int i = 0; i < runs; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (_, r) = await runner.RunAsync(store, subject, benchmark, input, ct: ct);
            results.Add(r);
            components.Add(new EvalComponent(benchmark, 1.0, true));
        }

        var (score, severity) = MajorityVoteAggregation.Instance.Aggregate(results, components);
        var label = severity switch
        {
            "critical" or "high" => "fail",
            "medium" => "warn",
            _ => "pass"
        };

        return new EvalResult(
            Metric: new($"{benchmark.Key}.runs{runs}", $"{benchmark.Name} (×{runs} stochastic)", benchmark.Category, benchmark.Version),
            Score: new(score, null, label, label == "pass", benchmark.Threshold, severity, null),
            Details: new(null, null, null, results.AsReadOnly(), MajorityVoteAggregation.Instance.Name),
            Provenance: new("composite", null, null, null, null, results.Sum(r => r.Provenance.EstimatedCost), false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
