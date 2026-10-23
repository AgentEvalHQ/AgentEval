// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap relocated here.
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using HotChocolate.CostAnalysis.Types; // SEC-11: [Cost] weights for expensive resolvers.

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Root GraphQL <c>Query</c> type for Mission Control.
/// </summary>
/// <remarks>
/// Existing output-store reads and additive shared evidence queries coexist on this root.
/// Shared evidence resolvers delegate availability, cursor and verification behavior to MissionControl.Core.
/// </remarks>
public sealed partial class Query
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

    // ─── Gatekeeper security graph (Phase 6.5) ──────────────────────────────

    /// <summary>
    /// Returns a bounded privacy-minimized security graph view, or <see langword="null"/> when this Mission
    /// Control host has no explicitly injected read-only graph source.
    /// </summary>
    [Cost(100.0)]
    public SecurityGraphOpsView? SecurityGraph(
        [Service] ISecurityGraphReportSource source,
        int nodeLimit = SecurityGraphOpsProjector.DefaultNodeLimit,
        int edgeLimit = SecurityGraphOpsProjector.DefaultEdgeLimit)
    {
        SecurityGraphOpsProjector.ValidateLimits(
            nodeLimit,
            edgeLimit);
        var report = source.Read();
        return report is null
            ? null
            : SecurityGraphOpsProjector.Project(
                report,
                nodeLimit,
                edgeLimit);
    }

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
        [Service] Microsoft.Extensions.Configuration.IConfiguration configuration,
        CancellationToken ct = default)
    {
        var version = typeof(IOutputStoreReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        // SEC-12: expose the absolute workspace root ONLY in Mode A (loopback, single operator); redact it
        // for Mode B/C, mirroring the /api/v1/version endpoint. Enforced, not just documented.
        var root = McHost.IsModeA(configuration["AgentEval:Mode"]) ? store.WorkspaceRoot : null;

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
    /// <c>&lt;ComplianceMatrix/&gt;</c> component (a CSS grid heatmap, no chart
    /// library) renders directly from this shape.
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
    [Cost(100.0)]
    [HotChocolate.Types.UseRequestScope]
    public async Task<RunCostBreakdown?> RunCostBreakdown(
        [Service] IWorkspaceRunSource source, string runId, string? sourceId = null, CancellationToken ct = default)
    {
        var run = WorkspaceRunSource.Find(await source.ReadAsync(ct), runId, sourceId);
        if (run is null) return null;
        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0, legacyFlat = 0;
        var scenarios = WorkspaceRunProjector.Scenarios(run);
        var unpriced = scenarios.Count == 0 ? 1 : 0;
        foreach (var scenario in scenarios)
        {
            var tree = WorkspaceRunProjector.CapturedLegacyTree(run, scenario.ResultId);
            if (tree is not null)
                WalkAndAccumulate(tree, ref trivial, ref low, ref medium, ref high, ref unknown, ref unpriced);
            else if (scenario.EstimatedCost is { } cost)
            {
                if (run.Evidence.Info.Imported) legacyFlat += cost;
                else unknown += cost;
            }
            else unpriced++;
        }
        return new RunCostBreakdown(trivial + low + medium + high + unknown + legacyFlat,
            new(trivial, low, medium, high), unknown, legacyFlat, []) { UnpricedCount = unpriced };
    }

    /// <summary>Max recursion depth for tree walks — protects against
    /// pathological / attacker-shaped trees that would overflow the stack.
    /// Set to 32 to comfortably exceed the GraphQL depth cap (10) and any
    /// realistic composite (root → pillar → article → multi-judge ≤ 5).</summary>
    internal const int MaxTreeWalkDepth = EvalTreeLimits.MaxTreeWalkDepth; // ARC-03: one source of truth

    internal static void WalkAndAccumulate(
        EvalResult node,
        ref double trivial,
        ref double low,
        ref double medium,
        ref double high,
        ref double unknown,
        int depth = 0)
    {
        int unpriced = 0;
        WalkAndAccumulate(node, ref trivial, ref low, ref medium, ref high, ref unknown, ref unpriced, depth);
    }

    /// <summary>As above, also counting the leaves whose cost was not priced (<see cref="EvalProvenance.EstimatedCost"/>
    /// null) into <paramref name="unpriced"/>: they are counted, never summed as $0. A composite whose cost is null
    /// because a part was not priced is not counted itself; its unpriced leaves are.</summary>
    /// <remarks>
    /// Each leaf's cost is added once. A composite is walked, never added: its own cost is its parts' rolled up
    /// (<see cref="CostRollup.Aggregate"/>), so adding it as well counted every leaf once more for each composite above
    /// it (T83). At <see cref="MaxTreeWalkDepth"/> a composite's parts are not walked and its rolled-up cost stands for
    /// them, counted once (or, when it is null, the composite counts once as unpriced): a subtree past the cap is neither
    /// dropped nor counted twice.
    /// </remarks>
    internal static void WalkAndAccumulate(
        EvalResult node,
        ref double trivial,
        ref double low,
        ref double medium,
        ref double high,
        ref double unknown,
        ref int unpriced,
        int depth = 0)
    {
        if (depth > MaxTreeWalkDepth) return;

        if (node.Details.SubResults is { Count: > 0 } children && depth < MaxTreeWalkDepth)
        {
            foreach (var child in children)
            {
                WalkAndAccumulate(child, ref trivial, ref low, ref medium, ref high, ref unknown, ref unpriced, depth + 1);
            }
            return;
        }

        if (node.Provenance.EstimatedCost is not double cost)
        {
            unpriced++;
            return;
        }

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
    }

    // ─── Recursive EvalResult tree (MC1.4.6) ─────────────────────────────────

    /// <summary>
    /// Returns a captured legacy <see cref="EvalResult"/> tree from the selected verified
    /// workspace source, checked against its canonical results. Returns <c>null</c> when
    /// the scenario or captured tree is absent. Native results use <c>scenarioResultTree</c>.
    /// </summary>
    /// <remarks>
    /// Source and result IDs disambiguate imported runs and scenarios. Capture-off imports
    /// retain their canonical descendants without synthesizing legacy presentation data.
    /// </remarks>
    [Cost(100.0)]
    [HotChocolate.Types.UseRequestScope]
    public async Task<EvalResult?> ScenarioTree(
        [Service] IWorkspaceRunSource source,
        string runId,
        string scenarioId,
        string? sourceId = null,
        string? resultId = null,
        CancellationToken ct = default)
    {
        var run = WorkspaceRunSource.Find(await source.ReadAsync(ct), runId, sourceId);
        if (run is null) return null;
        var scenario = WorkspaceRunProjector.Scenarios(run).SingleOrDefault(s => s.Id == scenarioId
            && (resultId is null || s.ResultId == resultId));
        return scenario is null ? null : WorkspaceRunProjector.CapturedLegacyTree(run, scenario.ResultId);
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
    [Cost(100.0)] // SEC-11: bound alias-multiplication of this expensive resolver
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
