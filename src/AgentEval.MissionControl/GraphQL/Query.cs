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
    [Cost(100.0)]
    [HotChocolate.Types.UseRequestScope]
    public Task<IReadOnlyList<ComplianceRegulationSummary>> Compliance(
        [Service] ComplianceMatrixService service,
        CancellationToken ct = default) =>
        service.ListRegulationsAsync(ct);

    /// <summary>
    /// Returns the subjects × controls matrix for a regulation. The portal's
    /// <c>&lt;ComplianceMatrix/&gt;</c> component (a CSS grid heatmap, no chart
    /// library) renders directly from this shape.
    /// </summary>
    [Cost(100.0)]
    [HotChocolate.Types.UseRequestScope]
    public Task<ComplianceMatrix> ComplianceMatrix(
        [Service] ComplianceMatrixService service,
        string regulation,
        CancellationToken ct = default) =>
        service.BuildMatrixAsync(regulation, ct);

    /// <summary>Returns the selected verified report with its container location and custody warning.</summary>
    /// <remarks>Imports do not establish original execution custody. Invalid or ambiguous evidence is refused.</remarks>
    [Cost(100.0)]
    [HotChocolate.Types.UseRequestScope]
    public Task<ComplianceEvidenceWithChain?> ComplianceEvidence(
        [Service] ComplianceMatrixService service,
        string regulation,
        SubjectKind subjectKind,
        string subjectName,
        string timestamp,
        CancellationToken ct = default) => service.ReadEvidenceAsync(regulation, subjectKind, subjectName, timestamp, ct);

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

    /// <summary>Returns at most 200 recorded canonical occurrences, newest known execution first.</summary>
    /// <remarks>Count limits occurrences, including repeated nodes and trials. Unknown dates sort last.
    /// Source verification completes before selection; no capture, index or legacy tree is required.</remarks>
    [Cost(100.0)]
    [HotChocolate.Types.UseRequestScope]
    public async Task<IReadOnlyList<EvaluatorTimelinePoint>> EvaluatorTimeline(
        [Service] IWorkspaceRunSource source, string evaluatorKey, int count = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evaluatorKey) || count <= 0) return [];
        return WorkspaceTimelineProjector.Points(await source.ReadAsync(ct), evaluatorKey, Math.Min(count, 200));
    }
}
