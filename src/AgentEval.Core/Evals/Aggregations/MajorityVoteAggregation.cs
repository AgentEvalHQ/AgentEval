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
    /// <remarks>Forwards to the static of the same name, so the interface and the direct call
    /// cannot diverge.</remarks>
    (double Score, string Severity) IAggregationStrategy.AggregateWeights(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<double> weights) => AggregateWeights(results, weights);

    /// <inheritdoc/>
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        return AggregateWeights(results, components.Select(c => c.Weight).ToArray());
    }

    /// <summary>
    /// The weights-only entry point. Aggregation reads nothing from an <see cref="EvalComponent"/>
    /// except its <see cref="EvalComponent.Weight"/> — verified across all five strategies:
    /// <c>grep -rn '\.Eval\b|\.Required\b' src/AgentEval.Core/Evals/Aggregations/ | grep -v '///'</c> returns 0 — so a
    /// caller that has weights but no evals does not need a throwing <c>IEval</c> stub to carry them.
    /// Four such stubs existed only to satisfy the <see cref="EvalComponent"/> constructor.
    /// </summary>
    public static (double Score, string Severity) AggregateWeights(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(weights);
        if (results.Count != weights.Count)
            throw new InvalidOperationException("Results and weights must align 1:1.");

        // 17: exclude "error" leaves too (transient provider failure, severity "none" by construction), not
        // just "skipped" — an "error" leaf never counts as a pass/warn/fail vote (its label matches none of
        // them), but WITHOUT this exclusion its placeholder score still polluted the returned meanScore below.
        var voting = results.Where(r => r.Score.CountsTowardAggregate()).ToList();
        if (voting.Count == 0) return (0, "none");

        var meanScore = voting.Average(r => r.Score.Value);

        // Determine the WINNING label by majority, with most-severe tie-break.
        // Then derive the severity from the winning label so the composite
        // verdict matrix actually reflects the majority vote (previously
        // every branch returned the rolled-up max severity, which collapsed
        // the vote into "worst result wins" regardless of the count).
        var winningLabel = WinningLabel(voting);

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

    /// <summary>
    /// The label the majority of <paramref name="voting"/> carries — fail beats warn beats pass on a tie. The caller
    /// passes only results that count (<c>CountsTowardAggregate</c>) and at least one. A caller that needs the vote's
    /// VERDICT reads it here: mapping <see cref="Aggregate"/>'s severity back to a label lifted a majority of
    /// medium-severity fails to warn, and read "no voter at all" (severity <c>none</c>) as a pass (#203 review, B6c-1).
    /// </summary>
    public static string WinningLabel(IReadOnlyList<EvalResult> voting)
    {
        ArgumentNullException.ThrowIfNull(voting);
        if (voting.Count == 0)
            throw new ArgumentException("No result counts toward the vote; there is no winning label.", nameof(voting));

        var passCount = voting.Count(r => r.Score.Label == "pass");
        var warnCount = voting.Count(r => r.Score.Label == "warn");
        var failCount = voting.Count(r => r.Score.Label == "fail");

        if (failCount > passCount && failCount > warnCount) return "fail";
        if (warnCount > passCount && warnCount > failCount) return "warn";
        if (passCount > failCount && passCount > warnCount) return "pass";
        if (failCount > 0 && failCount >= warnCount && failCount >= passCount) return "fail";  // tie → fail wins
        if (warnCount > 0 && warnCount >= passCount) return "warn";                            // tie → warn beats pass
        return "pass";
    }
}
