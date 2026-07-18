// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.Guardrails;

/// <summary>
/// Fleet Correlation Layer — a live, per-session, cross-policy, confidence-aware correlator. Every gate in the
/// fleet decides ALONE today; an attacker who spreads a multi-step probe thin enough that no single gate
/// crosses its own block threshold currently sails through untouched, even though the PATTERN — several
/// independent gates each registering low-confidence concern in the same session — is itself evidence. This
/// is the fourth fleet-level view (see the design doc for how it differs from
/// <c>GatekeeperFleetHealthIndex</c>/<c>GateTelemetry</c>/<c>ParallelJudgeFanOut</c>, none of which are this).
/// </summary>
/// <remarks>
/// <para>
/// <b>Session-scoped, not process-wide.</b> Owned the same way <c>GateTelemetry</c>/<c>ShadowJudgePump</c>
/// already are — construct one per session, pass it in, no hidden global state. See
/// <see cref="EvalGatingChatClient"/>'s optional constructor parameter for the wiring point.
/// </para>
/// <para>
/// <b>What counts as "correlated," precisely.</b> Within a bounded trailing-turn window
/// (<see cref="FleetCorrelatorOptions.WindowTurns"/>), at least <see cref="FleetCorrelatorOptions.MinDistinctFamilies"/>
/// distinct families (see <see cref="FleetCorrelatorOptions.FamilyOf"/>) must each report a verdict with
/// <see cref="GateVerdict.Confidence"/> at or above <see cref="FleetCorrelatorOptions.SoftSignalFloor"/>. A
/// family is the gate's independent detection MECHANISM, not its instance — the same family firing twice in
/// one session proves nothing new the second time; only independent mechanisms agreeing counts. This directly
/// reuses <c>ParallelJudgeFanOut</c>'s fail-closed-OR PHILOSOPHY (any sufficient evidence blocks) while being
/// genuinely new in the dimension that matters: OR-across-time-and-policy instead of OR-across-one-turn.
/// </para>
/// <para>
/// <b>Abstains, does not starve toward false triggers.</b> <see cref="CheckCorrelation"/> returns
/// <see langword="null"/> whenever fewer than <see cref="FleetCorrelatorOptions.MinDistinctFamilies"/> distinct
/// families have a qualifying signal in the window — including a fleet with only deterministic/regex gates and
/// zero judges, which simply cannot correlate yet and should say so rather than force a trigger on noise.
/// </para>
/// <para>
/// <b>A correlation Block has weaker per-event evidence than any single gate's own Block</b> — its reason
/// states that honestly ("N independent gates each showed low-confidence concern," not "gate X detected Y with
/// high confidence"). This repo's honesty motto applies to the correlator's own explanations, not just judges.
/// </para>
/// <para><b>Experimental:</b> shipped 2026-07-18, no real-world consumer yet (no CLI verb, no calibration
/// against a real correlated-attack gold set — a natural second-order use of Crucible's own
/// <c>GateCalibrationHarness</c> once it has enough such cases) — the mechanism and defaults may still change.
/// Suppress via <c>&lt;NoWarn&gt;$(NoWarn);AGENTEVAL_GATEKEEPER_PREVIEW001&lt;/NoWarn&gt;</c> once you've read
/// this note.</para>
/// </remarks>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public sealed class FleetCorrelator
{
    private readonly FleetCorrelatorOptions _options;
    private readonly object _lock = new();
    private readonly List<Observation> _observations = new();
    private int _currentTurn;

    /// <summary>Creates a new session-scoped correlator.</summary>
    public FleetCorrelator(FleetCorrelatorOptions? options = null)
    {
        _options = options ?? new FleetCorrelatorOptions();

        if (double.IsNaN(_options.SoftSignalFloor) || _options.SoftSignalFloor < 0.0 || _options.SoftSignalFloor > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FleetCorrelatorOptions.SoftSignalFloor must be in [0, 1].");
        }

        if (_options.WindowTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FleetCorrelatorOptions.WindowTurns must be positive.");
        }

        if (_options.MinDistinctFamilies < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "FleetCorrelatorOptions.MinDistinctFamilies must be at least 2 — correlating fewer than 2 " +
                "distinct families defeats the purpose (a single low-confidence signal proves nothing new).");
        }

        ArgumentNullException.ThrowIfNull(_options.FamilyOf, nameof(options));
    }

    private readonly record struct Observation(string Family, double Confidence, int Turn);

    /// <summary>
    /// Advances the internal turn counter. Call exactly once per round-trip, before any gate in that
    /// round-trip runs — <see cref="EvalGatingChatClient"/> calls this once per <c>GetResponseAsync</c>/
    /// <c>GetStreamingResponseAsync</c> invocation.
    /// </summary>
    public void AdvanceTurn()
    {
        lock (_lock)
        {
            _currentTurn++;
        }
    }

    /// <summary>
    /// Records <paramref name="verdict"/> as a correlation input, if it carries a soft signal
    /// (<see cref="GateVerdict.Confidence"/> is not <see langword="null"/>). A verdict with no confidence
    /// (most deterministic/regex gates) is silently not a candidate — there is no signal to correlate, and
    /// that is a legitimate, common case, not an error.
    /// </summary>
    /// <param name="verdict">The gate's verdict for this turn.</param>
    /// <param name="stage">"pre" or "post" — recorded for parity with <c>EvalGatingChatClient.Record</c>'s own trace key shape, not currently used in the correlation decision itself.</param>
    public void Observe(GateVerdict verdict, string stage)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        if (verdict.Confidence is not { } confidence)
        {
            return;
        }

        var family = _options.FamilyOf(verdict.PolicyName);
        lock (_lock)
        {
            _observations.Add(new Observation(family, confidence, _currentTurn));
        }
    }

    /// <summary>
    /// Checks whether enough independent, elevated-but-sub-threshold signal has accumulated within the
    /// configured window to escalate. Returns a synthetic <see cref="GateAction.Block"/> verdict
    /// (<see cref="GateVerdict.PolicyName"/> = <c>"fleet-correlation"</c>) when it does, or
    /// <see langword="null"/> (abstain) otherwise. The caller is expected to enforce the returned verdict
    /// through the SAME <c>EvalGatePolicy</c> path as every other gate — this class has no enforcement
    /// opinion of its own.
    /// </summary>
    public GateVerdict? CheckCorrelation()
    {
        lock (_lock)
        {
            var windowStart = _currentTurn - _options.WindowTurns + 1;
            _observations.RemoveAll(o => o.Turn < windowStart);

            var qualifying = _observations.Where(o => o.Confidence >= _options.SoftSignalFloor).ToList();
            var families = qualifying
                .Select(o => o.Family)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (families.Count < _options.MinDistinctFamilies)
            {
                return null;
            }

            var combinedConfidence = qualifying.Average(o => o.Confidence);
            return GateVerdict.Block(
                "fleet-correlation",
                $"{qualifying.Count} independent gate signal(s) across {families.Count} families " +
                $"(families: {string.Join(", ", families)}) each showed elevated but sub-threshold concern " +
                $"within the last {_options.WindowTurns} turn(s). This is a WEAKER signal than any single " +
                "gate's own Block — no individual gate was confident enough to act alone.",
                matches: families) with { Confidence = combinedConfidence };
        }
    }
}
