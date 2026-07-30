// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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
/// distinct families (see <see cref="FleetCorrelatorOptions.FamilyOf"/>) must each report an Allow verdict
/// whose finite confidence is at or above <see cref="FleetCorrelatorOptions.SoftSignalFloor"/> and whose
/// matching threshold/actual provenance proves that the verdict was a sub-threshold finding. A family is the
/// gate's independent detection MECHANISM, not its instance — only the strongest signal from a family counts,
/// so repeated firings cannot manufacture independent agreement. Retained contribution provenance is bounded
/// and content-free. This directly
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
    private const int MaximumObservations = 65_536;
    private const int MaximumFamilyLength = 128;
    private const int MaximumContributingFamilies = 64;
    private readonly FleetCorrelatorOptions _options;
    private readonly object _lock = new();
    private readonly List<Observation> _observations = new();
    private long _currentTurn;

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

        if (_options.MinDistinctFamilies < 2 ||
            _options.MinDistinctFamilies > MaximumContributingFamilies)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"FleetCorrelatorOptions.MinDistinctFamilies must be between 2 and {MaximumContributingFamilies}.");
        }

        if (_options.MaxObservations < _options.MinDistinctFamilies ||
            _options.MaxObservations > MaximumObservations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"FleetCorrelatorOptions.MaxObservations must be at least MinDistinctFamilies and no greater than {MaximumObservations}.");
        }

        ArgumentNullException.ThrowIfNull(_options.FamilyOf, nameof(options));
    }

    private readonly record struct Observation(
        string Family,
        double Confidence,
        long Turn,
        GateProvenance Provenance);

    /// <summary>
    /// Advances the internal turn counter for sequential callers. Concurrent callers should use
    /// <see cref="BeginTurn"/> and pass its token to <see cref="Observe(GateVerdict,string,long)"/>.
    /// </summary>
    public void AdvanceTurn() => _ = BeginTurn();

    /// <summary>
    /// Starts one round trip and returns its stable correlation turn token. The token must be carried through
    /// every pre/post observation produced by that round trip so concurrent requests cannot reattribute a slow
    /// observation to a newer turn.
    /// </summary>
    /// <returns>The positive, monotonically increasing token for this round trip.</returns>
    public long BeginTurn()
    {
        lock (_lock)
        {
            if (_currentTurn == long.MaxValue)
            {
                throw new InvalidOperationException(
                    "Fleet correlator turn capacity exhausted.");
            }

            return ++_currentTurn;
        }
    }

    /// <summary>
    /// Records <paramref name="verdict"/> as a correlation input only when it is an Allow carrying a finite
    /// confidence and matching threshold/actual provenance that proves it was a sub-threshold finding. An
    /// ordinary Allow or any Block is not a near miss and is ignored.
    /// </summary>
    /// <param name="verdict">The gate's verdict for this turn.</param>
    /// <param name="stage">"pre" or "post" — recorded for parity with <c>EvalGatingChatClient.Record</c>'s own trace key shape, not currently used in the correlation decision itself.</param>
    public void Observe(GateVerdict verdict, string stage)
        => ObserveCore(verdict, stage, turn: null);

    /// <summary>
    /// Records a verdict against the stable token returned by <see cref="BeginTurn"/>. An observation whose
    /// turn has already left the configured window is ignored rather than reattributed or allowed to consume
    /// current capacity.
    /// </summary>
    /// <param name="verdict">The gate verdict produced by the identified round trip.</param>
    /// <param name="stage">The exact pipeline stage: <c>pre</c> or <c>post</c>.</param>
    /// <param name="turn">The token returned by <see cref="BeginTurn"/> for that round trip.</param>
    public void Observe(
        GateVerdict verdict,
        string stage,
        long turn)
    {
        if (turn < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(turn));
        }

        ObserveCore(verdict, stage, turn);
    }

    private void ObserveCore(
        GateVerdict verdict,
        string stage,
        long? turn)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (stage is not ("pre" or "post"))
        {
            throw new ArgumentException(
                "Fleet correlation stage must be 'pre' or 'post'.",
                nameof(stage));
        }

        if (verdict.Action != GateAction.Allow ||
            verdict.Confidence is not { } confidence)
        {
            return;
        }

        if (!double.IsFinite(confidence) ||
            confidence is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verdict),
                "A near-miss confidence must be finite and in [0, 1].");
        }

        if (verdict.Provenance is not
            {
                Threshold: { } threshold,
                ActualValue: { } actual,
            })
        {
            return;
        }

        if (!double.IsFinite(threshold) ||
            !double.IsFinite(actual) ||
            threshold is < 0.0 or > 1.0 ||
            actual is < 0.0 or > 1.0 ||
            actual != confidence ||
            actual >= threshold)
        {
            throw new ArgumentException(
                "A near miss requires matching finite threshold/actual provenance below its block threshold.",
                nameof(verdict));
        }

        var family = ValidateFamily(
            _options.FamilyOf(verdict.PolicyName));
        var retainedProvenance = new GateProvenance(
            family,
            Array.Empty<string>(),
            Threshold: threshold,
            ActualValue: actual);
        lock (_lock)
        {
            var observationTurn = turn ?? _currentTurn;
            if (observationTurn > _currentTurn)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(turn),
                    "Fleet correlation turn cannot be in the future.");
            }

            PruneExpired();
            var windowStart = _currentTurn - _options.WindowTurns + 1L;
            if (observationTurn < windowStart)
            {
                return;
            }

            var existingIndex = _observations.FindIndex(
                observation =>
                    observation.Turn == observationTurn &&
                    string.Equals(
                        observation.Family,
                        family,
                        StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                if (confidence > _observations[existingIndex].Confidence)
                {
                    _observations[existingIndex] =
                        new Observation(
                            family,
                            confidence,
                            observationTurn,
                            retainedProvenance);
                }

                return;
            }

            if (_observations.Count >= _options.MaxObservations)
            {
                throw new InvalidOperationException(
                    "Fleet correlator observation capacity exhausted.");
            }

            _observations.Add(
                new Observation(
                    family,
                    confidence,
                    observationTurn,
                    retainedProvenance));
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
            if (_observations.Count == 0)
            {
                return null;
            }

            PruneExpired();

            var representatives = _observations
                .Where(
                    observation =>
                        observation.Confidence >=
                        _options.SoftSignalFloor)
                .GroupBy(
                    observation => observation.Family,
                    StringComparer.Ordinal)
                .Select(
                    family => family
                        .OrderByDescending(
                            observation =>
                                observation.Confidence)
                        .ThenByDescending(
                            observation => observation.Turn)
                        .First())
                .OrderBy(
                    observation => observation.Family,
                    StringComparer.Ordinal)
                .ToList();

            if (representatives.Count < _options.MinDistinctFamilies)
            {
                return null;
            }

            var represented = representatives
                .Take(MaximumContributingFamilies)
                .ToArray();
            var families = represented
                .Select(observation => observation.Family)
                .ToArray();
            var combinedConfidence = represented.Average(
                observation => observation.Confidence);
            var contributing = represented
                .Select(observation => observation.Provenance)
                .ToArray();
            var familyCount = representatives.Count == represented.Length
                ? $"{represented.Length} independent gate family signal(s)"
                : $"{representatives.Count} qualifying gate families; bounded evidence represents the first {represented.Length} in canonical order";
            return GateVerdict.Block(
                "fleet-correlation",
                $"{familyCount} " +
                $"(families: {string.Join(", ", families)}) each showed elevated but sub-threshold concern " +
                $"within the last {_options.WindowTurns} turn(s). This is a WEAKER signal than any single " +
                "gate's own Block — no individual gate was confident enough to act alone.",
                matches: families) with
            {
                Confidence = combinedConfidence,
                Provenance = new GateProvenance(
                    "fleet-correlation",
                    Array.Empty<string>(),
                    Contributing: contributing),
            };
        }
    }

    private void PruneExpired()
    {
        var windowStart = _currentTurn - _options.WindowTurns + 1;
        _observations.RemoveAll(
            observation => observation.Turn < windowStart);
    }

    private static string ValidateFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family) ||
            family.Length > MaximumFamilyLength ||
            !string.Equals(
                family,
                family.Trim(),
                StringComparison.Ordinal) ||
            family.Any(
                character =>
                    char.IsControl(character) ||
                    CharUnicodeInfo.GetUnicodeCategory(character) ==
                    UnicodeCategory.Format))
        {
            throw new ArgumentException(
                "Fleet correlation family must be a bounded, canonical visible identity.",
                nameof(family));
        }

        return family;
    }
}
