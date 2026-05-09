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
}
