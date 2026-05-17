// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Evals.Agentic.Composition;

/// <summary>
/// Drives an agentic benchmark composite (e.g. <see cref="AgenticBenchmark.AgenticExecution"/>) end-to-end
/// against an <see cref="IOutputStore"/>. Runs the composite, walks the result tree, persists each leaf
/// scenario result via <see cref="EvalResultPersistence"/>, and writes a run summary.
/// </summary>
public sealed class AgenticBenchmarkRunner
{
    /// <summary>
    /// Executes <paramref name="benchmark"/> against <paramref name="input"/>, persists
    /// all leaf scenario results to <paramref name="store"/>, and returns the run ID together
    /// with the composite <see cref="EvalResult"/>.
    /// </summary>
    /// <param name="store">Output store that receives the run manifest, scenario results, and summary.</param>
    /// <param name="subject">Identity of the agent or workflow under evaluation.</param>
    /// <param name="benchmark">The composite eval to run (typically built by <see cref="AgenticBenchmark"/>).</param>
    /// <param name="input">Eval input forwarded to every sub-eval.</param>
    /// <param name="context">Optional run context; a default agentic benchmark context is used when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the run ID (for passing to reporters) and the composite <see cref="EvalResult"/>
    /// after all sub-evals have completed.
    /// </returns>
    public async Task<(string RunId, EvalResult Result)> RunAsync(
        IOutputStore store,
        SubjectIdentity subject,
        CompositeEval benchmark,
        EvalInput input,
        RunContext? context = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(input);

        context ??= new RunContext(
            EvalProject: "AgentEval.Evals.Agentic",
            EvalProjectPath: "src/AgentEval.Evals.Agentic/",
            Harness: "AgenticBenchmarkRunner",
            Seed: null,
            ParentInvocationId: null,
            Kind: "benchmark");

        var manifest = await store.StartRunAsync(subject, context, ct);

        // Run the composite — sub-evals fire in parallel via Task.WhenAll inside CompositeEval.
        var result = await benchmark.EvaluateAsync(input, ct);

        // Persist each leaf scenario result.
        foreach (var (scenarioId, leafResult) in EnumerateAtomicLeaves(result))
        {
            var sr = EvalResultPersistence.ToScenarioResult(leafResult, scenarioId, leafResult.Metric.Name);
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

    private static RunSummary BuildSummary(EvalResult root, string runId)
    {
        var leaves = EnumerateAtomicLeaves(root).ToList();
        var passed   = leaves.Count(l => l.Result.Score.Passed);
        var failed   = leaves.Count(l => !l.Result.Score.Passed && l.Result.Score.Label != "warn");
        var warnings = leaves.Count(l => l.Result.Score.Label == "warn");
        var stats = new RunStats(leaves.Count, passed, failed, warnings);

        var verdict = root.Score.Label.ToUpperInvariant() switch
        {
            "PASS" => "PASS",
            "WARN" => "WARN",
            _      => "FAIL"
        };

        return new RunSummary(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: verdict,
            Stats: stats,
            Metrics: new Dictionary<string, double> { ["overallScore"] = root.Score.Value });
    }
}
