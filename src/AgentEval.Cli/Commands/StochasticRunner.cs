// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Output;

using AgentEval.Evals.Meta;

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

        // The verdict is the vote's winning LABEL, over the runs that produced one (#203 review, B6c-1). It was mapped
        // back from the vote's severity: with no counting run (every run errored, or withheld its pass) the vote's
        // (0, "none") read PASS and exited 0, and a majority of medium-severity fails read WARN.
        var counted = results.Where(r => r.Score.CountsTowardAggregate()).ToList();
        string label;
        string? note = null;
        var measurement = MeasurementState.Measured;
        if (counted.Count == 0)
        {
            var errored = results.Count(r => r.Score.Label == "error");
            label = errored > 0 ? "error" : "skipped";
            measurement = results.All(r => r.Score.CensusBucket() == MeasurementState.NotApplicable)
                ? MeasurementState.NotApplicable
                : MeasurementState.NotMeasured;
            note = $"None of the {runs} runs produced a verdict ({errored} errored, {runs - errored} skipped or withheld), " +
                   "so no verdict is reported.";
        }
        else
        {
            label = MajorityVoteAggregation.WinningLabel(counted);
            if (counted.Count < runs)
            {
                note = $"{runs - counted.Count} of {runs} runs produced no verdict; the vote rests on {counted.Count}.";
                if (label == "pass")
                {
                    // A pass on part of the runs is not the N-run pass the caller asked for.
                    label = "warn";
                    note += " A pass on part of the runs is a warn.";
                }
            }
        }

        return new EvalResult(
            Metric: new($"{benchmark.Key}.runs{runs}", $"{benchmark.Name} (×{runs} stochastic)", benchmark.Category, benchmark.Version),
            Score: new(score, null, label, label == "pass", benchmark.Threshold, severity, null) { Measurement = measurement },
            Details: new(null, null, note is null ? null : [note], results.AsReadOnly(), MajorityVoteAggregation.Instance.Name)
            {
                Summary = note,
            },
            Provenance: new("composite", null, null, null, null, results.Sum(r => r.Provenance.EstimatedCost), false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
