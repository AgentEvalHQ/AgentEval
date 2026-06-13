// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Baseline;

/// <summary>
/// Compares RedTeam results against baselines.
/// </summary>
public class RedTeamBaselineComparer
{
    /// <summary>
    /// Compares a RedTeam result to a baseline.
    /// </summary>
    /// <param name="current">The current RedTeam result.</param>
    /// <param name="baseline">The baseline to compare against.</param>
    /// <param name="thresholds">Optional thresholds used to classify the comparison.</param>
    /// <param name="requireMatchingIntensity">
    /// When <see langword="true"/> (default), a mismatch between current and baseline intensity throws,
    /// because the two were measured over different probe sets and the deltas would be misleading (RC-6).
    /// </param>
    /// <returns>Comparison result showing deltas and regressions.</returns>
    public RedTeamComparison Compare(
        RedTeamResult current,
        RedTeamBaseline baseline,
        ComparisonThresholds? thresholds = null,
        bool requireMatchingIntensity = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        var currentIntensity = current.Options?.Intensity ?? Intensity.Moderate;
        if (requireMatchingIntensity && currentIntensity != baseline.Intensity)
        {
            throw new InvalidOperationException(
                $"Cannot compare a {currentIntensity} scan against a {baseline.Intensity} baseline: they use " +
                "different probe sets, so score/ASR deltas are not meaningful. Re-run at the baseline's intensity, " +
                "or call Compare(..., requireMatchingIntensity: false) to override (RC-6).");
        }

        // RA3-06 / T5-2: a FailFast-truncated scan executed only a partial probe set, so its score/ASR
        // denominators are not comparable to a full baseline. Refuse by default; the explicit override stands.
        if (requireMatchingIntensity && current.WasTruncated)
        {
            throw new InvalidOperationException(
                $"Cannot compare a FailFast-truncated scan ({current.TotalProbes}/{current.PlannedProbes} probes " +
                "executed) against a baseline: the truncated probe set makes score/ASR deltas non-comparable (RA3-06). " +
                "Re-run without FailFast, or call Compare(..., requireMatchingIntensity: false) to override.");
        }

        var effectiveThresholds = thresholds ?? ComparisonThresholds.Default;

        // Collect current vulnerabilities
        var currentVulns = current.FailedAttacks
            .SelectMany(a => a.ProbeResults
                .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Select(p => (Attack: a, Probe: p)))
            .ToList();

        var currentVulnIds = currentVulns.Select(v => v.Probe.ProbeId).ToHashSet();
        var baselineVulnIds = baseline.KnownVulnerabilities.ToHashSet();

        // Find new vulnerabilities (a current vuln not known in the baseline is real regardless of attack set).
        var newVulns = currentVulns
            .Where(v => !baselineVulnIds.Contains(v.Probe.ProbeId))
            .Select(v => new NewVulnerability
            {
                ProbeId = v.Probe.ProbeId,
                AttackName = v.Attack.AttackDisplayName,
                Technique = v.Probe.Technique,
                Reason = v.Probe.Reason,
                Severity = v.Probe.Severity
            })
            .ToList();

        // 5e: Resolved/Persistent are only meaningful for attacks present in BOTH runs. A narrowed --attacks scan
        // that simply did NOT re-run an attack must not have that attack's known vulns counted as "Resolved/Fixed"
        // (a fabricated improvement). Build the comparable baseline-vuln set from per-attack FailedProbeIds restricted
        // to the attack-name intersection (KnownVulnerabilities is flat and not attack-attributed, so it can't be filtered).
        var currentAttackNames = current.AttackResults.Select(a => a.AttackName).ToHashSet();
        var comparableBaselineVulnIds = baseline.AttackResults
            .Where(a => currentAttackNames.Contains(a.AttackName))
            .SelectMany(a => a.FailedProbeIds)
            .ToHashSet();

        var resolved = comparableBaselineVulnIds.Where(id => !currentVulnIds.Contains(id)).ToList();
        var persistent = comparableBaselineVulnIds.Where(id => currentVulnIds.Contains(id)).ToList();

        // Attacks in the baseline that were NOT re-tested in this (narrowed) run — surfaced rather than silently
        // dropped, so the user sees that "Fixed" excludes them.
        var notReTested = baseline.AttackResults
            .Where(a => !currentAttackNames.Contains(a.AttackName))
            .Select(a => new NotReTestedAttack { AttackName = a.AttackName, KnownVulnerabilities = a.FailedProbeIds.Count })
            .ToList();

        // Compare attacks
        var attackComparisons = CompareAttacks(current, baseline, effectiveThresholds);

        return new RedTeamComparison
        {
            Baseline = baseline,
            Current = current,
            Thresholds = effectiveThresholds,
            NewVulnerabilities = newVulns,
            ResolvedVulnerabilities = resolved,
            PersistentVulnerabilities = persistent,
            AttackComparisons = attackComparisons,
            NotReTested = notReTested,
        };
    }

    private static List<AttackComparison> CompareAttacks(RedTeamResult current, RedTeamBaseline baseline, ComparisonThresholds thresholds)
    {
        var comparisons = new List<AttackComparison>();
        var baselineByName = baseline.AttackResults.ToDictionary(a => a.AttackName);

        foreach (var attack in current.AttackResults)
        {
            var currentFailures = attack.ProbeResults
                .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Select(p => p.ProbeId)
                .ToHashSet();

            var currentRate = attack.TotalCount > 0
                ? (double)attack.ResistedCount / attack.TotalCount
                : 1.0;

            if (baselineByName.TryGetValue(attack.AttackName, out var baselineAttack))
            {
                var baselineFailures = baselineAttack.FailedProbeIds.ToHashSet();

                comparisons.Add(new AttackComparison
                {
                    AttackName = attack.AttackName,
                    AttackDisplayName = attack.AttackDisplayName,
                    BaselineRate = baselineAttack.Rate,
                    CurrentRate = currentRate,
                    StableBand = thresholds.AttackRateStableBand,
                    NewFailures = currentFailures
                        .Where(id => !baselineFailures.Contains(id))
                        .ToList(),
                    Resolved = baselineFailures
                        .Where(id => !currentFailures.Contains(id))
                        .ToList()
                });
            }
            else
            {
                // New attack not in baseline
                comparisons.Add(new AttackComparison
                {
                    AttackName = attack.AttackName,
                    AttackDisplayName = attack.AttackDisplayName,
                    BaselineRate = 1.0, // Assume 100% resistance if not in baseline
                    CurrentRate = currentRate,
                    StableBand = thresholds.AttackRateStableBand,
                    NewFailures = currentFailures.ToList(),
                    Resolved = []
                });
            }
        }

        return comparisons;
    }
}
