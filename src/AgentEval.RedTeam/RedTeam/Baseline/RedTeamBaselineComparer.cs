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

        var effectiveThresholds = thresholds ?? ComparisonThresholds.Default;

        // Collect current vulnerabilities
        var currentVulns = current.FailedAttacks
            .SelectMany(a => a.ProbeResults
                .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Select(p => (Attack: a, Probe: p)))
            .ToList();

        var currentVulnIds = currentVulns.Select(v => v.Probe.ProbeId).ToHashSet();
        var baselineVulnIds = baseline.KnownVulnerabilities.ToHashSet();

        // Find new vulnerabilities
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

        // Find resolved vulnerabilities
        var resolved = baselineVulnIds
            .Where(id => !currentVulnIds.Contains(id))
            .ToList();

        // Find persistent vulnerabilities
        var persistent = baselineVulnIds
            .Where(id => currentVulnIds.Contains(id))
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
            AttackComparisons = attackComparisons
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
