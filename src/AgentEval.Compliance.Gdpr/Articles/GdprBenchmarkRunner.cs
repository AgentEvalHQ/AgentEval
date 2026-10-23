// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Compliance.Gdpr.Articles;

/// <summary>
/// Drives a GDPR benchmark composite (Smoke / Standard / etc.) end-to-end against
/// an <see cref="IOutputStore"/>. Runs the composite, walks the result tree, persists
/// each leaf scenario result via <see cref="EvalResultPersistence"/>, and writes a
/// run summary.
/// </summary>
public sealed class GdprBenchmarkRunner
{
    /// <summary>
    /// Executes <paramref name="benchmark"/> against <paramref name="input"/>, persists
    /// all leaf scenario results to <paramref name="store"/>, and returns the run ID together
    /// with the composite <see cref="EvalResult"/>.
    /// </summary>
    /// <param name="store">Output store that receives the run manifest, scenario results, and summary.</param>
    /// <param name="subject">Identity of the agent or workflow under evaluation.</param>
    /// <param name="benchmark">The composite eval to run (typically built by <see cref="AgentEval.Compliance.Gdpr.Articles.Building.ArticleCompositeBuilder"/>).</param>
    /// <param name="input">Eval input forwarded to every sub-eval.</param>
    /// <param name="context">Optional run context; a default GDPR benchmark context is used when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the run ID (for passing to reporters) and the composite <see cref="EvalResult"/>
    /// after all sub-evals have completed.
    /// </returns>
    public Task<(string RunId, EvalResult Result)> RunAsync(
        IOutputStore store,
        SubjectIdentity subject,
        CompositeEval benchmark,
        EvalInput input,
        RunContext? context = null,
        CancellationToken ct = default) =>
        RunCoreAsync(store, subject, benchmark, input, context, ct, null, null);

    /// <summary>
    /// Runs and persists the benchmark while reporting completed nodes and optional start notifications.
    /// Paths and callback concurrency follow <see cref="CompositeEval.EvaluateWithProgressAsync"/>.
    /// </summary>
    public Task<(string RunId, EvalResult Result)> RunWithProgressAsync(
        IOutputStore store, SubjectIdentity subject, CompositeEval benchmark, EvalInput input,
        Action<IReadOnlyList<int>, EvalResult> onCompleted, Action<IReadOnlyList<int>>? onStarted = null,
        RunContext? context = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onCompleted);
        return RunCoreAsync(store, subject, benchmark, input, context, ct, onCompleted, onStarted);
    }

    /// <summary>
    /// Reports the persisted manifest identity before evaluation, then streams node progress.
    /// The run-created callback is synchronous and also identifies runs interrupted before completion.
    /// </summary>
    public Task<(string RunId, EvalResult Result)> RunWithProgressAsync(
        IOutputStore store, SubjectIdentity subject, CompositeEval benchmark, EvalInput input,
        Action<string> onRunCreated, Action<IReadOnlyList<int>, EvalResult> onCompleted,
        Action<IReadOnlyList<int>>? onStarted = null, RunContext? context = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onRunCreated);
        ArgumentNullException.ThrowIfNull(onCompleted);
        return RunCoreAsync(store, subject, benchmark, input, context, ct, onCompleted, onStarted, onRunCreated);
    }

    private async Task<(string RunId, EvalResult Result)> RunCoreAsync(
        IOutputStore store, SubjectIdentity subject, CompositeEval benchmark, EvalInput input,
        RunContext? context, CancellationToken ct, Action<IReadOnlyList<int>, EvalResult>? onCompleted,
        Action<IReadOnlyList<int>>? onStarted, Action<string>? onRunCreated = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(input);

        context ??= new RunContext(
            EvalProject: "AgentEval.Compliance.Gdpr",
            EvalProjectPath: "src/AgentEval.Compliance.Gdpr/",
            Harness: "GdprBenchmarkRunner",
            Seed: null,
            ParentInvocationId: null,
            Kind: "compliance");

        var manifest = await store.StartRunAsync(subject, context, ct);
        onRunCreated?.Invoke(manifest.Run.RunId);

        // Run the composite — sub-evals fire in parallel via Task.WhenAll inside CompositeEval.
        var result = onCompleted is null
            ? await benchmark.EvaluateAsync(input, ct)
            : await benchmark.EvaluateWithProgressAsync(input, onCompleted, ct, onStarted);

        // Persist each leaf scenario result.
        foreach (var (scenarioId, leafResult) in EnumerateAtomicLeaves(result))
        {
            // A scenario may replace the composite's input. Persist only the leaf's observation;
            // a custom evaluator that reports none leaves the stimulus and subject model unknown.
            var sr = EvalResultPersistence.ToScenarioResult(
                leafResult, scenarioId, leafResult.Metric.Name);
            await store.WriteScenarioResultAsync(manifest.Run.RunId, sr, ct);
        }

        // Build a RunSummary from the composite's verdict + roll-up stats.
        var summary = BuildSummary(result, manifest.Run.RunId);
        await store.CompleteRunAsync(manifest, summary, ct);

        return (manifest.Run.RunId, result);
    }

    private static IEnumerable<(string Id, EvalResult Result)> EnumerateAtomicLeaves(EvalResult node)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0)
        {
            // Leaf node — yield this result directly.
            yield return (node.Metric.Key, node);
            yield break;
        }
        // Recurse into children.
        foreach (var child in subs)
            foreach (var leaf in EnumerateAtomicLeaves(child))
                yield return leaf;
    }

    internal static RunSummary BuildSummary(EvalResult root, string runId)
    {
        var leaves = EnumerateAtomicLeaves(root).ToList();
        // One bucket per leaf, by the shared exclusive chain (#203 review, B8): not measured (skipped, errored,
        // inapplicable — ADR-030 keeps the schema's single bucket for NotApplicable and NotMeasured) → Skipped, else warn →
        // Warnings, else Passed decides. Four independent counts let a leaf land in two buckets (a warn that was not
        // measured, a passed one that was skipped), so the buckets could add up to more than Total.
        var stats = leaves.Select(l => l.Result.Score).ToRunStats();

        // A root that errored or measured nothing is not a FAIL (B9b): PENDING when nothing was measured, else WARN.
        var verdict = root.Score.RunVerdict(stats);

        // The root's score only when it is a reading (T13): a root that errored or measured nothing wrote overallScore 0
        // beside its PENDING verdict.
        return new RunSummary(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: verdict,
            Stats: stats,
            Metrics: root.Score.MeasuredValue() is { } overallScore
                ? new Dictionary<string, double> { ["overallScore"] = overallScore }
                : new Dictionary<string, double>());
    }
}
