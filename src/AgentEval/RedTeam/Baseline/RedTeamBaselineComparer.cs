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
    /// <returns>Comparison result showing deltas and regressions.</returns>
    public RedTeamComparison Compare(RedTeamResult current, RedTeamBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

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
        var attackComparisons = CompareAttacks(current, baseline);

        return new RedTeamComparison
        {
            Baseline = baseline,
            Current = current,
            NewVulnerabilities = newVulns,
            ResolvedVulnerabilities = resolved,
            PersistentVulnerabilities = persistent,
            AttackComparisons = attackComparisons
        };
    }

    private static List<AttackComparison> CompareAttacks(RedTeamResult current, RedTeamBaseline baseline)
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
                    NewFailures = currentFailures.ToList(),
                    Resolved = []
                });
            }
        }

        return comparisons;
    }
}
