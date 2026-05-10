// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Evals;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
using AgentEval.Output;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Root GraphQL <c>Query</c> type for Mission Control.
/// </summary>
/// <remarks>
/// <para>
/// Plan-08 MC1.4.0 baseline scaffolding — only the smoke-test resolvers
/// (<see cref="Version"/> and <see cref="Ping"/>) ship in this commit.
/// </para>
/// <para>
/// Subsequent plan-08 tasks add the real resolvers:
/// </para>
/// <list type="bullet">
///   <item>MC1.4.2 — solution / subjects / runs / recentRuns (read via <c>IOutputStoreReader</c>)</item>
///   <item>MC1.4.3 — runCostBreakdown</item>
///   <item>MC1.4.4 — compliance + cross-regulation</item>
///   <item>MC1.4.5 — red-team campaigns</item>
///   <item>MC1.4.6 — recursive <c>EvalResult</c> resolver with depth limit</item>
///   <item>MC1.5.3 — evaluators (driven by the in-memory <c>EvaluatorCard</c> registry)</item>
/// </list>
/// </remarks>
public sealed class Query
{
    /// <summary>
    /// Returns the AgentEval assembly version currently serving this Mission Control instance.
    /// Useful for diagnostics and SPA version-skew detection.
    /// </summary>
    public string AgentEvalVersion() =>
        typeof(AgentEval.Output.IOutputStoreReader).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Diagnostic resolver — returns the literal string <c>"pong"</c>. Used by
    /// <c>GraphQLSmokeTests</c> to verify the GraphQL endpoint accepts queries
    /// and returns well-formed responses.
    /// </summary>
    public string Ping() => "pong";

    // ─── Evaluator registry (MC1.5.3) ────────────────────────────────────────

    /// <summary>
    /// Returns all registered evaluator cards, optionally filtered by category
    /// (e.g. <c>"system"</c>, <c>"adversarial"</c>) and / or cost tier.
    /// Drives Mission Control's <c>&lt;EvaluatorRegistry/&gt;</c> page.
    /// </summary>
    public IEnumerable<EvaluatorCard> Evaluators(
        [Service] EvaluatorCardRegistry registry,
        string? category = null,
        EvaluatorCostTier? costTier = null) =>
        registry.List(category, costTier);

    /// <summary>
    /// Returns a single evaluator card by its key (e.g. <c>"task_completion"</c>),
    /// or <c>null</c> if no card is registered for that key.
    /// </summary>
    public EvaluatorCard? Evaluator(
        [Service] EvaluatorCardRegistry registry,
        string key) =>
        registry.Get(key);

    // ─── Solution / subjects / runs (MC1.4.2) ────────────────────────────────

    /// <summary>
    /// Returns the local solution info, or <c>null</c> when no <c>solution.json</c>
    /// exists (i.e. the cwd's <c>.agenteval/</c> hasn't been initialised).
    /// </summary>
    public async Task<SolutionInfo?> Solution(
        [Service] IOutputStoreReader store,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        try
        {
            return await store.EnsureSolutionAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // FileSystemOutputStore throws "no solution.json" for an uninitialised folder;
            // surface that to the SPA as `solution: null` instead of a GraphQL error.
            return null;
        }
    }

