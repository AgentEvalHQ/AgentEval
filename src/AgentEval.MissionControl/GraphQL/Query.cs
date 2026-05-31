// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap relocated here.
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
    /// (e.g. <c>"system-outcome"</c>, <c>"adversarial"</c>) and / or cost tier.
    /// Drives Mission Control's <c>&lt;EvaluatorRegistry/&gt;</c> page.
    /// See <c>evaluator-card.schema.json</c> for the canonical category enum.
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

    // ─── Workspace state (MC1.10.1 first-run landing) ────────────────────────

    /// <summary>
    /// Returns workspace bootstrap state — whether the resolved workspace has
    /// an initialised <c>.agenteval/</c> folder, the root path, and the
    /// AgentEval version. Drives the SPA's first-run landing page: when
    /// <see cref="WorkspaceState.Initialized"/> is <c>false</c> the SPA renders
    /// "run agenteval init" instead of an empty dashboard.
    /// </summary>
    public async Task<WorkspaceState> Workspace(
        [Service] IOutputStoreReader store,
        CancellationToken ct = default)
    {
        var version = typeof(IOutputStoreReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var root = store.WorkspaceRoot;

        // The store reports `IsAvailable=false` until solution.json + at
        // least the .agenteval/ folder are present. Treat anything else as
        // uninitialised — even if the folder physically exists. This matches
        // what the user expects from `agenteval init`'s post-condition.
        if (!store.IsAvailable)
        {
            return new WorkspaceState(
                Initialized: false,
                Root: root,
                Solution: null,
                AgentEvalVersion: version);
        }

        SolutionInfo? solution = null;
        try
        {
            solution = await store.EnsureSolutionAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // Solution missing → treat as not yet initialised.
            return new WorkspaceState(
                Initialized: false,
                Root: root,
                Solution: null,
                AgentEvalVersion: version);
        }

        return new WorkspaceState(
            Initialized: true,
            Root: root,
            Solution: solution,
            AgentEvalVersion: version);
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
    /// T3.8 (2026-05-25) — Relay-shaped paginated variant of <see cref="Subjects"/>.
    /// Default page size 50, max 200. Cursors are opaque (base64 index); pass
    /// the previous response's <c>pageInfo.endCursor</c> back as <paramref name="after"/>
    /// to advance. Returns an empty page (no edges, hasNextPage=false) when the
    /// store is unavailable. The Connection shape gives the SPA bounded payload
    /// sizes for large workspaces without forcing a deprecation of the
    /// existing <see cref="Subjects"/> resolver.
    /// </summary>
    public async Task<Connection<SubjectInfo>> SubjectsConnection(
        [Service] IOutputStoreReader store,
        SubjectKind? kind = null,
        int? first = null,
        string? after = null,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable)
        {
            return new Connection<SubjectInfo>(
                Array.Empty<Edge<SubjectInfo>>(),
                new PageInfo(false, false, null, null),
                TotalCount: 0);
        }

        // Materialise + sort once. Subjects are bounded by ListSubjectsAsync;
        // for the v1 scale (≤ thousands of subjects per workspace) the up-front
        // materialisation is cheap and gives cursor stability across page calls.
        var subjects = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(kind, ct))
        {
            subjects.Add(s);
        }
        subjects.Sort((a, b) => string.CompareOrdinal(a.Identity.Name, b.Identity.Name));
        return Pagination.Paginate(subjects, first, after);
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
        if (!FileSystemLayout.IsSafePathSegment(name)) return null;
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
        if (!FileSystemLayout.IsSafePathSegment(runId)) return Task.FromResult<RunManifest?>(null);
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
        if (!FileSystemLayout.IsSafePathSegment(runId)) return Task.FromResult<RunSummary?>(null);
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
        if (!FileSystemLayout.IsSafePathSegment(runId)) yield break;
        await foreach (var s in store.GetScenarioResultsAsync(runId, ct))
        {
            yield return s;
        }
    }

    /// <summary>
    /// T3.8 (2026-05-25) — Relay-shaped paginated variant of <see cref="Scenarios"/>.
    /// Default page size 50, max 200. The original (non-paginated) resolver is
    /// retained so the SPA's existing run-detail page keeps working unchanged.
    /// Returns an empty page when the run id is unknown or unsafe.
    /// </summary>
    public async Task<Connection<ScenarioResult>> ScenarioResultsConnection(
        [Service] IOutputStoreReader store,
        string runId,
        int? first = null,
        string? after = null,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable || !FileSystemLayout.IsSafePathSegment(runId))
        {
            return new Connection<ScenarioResult>(
                Array.Empty<Edge<ScenarioResult>>(),
                new PageInfo(false, false, null, null),
                TotalCount: 0);
        }

        // Materialise scenarios for cursor stability. Runs are bounded by
        // per-run scenario count (≤ 100s for v1); large compliance runs may
        // approach 1000 — still cheap relative to the LLM-judge cost that
        // produced them. Sort by Id for deterministic pagination.
        var scenarios = new List<ScenarioResult>();
        await foreach (var s in store.GetScenarioResultsAsync(runId, ct))
        {
            scenarios.Add(s);
        }
        scenarios.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Pagination.Paginate(scenarios, first, after);
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
        if (!FileSystemLayout.IsSafePathSegment(runId)) return null;
        if (!FileSystemLayout.IsSafePathSegment(scenarioId)) return null;
        await foreach (var s in store.GetScenarioResultsAsync(runId, ct))
        {
            if (string.Equals(s.Id, scenarioId, StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    // ─── Red-team campaigns (MC1.4.5 / T3.6, 2026-05-25) ─────────────────────

    /// <summary>
    /// T3.6 (2026-05-25) — lists all red-team campaign manifests stored under
    /// <c>.agenteval/red-team/</c>. Returns an empty list when the store is
    /// unavailable or no campaigns exist. Drives the SPA <c>/red-team</c> page.
    /// </summary>
    public async IAsyncEnumerable<RedTeamCampaignManifest> RedTeamCampaigns(
        [Service] IOutputStoreReader store,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!store.IsAvailable) yield break;
        await foreach (var campaign in store.ListRedTeamCampaignsAsync(ct))
        {
            yield return campaign;
        }
    }

    /// <summary>
    /// T3.6 (2026-05-25) — fetches a single red-team campaign by id, or
    /// <c>null</c> when the id is unknown / unsafe.
    /// </summary>
    public Task<RedTeamCampaignManifest?> RedTeamCampaign(
        [Service] IOutputStoreReader store,
        string id,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return Task.FromResult<RedTeamCampaignManifest?>(null);
        if (!FileSystemLayout.IsSafePathSegment(id)) return Task.FromResult<RedTeamCampaignManifest?>(null);
        return store.GetRedTeamCampaignAsync(id, ct);
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
    /// Returns a single compliance evidence document together with its per-doc
    /// audit-chain verdict. Plan-07 §7 requires every evidence read to enforce
    /// that <c>evidence.SourceRun.ManifestHash</c> matches the actual source
    /// <c>RunManifest.ContentHash</c>; this resolver makes the verdict explicit
    /// so the SPA can surface a tampering warning on the evidence-detail page.
    /// (Regulation-specific wrappers like <c>GdprComplianceEvidence</c> arrive
    /// in a follow-up once the GraphQL interface + inline-fragment support per
    /// plan-07 §8.3 lands.)
    /// </summary>
    /// <remarks>
    /// <c>ChainBreakReason</c> values:
    /// <list type="bullet">
    ///   <item><c>null</c> — chain valid; the stored manifest hash matches the recomputed source-run content hash.</item>
    ///   <item><c>"source-run-not-found"</c> — evidence references a run that no longer exists under <c>runs/&lt;run-id&gt;/manifest.json</c>.</item>
    ///   <item><c>"hash-mismatch"</c> — the source run exists, but its <c>ContentHash</c> differs from <c>evidence.SourceRun.ManifestHash</c> (tamper signal).</item>
    /// </list>
    /// The resolver returns the evidence WITH the broken-chain bit set rather
    /// than throwing or returning <c>null</c>, so the SPA can render a visible
    /// warning instead of silently failing.
    /// </remarks>
    public async Task<ComplianceEvidenceWithChain?> ComplianceEvidence(
        [Service] IOutputStoreReader store,
        string regulation,
        SubjectKind subjectKind,
        string subjectName,
        string timestamp,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        if (!FileSystemLayout.IsSafePathSegment(regulation)) return null;
        if (!FileSystemLayout.IsSafePathSegment(subjectName)) return null;
        if (!FileSystemLayout.IsSafePathSegment(timestamp)) return null;
        var identity = new SubjectIdentity(subjectKind, subjectName);
        var evidence = await store.GetComplianceEvidenceAsync(regulation, identity, timestamp, ct);
        if (evidence is null) return null;

        // Per-doc audit-chain check (plan-07 §7). Mirrors the verdict
        // ComplianceMatrixService.BuildMatrixAsync already computes for the
        // aggregated matrix, surfaced here per-document so the evidence-detail
        // page can render a visible tampering warning.
        //
        // Defense-in-depth: `evidence.SourceRun.RunId` is read from the
        // workspace's own evidence.json. A hostile workspace could ship a
        // path-traversal payload (e.g. `../../../etc/passwd`) hoping
        // `GetRunManifestAsync` reads outside the workspace. The store layer
        // does its own scoping, but a path-segment guard here closes the
        // theoretical hole at the resolver boundary too (Phase-0 gap-review
        // concern #2 / 2026-05-13). If the segment is unsafe, treat it as
        // source-run-not-found rather than throwing — same UX as the
        // legitimately-orphaned-evidence case.
        if (!FileSystemLayout.IsSafePathSegment(evidence.SourceRun.RunId))
            return new ComplianceEvidenceWithChain(evidence, ChainValid: false, ChainBreakReason: "source-run-not-found");

        var manifest = await store.GetRunManifestAsync(evidence.SourceRun.RunId, ct);
        string? breakReason = manifest switch
        {
            null => "source-run-not-found",
            _ when !string.Equals(manifest.ContentHash, evidence.SourceRun.ManifestHash, StringComparison.Ordinal)
                => "hash-mismatch",
            _ => null,
        };
        return new ComplianceEvidenceWithChain(evidence, breakReason is null, breakReason);
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
    /// <remarks>
    /// PERF-05 (accept-and-document): the scenario trees are reconstituted and walked uncached on every
    /// request. Cost is O(scenarios × tree-depth) but bounded to a SINGLE run (unlike
    /// <see cref="EvaluatorTimeline"/>, which scans up to 200), so for the v1 scale this is acceptable
    /// without per-request tree caching. Folding this into the GreenDonut DataLoader batching / a
    /// parsed-tree cache is deferred to MC1.4.7 alongside the other tree-walking resolvers.
    /// </remarks>
    public async Task<RunCostBreakdown?> RunCostBreakdown(
        [Service] IOutputStoreReader store,
        string runId,
        CancellationToken ct = default)
    {
        if (!store.IsAvailable) return null;
        if (!FileSystemLayout.IsSafePathSegment(runId)) return null;

        var manifest = await store.GetRunManifestAsync(runId, ct);
        if (manifest is null) return null;

        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0, legacyFlat = 0;

        await foreach (var scenario in store.GetScenarioResultsAsync(runId, ct))
        {
            // Some scenarios are flat (Output is the agent response text); for those
            // we attribute the scenario's estimatedCost to the LEGACY FLAT bucket —
            // the tier-aware path requires a recursive EvalResult tree in Output
            // JSON which pre-v0.8.1-beta evidence does NOT carry. T3.5 (2026-05-25):
            // surfaced as a separate field from UnknownKeyCost (which is the
            // in-tree-but-unregistered case) so operators can tell the two
            // pathologies apart.
            var tree = EvalResultPersistence.FromScenarioResult(scenario);
            if (tree is null)
            {
                legacyFlat += scenario.EstimatedCost;
                continue;
            }

            // Walk the tree, accumulating per-tier cost from every leaf's provenance.
            // Composites have EstimatedCost == 0; only LLM/code leaves carry real cost.
            // The tree is authoritative — supersedes scenario.EstimatedCost (which is
            // the root composite's provenance, often 0 for trees).
            WalkAndAccumulate(tree, ref trivial, ref low, ref medium, ref high, ref unknown);
        }

        // Invariant: total = sum(byTier) + unknown + legacyFlat. Computed last so callers
        // can trust the relationship for visualisation (stacked-bar height = total).
        var total = trivial + low + medium + high + unknown + legacyFlat;

        return new RunCostBreakdown(
            TotalCost: total,
            ByTier: new CostByTier(trivial, low, medium, high),
            UnknownKeyCost: unknown,
            LegacyFlatCost: legacyFlat,
            FilteredOut: Array.Empty<string>());
    }

    /// <summary>Max recursion depth for tree walks — protects against
    /// pathological / attacker-shaped trees that would overflow the stack.
    /// Set to 32 to comfortably exceed the GraphQL depth cap (10) and any
    /// realistic composite (root → pillar → article → multi-judge ≤ 5).</summary>
    internal const int MaxTreeWalkDepth = 32;

    internal static void WalkAndAccumulate(
        EvalResult node,
        ref double trivial,
        ref double low,
        ref double medium,
        ref double high,
        ref double unknown,
        int depth = 0)
    {
        if (depth > MaxTreeWalkDepth) return;

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
                WalkAndAccumulate(child, ref trivial, ref low, ref medium, ref high, ref unknown, depth + 1);
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
        if (!FileSystemLayout.IsSafePathSegment(runId)) return null;
        if (!FileSystemLayout.IsSafePathSegment(scenarioId)) return null;
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
    internal static EvalResult? FindEvaluatorNode(EvalResult node, string key, int depth = 0)
    {
        if (depth > MaxTreeWalkDepth) return null;
        if (string.Equals(node.Metric.Key, key, StringComparison.Ordinal))
            return node;
        var subs = node.Details.SubResults;
        if (subs is null) return null;
        foreach (var s in subs)
        {
            var found = FindEvaluatorNode(s, key, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }
}
