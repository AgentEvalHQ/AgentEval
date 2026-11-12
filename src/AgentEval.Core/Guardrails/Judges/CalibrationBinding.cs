// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Guardrails.Judges;

// A frozen, per-registration view of a report. Never mutates the report or a shared judge instance.
internal sealed class CalibrationBinding
{
    private readonly CalibrationUse _source;
    private readonly bool _sameInstrument;

    internal CalibrationBinding(CalibrationReport report, string axis, string? targetDigest, CalibrationReusePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (report.Axis != axis || !report.IsInlineReady)
            throw Invalid("a ready report for this axis", "the report is for another axis or is not inline-ready");
        var observations = report.Cases.SelectMany(c => c.JudgeInstruments).ToArray();
        var legacy = report.CalibratedInstrumentDigest is null && report.InstrumentDigests.Count == 0 && observations.Length == 0;
        if (report.CalibratedInstrumentDigest is null && !legacy)
            throw Invalid("independent source calibration", "a panel or unidentified configured report cannot certify an individual judge");
        _sameInstrument = string.Equals(report.CalibratedInstrumentDigest, targetDigest, StringComparison.Ordinal);
        if (!_sameInstrument && policy == CalibrationReusePolicy.RequireMatchingInstrument)
            throw Invalid("a matching instrument or explicit same-axis reuse", "the source and target instrument identities differ");
        _source = new()
        {
            Axis = axis, Policy = policy, CalibratedAt = report.CapturedAt,
            SourceInstrumentDigest = report.CalibratedInstrumentDigest, TargetInstrumentDigest = targetDigest,
            CalibratedModelIds = Array.AsReadOnly(observations.SelectMany(i => i.Calls).Where(c => c.ResponseReceived)
                .Select(c => c.ModelId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            CalibratedLocales = Array.AsReadOnly(observations.Select(i => i.Prompt.EffectiveLocale)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Kind = _sameInstrument && legacy ? CalibrationUseKind.LegacyAxisOnly : CalibrationUseKind.Transferred,
            Reason = "Runtime observation has not yet been checked."
        };
    }

    internal CalibrationUse Evaluate(GateVerdict verdict)
    {
        var observed = JudgeGateInstrumentation.Observed(verdict.Provenance)
            .Where(i => i.ConfigurationDigest == _source.TargetInstrumentDigest).ToArray();
        var use = _source with
        {
            ObservedModelIds = Array.AsReadOnly(observed.SelectMany(i => i.Calls).Where(c => c.ResponseReceived)
                .Select(c => c.ModelId).Distinct(StringComparer.Ordinal).ToArray()),
            ObservedLocales = Array.AsReadOnly(observed.Select(i => i.Prompt.EffectiveLocale)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
        if (verdict.Provenance?.RuleName.EndsWith(":budget-exhausted", StringComparison.Ordinal) == true)
            return use with { Kind = CalibrationUseKind.Unmeasured, Reason = "Judge budget exhausted; calibration does not turn a skipped call into a measurement." };
        if (_source.TargetInstrumentDigest is null)
            return use with { Kind = _sameInstrument ? CalibrationUseKind.LegacyAxisOnly : CalibrationUseKind.Transferred,
                Reason = _sameInstrument ? "Legacy axis-only evidence; runtime model and locale are not verified."
                    : "Explicit same-axis transfer to a legacy judge; runtime model and locale are unknown." };
        if (observed.Any(i => !i.Measured))
            return use with { Kind = CalibrationUseKind.Unmeasured, Reason = "The configured judge did not obtain a conclusive measurement; calibration cannot authorize this outcome." };
        if (observed.Length == 0 || observed.Any(i => !i.Calls.Any(c => c.ResponseReceived) && i.DeterministicRule is null))
            return use with { Kind = CalibrationUseKind.Rejected, Reason = "A required configured judge observation is missing or unmeasured." };
        if (_sameInstrument && MatchesObservedModels(_source.TargetInstrumentDigest, _source.CalibratedModelIds, observed))
            return use with { Kind = CalibrationUseKind.Matched, Reason = observed.All(i => i.Calls.Count == 0)
                ? "Instrument matches; an implementation-owned structural rule decided without a model call."
                : "Instrument and observed model match the source calibration." };
        return use with
        {
            Kind = _source.Policy == CalibrationReusePolicy.AllowSameAxis ? CalibrationUseKind.Transferred : CalibrationUseKind.Rejected,
            Reason = _source.Policy == CalibrationReusePolicy.AllowSameAxis
                ? "Explicit same-axis transfer; this report does not measure the target instrument/model's accuracy."
                : "The runtime model is unknown or differs from the calibrated model."
        };
    }

    internal static bool MatchesObservedModels(string digest, IEnumerable<string?> calibratedModels, IEnumerable<JudgeGateInstrument> observations)
    {
        var expected = calibratedModels.Distinct(StringComparer.Ordinal).ToArray();
        var observed = observations.ToArray();
        return expected.Length == 1 && !string.IsNullOrWhiteSpace(expected[0]) && observed.Length > 0 &&
            observed.All(i => i.ConfigurationDigest == digest && i.Measured &&
                (i.Calls.Count == 0 ? i.DeterministicRule is not null :
                    i.Calls.Any(c => c.ResponseReceived) && i.Calls.Where(c => c.ResponseReceived).All(c => c.ModelId == expected[0])));
    }

    private static InvalidOperationException Invalid(string expected, string actual) => new(
        $"Expected: {expected}. Actual: {actual}. Suggestions: supply independent evidence or explicitly select AllowSameAxis for a compatible acceptance policy. Because: transferred evidence must not claim independently measured target accuracy.");
}