    /// <summary>
    /// Lists all known subjects in the local solution, optionally filtered by kind.
    /// </summary>
    public async IAsyncEnumerable<SubjectInfo> Subjects(
        [Service] IOutputStoreReader store,
        SubjectKind? kind = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!store.IsAvailable) yield break;
        await foreach (var s in store.ListSubjectsAsync(kind, ct))
        {
            yield return s;
        }
    }

    /// <summary>
    /// Returns a single subject by kind + name, or <c>null</c> if no subject with
    /// that pair exists.
    /// </summary>
    public async Task<SubjectInfo?> Subject(
        [Service] IOutputStoreReader store,
        SubjectKind kind,
        string name,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        await foreach (var s in store.ListSubjectsAsync(kind, ct))
        {
            if (string.Equals(s.Identity.Name, name, StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    /// <summary>
    /// Returns the most recent run pointers across all subjects in the local solution,
    /// up to <paramref name="count"/>. Default 50, max 500.
    /// </summary>
    public async IAsyncEnumerable<RunPointer> RecentRuns(
        [Service] IOutputStoreReader store,
        int count = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!store.IsAvailable) yield break;
        var clamped = Math.Clamp(count, 1, 500);
        await foreach (var p in store.GetRecentRunsAsync(clamped, ct))
        {
            yield return p;
        }
    }

    /// <summary>
    /// Returns the manifest for a specific run, or <c>null</c> if the run ID is unknown.
    /// </summary>
    public Task<RunManifest?> Run(
        [Service] IOutputStoreReader store,
        string runId,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return Task.FromResult<RunManifest?>(null);
        return store.GetRunManifestAsync(runId, ct);
    }

    /// <summary>
    /// Returns the summary (verdict, stats, metrics, cost) for a specific run, or
    /// <c>null</c> if the run ID is unknown or the run is still in progress.
    /// </summary>
    public Task<RunSummary?> RunSummary(
        [Service] IOutputStoreReader store,
        string runId,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return Task.FromResult<RunSummary?>(null);
        return store.GetRunSummaryAsync(runId, ct);
    }

    /// <summary>
    /// Streams all scenario results for a given run. Returns empty when the run is
    /// unknown or has no scenarios. The recursive <c>EvalResult</c> tree per scenario
    /// is reconstituted from <see cref="ScenarioResult.Output"/> JSON in MC1.4.6 —
    /// this resolver returns the flat scenario shape only.
    /// </summary>
    public async IAsyncEnumerable<ScenarioResult> Scenarios(
        [Service] IOutputStoreReader store,
        string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!store.IsAvailable) yield break;
        await foreach (var s in store.GetScenarioResultsAsync(runId, ct))
        {
            yield return s;
        }
    }

    /// <summary>
    /// Returns a single scenario result by run + scenario id, or <c>null</c> if absent.
    /// </summary>
    public async Task<ScenarioResult?> Scenario(
        [Service] IOutputStoreReader store,
        string runId,
        string scenarioId,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        await foreach (var s in store.GetScenarioResultsAsync(runId, ct))
        {
            if (string.Equals(s.Id, scenarioId, StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    // ─── Compliance (MC1.4.4) ────────────────────────────────────────────────

    /// <summary>
    /// Lists all regulations that have at least one evidence record stored, with
    /// per-regulation aggregate stats (subject count, total evidence count, latest
    /// evidence timestamp + overall status).
    /// </summary>
    public Task<IReadOnlyList<ComplianceRegulationSummary>> Compliance(
        [Service] ComplianceMatrixService service,
        CancellationToken ct = default) =>
        service.ListRegulationsAsync(ct);

    /// <summary>
    /// Returns the subjects × controls matrix for a regulation. The portal's
    /// <c>&lt;ComplianceMatrix/&gt;</c> Visx heatmap (plan-07 §10) renders
    /// directly from this shape.
    /// </summary>
    public Task<ComplianceMatrix> ComplianceMatrix(
        [Service] ComplianceMatrixService service,
        string regulation,
        CancellationToken ct = default) =>
        service.BuildMatrixAsync(regulation, ct);

    /// <summary>
    /// Returns a single compliance evidence document (base shape only — regulation-
    /// specific wrappers like <c>GdprComplianceEvidence</c> arrive in a follow-up
    /// once the GraphQL interface + inline-fragment support per plan-07 §8.3 lands).
    /// </summary>
    public async Task<ComplianceEvidence?> ComplianceEvidence(
        [Service] IOutputStoreReader store,
        string regulation,
        SubjectKind subjectKind,
        string subjectName,
        string timestamp,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        var identity = new SubjectIdentity(subjectKind, subjectName);
        return await store.GetComplianceEvidenceAsync(regulation, identity, timestamp, ct);
    }

    // ─── Cost-tier breakdown (MC1.4.3) ───────────────────────────────────────

    /// <summary>
    /// Returns a per-cost-tier breakdown of a run's estimated cost. Walks every
    /// scenario's recursive <see cref="EvalResult"/> tree (reconstituted from
    /// <see cref="ScenarioResult.Output"/> JSON via
    /// <see cref="EvalResultPersistence.FromScenarioResult"/>); for each leaf
    /// with an LLM or code provenance, looks up the cost tier via
    /// <see cref="EvaluatorCostMap.GetTier"/> and adds
    /// <see cref="EvalProvenance.EstimatedCost"/> to that tier's bucket.
    /// Drives the SPA's <c>&lt;CostTierBreakdownChart/&gt;</c> stacked bar.
    /// </summary>
    public async Task<RunCostBreakdown?> RunCostBreakdown(
        [Service] IOutputStoreReader store,
        string runId,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;

        var manifest = await store.GetRunManifestAsync(runId, ct);
        if (manifest is null) return null;

        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0;

        await foreach (var scenario in store.GetScenarioResultsAsync(runId, ct))
        {
            // Some scenarios are flat (Output is the agent response text); for those
            // we attribute the scenario's estimatedCost to the unknown bucket — the
            // tier-aware path requires a recursive EvalResult tree in Output JSON.
            var tree = EvalResultPersistence.FromScenarioResult(scenario);
            if (tree is null)
            {
                unknown += scenario.EstimatedCost;
                continue;
            }

            // Walk the tree, accumulating per-tier cost from every leaf's provenance.
            // Composites have EstimatedCost == 0; only LLM/code leaves carry real cost.
            // The tree is authoritative — supersedes scenario.EstimatedCost (which is
            // the root composite's provenance, often 0 for trees).
            WalkAndAccumulate(tree, ref trivial, ref low, ref medium, ref high, ref unknown);
        }

        // Invariant: total = sum(byTier) + unknown. Computed last so callers can
        // trust the relationship for visualisation (stacked-bar height = total).
        var total = trivial + low + medium + high + unknown;

        return new RunCostBreakdown(
            TotalCost: total,
            ByTier: new CostByTier(trivial, low, medium, high),
            UnknownKeyCost: unknown,
            FilteredOut: Array.Empty<string>());
    }

    private static void WalkAndAccumulate(
        EvalResult node,
        ref double trivial,
        ref double low,
        ref double medium,
        ref double high,
        ref double unknown)
    {
        var cost = node.Provenance.EstimatedCost;
        if (cost > 0)
        {
            // Lookup by metric key. Unknown keys default to Medium per
            // EvaluatorCostMap.GetTier — but we want to flag them as
            // "unknown" instead of silently lumping them into Medium.
            // Use the explicit registration check.
            if (EvaluatorCostMap.IsRegistered(node.Metric.Key))
            {
                switch (EvaluatorCostMap.GetTier(node.Metric.Key))
                {
                    case EvaluatorCostTier.Trivial: trivial += cost; break;
                    case EvaluatorCostTier.Low:     low     += cost; break;
                    case EvaluatorCostTier.Medium:  medium  += cost; break;
                    case EvaluatorCostTier.High:    high    += cost; break;
                }
            }
            else
            {
                unknown += cost;
            }
        }

        if (node.Details.SubResults is { } children)
        {
            foreach (var child in children)
            {
                WalkAndAccumulate(child, ref trivial, ref low, ref medium, ref high, ref unknown);
            }
        }
    }

    // ─── Recursive EvalResult tree (MC1.4.6) ─────────────────────────────────

    /// <summary>
    /// Returns the recursive <see cref="EvalResult"/> tree for a given scenario,
    /// reconstituted from the JSON stored in <see cref="ScenarioResult.Output"/> via
    /// <see cref="EvalResultPersistence.FromScenarioResult"/>. Returns <c>null</c>
    /// when the scenario doesn't exist or its <c>Output</c> field is not a serialised
    /// EvalResult tree (e.g., legacy flat scenarios where Output is just the agent's
    /// response text).
    /// </summary>
    /// <remarks>
    /// This is the demonstration of plan-07 §3 Challenge 1's central justification:
    /// a single GraphQL fragment walks the whole composite tree in one round-trip.
    /// Compare with REST: returning the full tree requires either a fat endpoint
    /// (large response) or a depth/cursor parameter (chatty client).
    /// </remarks>
    public async Task<EvalResult?> ScenarioTree(
        [Service] IOutputStoreReader store,
        string runId,
        string scenarioId,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        await foreach (var s in store.GetScenarioResultsAsync(runId, ct))
        {
            if (string.Equals(s.Id, scenarioId, StringComparison.Ordinal))
                return EvalResultPersistence.FromScenarioResult(s);
        }
        return null;
    }

    // ─── Per-evaluator timeline (Wave 8b / MC1.6.9) ──────────────────────────

    /// <summary>
    /// Returns recent occurrences of a specific evaluator across runs, newest
    /// first. Drives the <c>&lt;EvaluatorTimelineChart/&gt;</c> on the
    /// evaluator-detail page; for the <c>judge_drift</c>,
    /// <c>judge_agreement</c>, <c>calibration_accuracy</c>, and
    /// <c>confidence_calibration</c> cards specifically, this is the drift /
    /// calibration surface called out in plan-07 §6.1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks <see cref="IOutputStoreReader.GetRecentRunsAsync"/>; for each run
    /// loads the manifest + scenario results; for each scenario reconstitutes
    /// the recursive <see cref="EvalResult"/> tree via
    /// <see cref="EvalResultPersistence.FromScenarioResult"/> and recursively
    /// searches for a node whose <c>metric.key</c> matches the requested
    /// <paramref name="evaluatorKey"/>. The first match per run is yielded.
    /// </para>
    /// <para>
    /// Cost: O(runs × scenarios × tree-depth). For the v1 scale (≤ 100 recent
    /// runs, &lt;100 scenarios per run, ≤ 5-deep trees) this is acceptable
    /// without the GreenDonut DataLoader scaffolding from MC1.4.7. When that
    /// lands, this resolver becomes a natural batching candidate.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<EvaluatorTimelinePoint> EvaluatorTimeline(
        [Service] IOutputStoreReader store,
        string evaluatorKey,
        int count = 30,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!store.IsAvailable) yield break;
        if (string.IsNullOrWhiteSpace(evaluatorKey)) yield break;
        if (count <= 0) yield break;

        // Scan up to 4× the requested count of recent runs so we can keep
        // returning data even when the evaluator is missing from many runs.
        // Capped to 200 to bound worst-case scanning.
        var maxScan = Math.Min(Math.Max(count * 4, 50), 200);
        var returned = 0;

        await foreach (var ptr in store.GetRecentRunsAsync(maxScan, ct))
        {
            if (returned >= count) yield break;

            var manifest = await store.GetRunManifestAsync(ptr.RunId, ct);
            if (manifest is null) continue;

            EvalResult? matched = null;
            await foreach (var scenario in store.GetScenarioResultsAsync(ptr.RunId, ct))
            {
                var tree = EvalResultPersistence.FromScenarioResult(scenario);
                if (tree is null) continue;

                matched = FindEvaluatorNode(tree, evaluatorKey);
                if (matched is not null) break;
            }

            if (matched is null) continue;

            yield return new EvaluatorTimelinePoint(
                RunId: ptr.RunId,
                Timestamp: manifest.Run.Timestamp,
                SubjectKind: manifest.Subject.Kind,
                SubjectName: manifest.Subject.Name,
                Score: matched.Score.Value,
                Passed: matched.Score.Passed,
                Severity: matched.Score.Severity,
                Confidence: matched.Score.Confidence,
                ManifestHash: manifest.ContentHash);
            returned++;
        }
    }

    /// <summary>
    /// Recursively walks a recursive <see cref="EvalResult"/> tree looking for
    /// the first node whose metric key matches. Pre-order (parent before
    /// children) so a composite-of-the-same-key wins over its leaves.
    /// </summary>
    private static EvalResult? FindEvaluatorNode(EvalResult node, string key)
    {
        if (string.Equals(node.Metric.Key, key, StringComparison.Ordinal))
            return node;
        var subs = node.Details.SubResults;
        if (subs is null) return null;
        foreach (var s in subs)
        {
            var found = FindEvaluatorNode(s, key);
            if (found is not null) return found;
        }
        return null;
    }
}
