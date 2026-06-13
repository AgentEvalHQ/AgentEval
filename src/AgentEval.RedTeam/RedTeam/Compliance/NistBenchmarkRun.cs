// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Benchmarks;

/// <summary>
/// Thin wrapper around a pre-configured <see cref="AttackPipeline"/> that produces both a native
/// <see cref="RedTeamResult"/> + <see cref="NistAiRmfComplianceReport"/> AND a unified <see cref="EvalResult"/>
/// (via <see cref="EvaluateAsync"/>) for the output-store / audit-chain pipeline. Mirrors
/// <see cref="MitreBenchmarkRun"/>, but the composite has <b>one leaf per NIST AI RMF control</b>.
/// </summary>
/// <remarks>
/// <para>Constructed by <see cref="NistBenchmark"/> presets; not intended to be instantiated directly.</para>
/// <para><b>Honesty:</b> GOVERN / MAP / MANAGE controls (and MEASURE 2.6 Safety) are
/// <see cref="ControlFidelity.Governance"/> — organizational, never PASS — and surface as <c>skipped</c> leaves, not
/// passes-by-default. Only the MEASURE security/privacy/validity sub-actions a black-box red-team can exercise are
/// scored. A passing run is ONE input into an AI RMF program, not RMF conformance.</para>
/// </remarks>
public sealed class NistBenchmarkRun
{
    private readonly AttackPipeline _pipeline;
    private readonly NistAiRmfComplianceReporter _reporter = new();
    private readonly IReadOnlyList<IAttackType> _roster;

    /// <summary>The preset name used to build this run ("RmfBaseline", "RmfSmoke", "RmfAuditGrade").</summary>
    public string PresetName { get; }

    /// <summary>The optional LLM judge supplied to the factory (retained for API symmetry).</summary>
    public IEvaluator? Judge { get; }

    /// <summary>Convenience: the configured attack pipeline (read-only access for tests).</summary>
    public AttackPipeline Pipeline => _pipeline;

    /// <summary>The NIST control IDs this preset's roster can exercise (a MEASURE sub-action with ≥1 relevant attack
    /// present in the roster). Governance controls are never covered.</summary>
    public IReadOnlyList<string> CoveredControlIds
    {
        get
        {
            var rosterNames = _roster.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return NistAiRmfControls.All
                .Where(c => c.Fidelity != ControlFidelity.Governance && c.RelevantAttacks.Any(rosterNames.Contains))
                .Select(c => c.ControlId)
                .ToList();
        }
    }

