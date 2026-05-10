// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Aggregation strategy for multi-run stochastic-agent verdicts.
/// Counts pass/warn/fail labels among non-skipped sub-results; the
/// winning label drives the returned <c>severity</c> (which is what the
/// composite verdict matrix in <see cref="CompositeEval"/> reads); the
/// numeric score is the mean of voting results. Ties between labels
/// resolve by most severe (<c>fail</c> &gt; <c>warn</c> &gt; <c>pass</c>),
/// matching the rollup convention used elsewhere.
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

        var meanScore = voting.Average(r => r.Score.Value);

        // Determine the WINNING label by majority, with most-severe tie-break.
        // Then derive the severity from the winning label so the composite
        // verdict matrix actually reflects the majority vote (previously
        // every branch returned the rolled-up max severity, which collapsed
        // the vote into "worst result wins" regardless of the count).
        string winningLabel;
        if (failCount > passCount && failCount > warnCount)            winningLabel = "fail";
        else if (warnCount > passCount && warnCount > failCount)       winningLabel = "warn";
        else if (passCount > failCount && passCount > warnCount)       winningLabel = "pass";
        else if (failCount > 0 && failCount >= warnCount && failCount >= passCount) winningLabel = "fail";  // tie → fail wins
        else if (warnCount > 0 && warnCount >= passCount)              winningLabel = "warn";              // tie → warn beats pass
        else                                                            winningLabel = "pass";

        var severity = winningLabel switch
        {
            "fail" => SeverityRollup.Max(voting.Where(r => r.Score.Label == "fail").Select(r => r.Score.Severity)),
            "warn" => "medium",
            _      => "none",
        };

        return (meanScore, severity);
    }
}
