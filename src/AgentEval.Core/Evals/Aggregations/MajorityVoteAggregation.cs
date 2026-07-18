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

        // 17: exclude "error" leaves too (transient provider failure, severity "none" by construction), not
        // just "skipped" — an "error" leaf never counts as a pass/warn/fail vote (its label matches none of
        // them), but WITHOUT this exclusion its placeholder score still polluted the returned meanScore below.
        var voting = results.Where(r => r.Score.Label is not ("skipped" or "error")).ToList();
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

        // Phase-7 Task 7.6: roll up severity from voters that actually carried
        // the winning label, instead of hard-coding "medium" / "none". A "warn"
        // vote can carry severity "high" (e.g. a borderline result that the
        // judge flagged as a serious risk); the prior "medium" hard-code lost
        // that signal. Falls back to {medium, none} only when no voter exists
        // for the winning label (defensive — should not happen given the
        // counting above).
        var winningVoterSeverities = voting
            .Where(r => r.Score.Label == winningLabel)
            .Select(r => r.Score.Severity)
            .ToList();
        var severity = winningLabel switch
        {
            "fail" => winningVoterSeverities.Count > 0 ? SeverityRollup.Max(winningVoterSeverities) : "high",
            "warn" => winningVoterSeverities.Count > 0 ? SeverityRollup.Max(winningVoterSeverities) : "medium",
            _      => winningVoterSeverities.Count > 0 ? SeverityRollup.Max(winningVoterSeverities) : "none",
        };

        return (meanScore, severity);
    }
}