    /// <summary>Constructs a run. Intended for <see cref="NistBenchmark"/> factory methods.</summary>
    internal NistBenchmarkRun(AttackPipeline pipeline, IEvaluator? judge, string presetName, IReadOnlyList<IAttackType> roster)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Judge = judge;
        PresetName = presetName ?? throw new ArgumentNullException(nameof(presetName));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
    }

    /// <summary>Runs the configured attack pipeline against <paramref name="agent"/> and returns the raw result.</summary>
    public Task<RedTeamResult> ScanAsync(IEvaluableAgent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return _pipeline.ScanAsync(agent, ct);
    }

    /// <summary>Generates the rich <see cref="NistAiRmfComplianceReport"/> from an existing result (pure projection).</summary>
    public NistAiRmfComplianceReport GenerateReport(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _reporter.GenerateReport(result);
    }

    /// <summary>Adapter that lets NIST results flow through the output-store + audit-chain pipeline. Resolves the
    /// agent from <c>input.Metadata["agent"]</c> (skipped composite if absent), scans, and shapes the composite.</summary>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        IEvaluableAgent? agent = null;
        if (input.Metadata?.TryGetValue("agent", out var rawAgent) == true)
            agent = rawAgent as IEvaluableAgent;

        if (agent is null)
            return BuildSkippedComposite(
                "NIST AI RMF adapter requires an IEvaluableAgent at EvalInput.Metadata[\"agent\"]. " +
                "Use NistBenchmarkRun.ScanAsync(agent) directly, or wrap the agent into Metadata[\"agent\"].");

        var redTeamResult = await _pipeline.ScanAsync(agent, ct);
        return BuildEvalResult(redTeamResult);
    }

    /// <summary>Shapes an already-computed result into the per-control composite (avoids a second scan).</summary>
    public EvalResult BuildEvalResult(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var report = _reporter.GenerateReport(result);
        return BuildComposite(result, report);
    }

    // ── EvalResult shaping ───────────────────────────────────────────────────

    private EvalResult BuildComposite(RedTeamResult redTeamResult, NistAiRmfComplianceReport report)
    {
        var attacksByName = redTeamResult.AttackResults.ToDictionary(a => a.AttackName, StringComparer.OrdinalIgnoreCase);

        var leaves = report.Controls.Select(c => BuildLeaf(c, attacksByName)).ToList();

        var components = leaves
            .Select(l => new EvalComponent(new NistSyntheticEval(l.Metric.Key, l.Metric.Name)))
            .ToList();
        var (compositeScore, compositeSeverity) = MinAggregation.Instance.Aggregate(leaves, components);

        var testedLeaves = leaves.Where(l => l.Score.Label != "skipped").ToList();
        string compositeLabel;
        bool compositePassed;
        if (testedLeaves.Count == 0) { compositeLabel = "skipped"; compositePassed = false; }
        else if (testedLeaves.Any(l => l.Score.Label == "fail")) { compositeLabel = "fail"; compositePassed = false; }
        else if (testedLeaves.Any(l => l.Score.Label == "warn")) { compositeLabel = "warn"; compositePassed = false; }
        else { compositeLabel = "pass"; compositePassed = true; }

        var dimensions = new Dictionary<string, double>
        {
            ["nist_overall_pass_rate"]   = report.Summary.OverallPassRate / 100.0,
            ["nist_controls_evaluated"]  = report.Summary.TestedCategories,
            ["nist_total_controls"]      = report.Summary.TotalCategories,
            ["nist_needs_improvement"]   = report.Summary.CriticalFindings,
            ["nist_partially_effective"] = report.Summary.HighFindings,
            ["nist_total_probes"]        = redTeamResult.TotalProbes,
            ["nist_succeeded_probes"]    = redTeamResult.SucceededProbes,
            ["nist_resisted_probes"]     = redTeamResult.ResistedProbes,
        };

        var compositeEvidence = new List<EvalEvidence>
        {
            new("nist", redTeamResult.AgentName,
                $"NIST AI RMF preset='{PresetName}': {report.Summary.TestedCategories}/{report.Summary.TotalCategories} " +
                $"MEASURE sub-actions evaluated, overall conclusive score {report.Summary.OverallPassRate:F1}%, " +
                $"{report.Summary.CriticalFindings} needs-improvement. Governance/MAP/MANAGE controls are Not Applicable " +
                "(a passing run is one input into an AI RMF program, not RMF conformance)."),
        };

        return new EvalResult(
            Metric: new(
                Key: $"nist.{PresetName.ToLowerInvariant()}",
                Name: $"NIST AI RMF — {PresetName}",
                Category: "compliance.nist",
                Version: "1.0.0"),
            Score: new(compositeScore, null, compositeLabel, compositePassed, 1.0, compositeSeverity, null),
            Details: new(
                Dimensions: dimensions,
                Evidence: compositeEvidence,
                Recommendations: report.Recommendations.Count > 0 ? report.Recommendations.ToList() : null,
                SubResults: leaves,
                AggregationStrategy: "Min"),
            Provenance: new("composite", Judge is null ? null : "nist-judge-passthrough", null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private EvalResult BuildSkippedComposite(string reason)
    {
        var emptyResult = new RedTeamResult
        {
            AgentName = "(no agent)",
            AttackResults = [],
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.Zero,
        };
        var emptyReport = _reporter.GenerateReport(emptyResult);
        var leaves = emptyReport.Controls
            .Select(c => RedTeamComplianceLeaf.BuildSkippedLeaf(
                "nist", "compliance.nist", c.Control.ControlId, c.Control.ControlId,
                "Not evaluated — no agent supplied.", includeDimensions: false))
            .ToList();

        return new EvalResult(
            Metric: new($"nist.{PresetName.ToLowerInvariant()}", $"NIST AI RMF — {PresetName}", "compliance.nist", "1.0.0"),
            Score: new(0.0, null, "skipped", false, 1.0, "none", null),
            Details: new(null, null, new[] { reason }, leaves, "Min"),
            Provenance: new("skipped", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static EvalResult BuildLeaf(ControlStatus control, IReadOnlyDictionary<string, AttackResult> attacksByName)
    {
        // Governance / not-evaluated controls are skipped leaves (never passes-by-default).
        if (control.Status is ControlEvaluationStatus.NotApplicable or ControlEvaluationStatus.NotEvaluated)
        {
            var message = control.Status == ControlEvaluationStatus.NotApplicable
                ? $"Not applicable — {control.Control.ControlName}: organizational/governance, not testable by a black-box red-team."
                : $"Not evaluated — {control.Control.ControlName}: no mapped attack ran (or all inconclusive).";
            return RedTeamComplianceLeaf.BuildSkippedLeaf(
                "nist", "compliance.nist", control.Control.ControlId,
                $"{control.Control.ControlId} — {control.Control.ControlName}", message, includeDimensions: true);
        }

        var attacks = control.Control.RelevantAttacks
            .Select(attacksByName.GetValueOrDefault)
            .Where(a => a is not null).Cast<AttackResult>().ToList();

        return RedTeamComplianceLeaf.BuildTestedLeaf(
            "nist", "compliance.nist", control.Control.ControlId, control.Control.ControlName,
            subjectLabel: $"{control.Control.ControlName} ({control.Control.Fidelity})",
            control.TotalTests, control.PassedTests, attacks);
    }

    /// <summary>Lightweight <see cref="IEval"/> stub to satisfy <see cref="EvalComponent"/> for <see cref="MinAggregation"/>.</summary>
    private sealed class NistSyntheticEval(string key, string name) : IEval
    {
        public string Key => key;
        public string Name => name;
        public string Category => "compliance.nist";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException("NistSyntheticEval is a stub — call NistBenchmarkRun.EvaluateAsync instead.");
    }
}
