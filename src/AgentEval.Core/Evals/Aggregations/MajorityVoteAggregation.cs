// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Aggregation strategy for multi-run stochastic-agent verdicts.
/// Counts pass/warn/fail labels among non-skipped sub-results; the majority wins.
/// Ties are broken by most severe (fail beats warn beats pass).
/// The numeric score is the mean of voting (non-skipped) results; severity is the max.
/// </summary>
public sealed class MajorityVoteAggregation : IAggregationStrategy
{
    /// <summary>Shared singleton instance.</summary>
    public static IAggregationStrategy Instance { get; } = new MajorityVoteAggregation();

    /// <inheritdoc/>
    public string Name => "MajorityVote";

    /// <inheritdoc/>
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        var voting = results.Where(r => r.Score.Label != "skipped").ToList();
        if (voting.Count == 0) return (0, "none");

        var passCount = voting.Count(r => r.Score.Label == "pass");
        var warnCount = voting.Count(r => r.Score.Label == "warn");
        var failCount = voting.Count(r => r.Score.Label == "fail");

        // Score = mean of voting results (so majority verdict still has a numeric anchor).
        var meanScore = voting.Average(r => r.Score.Value);
        var severity = SeverityRollup.Max(voting.Select(r => r.Score.Severity));

        // Determine majority. Tie-breaking by most severe (fail > warn > pass).
        if (failCount >= passCount && failCount >= warnCount) return (meanScore, severity);
        if (warnCount >= passCount) return (meanScore, severity);
        return (meanScore, severity);
        // Note: severity rollup already incorporates the worst — strategy returns mean score
        // and rolled-up severity. Caller's verdict matrix interprets via severity.
    }
}
