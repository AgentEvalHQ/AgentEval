// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Evals.Performance;

/// <summary>
/// The performance family as ADMITTED CHECKS: deterministic evals over what one run actually cost,
/// each carrying a floor and each declining rather than scoring when nothing was measured.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Every floor here is NOT DERIVABLE, and that is the finding rather than a shortcut.</b> A
/// chance floor answers "what would an arm that understood nothing score?", and that question needs
/// either a pool to draw from or a set of alternatives the task offers. A latency has neither: it is
/// a physical measurement compared against a threshold somebody declared, and <b>chance does not
/// produce a p99</b>. An arm that understood nothing but happened to run on a fast machine clears a
/// latency budget; an arm that understood everything on a slow one does not. That is a real property
/// of this family and the reason it was never floored — recorded here in each check's derivation
/// instead of papered over with a manufactured number.
/// </para>
/// <para>
/// <b>These sit BESIDE <c>PerformanceBenchmark</c>'s own leaves, not instead of them.</b> The
/// benchmark keeps its composite, its thresholds and its rollup. What is new is that the same
/// questions are now expressible through the door — so a caller can put latency and token budget
/// into a <see cref="BenchmarkDefinition"/>, run them per case with
/// <c>BenchmarkRunner</c>, and score them against a control arm with <c>BenchmarkScore</c>.
/// </para>
/// <para>
/// ⚠ <b>Absent is not fast, and absent is not free.</b> <see cref="EvalInput.Performance"/> is
/// <see langword="null"/> whenever the harness ran without
/// <c>EvaluationOptions.TrackPerformance</c>, and every check below returns
/// <c>NotApplicable</c> for it. Reading a missing measurement as a zero duration or a zero token
/// count would turn an unmeasured run into the best possible one — the flattering direction, and the
/// same <c>?? 0</c> shape this lane exists to remove.
/// </para>
/// </remarks>
public static class PerformanceChecks
{
    /// <summary>The shared reason no floor is derivable for a threshold comparison.</summary>
    /// <param name="quantity">What is being measured, e.g. <c>"a p99 latency"</c>.</param>
    /// <returns>The derivation text.</returns>
    public static string NoDrawModel(string quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quantity);

        return $"{quantity} is a physical measurement against a DECLARED threshold, not a choice among "
             + "alternatives, so there is no pool to draw from and no draw budget: chance does not produce "
             + "this number. An arm that understood nothing on fast hardware clears the budget and an arm "
             + "that understood everything on slow hardware does not, which is exactly why a chance floor "
             + "would say nothing about the agent.";
    }

    /// <summary>Latency against a declared wall-clock budget, as an admitted check.</summary>
    /// <param name="budget">The wall-clock budget one run must not exceed.</param>
    /// <returns>The check, paired with its floor, ready for a <see cref="BenchmarkDefinition"/>.</returns>
    public static AdmittedCheck WithinLatencyBudget(TimeSpan budget) =>
        new(new WithinLatencyBudgetEval(budget),
            ChanceFloor.NotDerivable(NoDrawModel("a run's wall-clock duration")));

    /// <summary>Total tokens against a declared budget, as an admitted check.</summary>
    /// <param name="maxTotalTokens">The token budget one run must not exceed.</param>
    /// <returns>The check, paired with its floor.</returns>
    public static AdmittedCheck WithinTokenBudget(int maxTotalTokens) =>
        new(new WithinTokenBudgetEval(maxTotalTokens),
            ChanceFloor.NotDerivable(NoDrawModel("a run's token count")));

    /// <summary>Time-to-first-token against a declared budget, as an admitted check.</summary>
    /// <param name="budget">The TTFT budget one run must not exceed.</param>
    /// <returns>The check, paired with its floor.</returns>
    public static AdmittedCheck WithinFirstTokenBudget(TimeSpan budget) =>
        new(new WithinFirstTokenBudgetEval(budget),
            ChanceFloor.NotDerivable(NoDrawModel("a run's time to first token")));
}

/// <summary>Did this run finish inside its declared wall-clock budget?</summary>
/// <param name="budget">The budget. Must be positive — a zero budget nothing can meet is not a check.</param>
public sealed class WithinLatencyBudgetEval(TimeSpan budget)
    : AtomicCodeEval("perf.within_latency_budget", "Within latency budget", "performance", "1.0.0")
{
    private readonly TimeSpan _budget = budget > TimeSpan.Zero
        ? budget
        : throw new ArgumentOutOfRangeException(nameof(budget), budget,
            "A latency budget must be positive. A budget of zero or less is one no run can meet, which "
            + "makes the check unfailable in the other direction: it reports the agent when it is "
            + "describing the budget.");

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Performance is not { } performance)
        {
            var reason =
                "nothing measured this run's duration, so nothing here can say whether it met its budget. "
                + "The harness records timings only under EvaluationOptions.TrackPerformance, and an "
                + "unmeasured run is not a fast one.";
            return NotApplicable(reason, new EvalEvidence("performance", "duration", reason));
        }

        var duration = performance.TotalDuration;
        var within = duration <= _budget;
        var summary = within
            ? $"the run took {duration.TotalMilliseconds:F0}ms, inside its {_budget.TotalMilliseconds:F0}ms budget."
            : $"the run took {duration.TotalMilliseconds:F0}ms, over its {_budget.TotalMilliseconds:F0}ms budget.";

        var scored = Build(
            value: within ? 1.0 : 0.0,
            passed: within,
            severity: within ? "none" : "medium",
            dimensions: new Dictionary<string, double>
            {
                ["duration_ms"] = duration.TotalMilliseconds,
                ["budget_ms"] = _budget.TotalMilliseconds,
            },
            evidence: [new EvalEvidence("performance", "duration", summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}

/// <summary>Did this run finish inside its declared token budget?</summary>
/// <param name="maxTotalTokens">The budget. Must be positive.</param>
public sealed class WithinTokenBudgetEval(int maxTotalTokens)
    : AtomicCodeEval("perf.within_token_budget", "Within token budget", "performance", "1.0.0")
{
    private readonly int _max = maxTotalTokens > 0
        ? maxTotalTokens
        : throw new ArgumentOutOfRangeException(nameof(maxTotalTokens), maxTotalTokens,
            "A token budget must be positive.");

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ⚠ TWO separate absences, and collapsing them is the defect. Performance may be null (nobody
        // measured anything) or present with a null TotalTokens (timings were taken but the provider
        // reported no usage). Both decline; neither is a zero-token run.
        if (input.Performance is not { } performance)
        {
            var reason =
                "nothing measured this run, so nothing here can say how many tokens it used. An "
                + "unmeasured run is not a free one.";
            return NotApplicable(reason, new EvalEvidence("performance", "tokens", reason));
        }

        if (performance.TotalTokens is not { } total)
        {
            var reason =
                "this run was timed but reported NO token usage, so the budget cannot be checked. A "
                + "provider that returns no usage is not a provider that used none — the two are "
                + "indistinguishable here and scoring the second would be a fabricated zero.";
            return NotApplicable(reason, new EvalEvidence("performance", "tokens", reason));
        }

        var within = total <= _max;
        var summary = within
            ? $"the run used {total} token(s), inside its budget of {_max}."
            : $"the run used {total} token(s), over its budget of {_max}.";

        var scored = Build(
            value: within ? 1.0 : 0.0,
            passed: within,
            severity: within ? "none" : "medium",
            dimensions: new Dictionary<string, double> { ["total_tokens"] = total, ["budget_tokens"] = _max },
            evidence: [new EvalEvidence("performance", "tokens", summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}

/// <summary>Did the first token arrive inside its declared budget?</summary>
/// <param name="budget">The budget. Must be positive.</param>
public sealed class WithinFirstTokenBudgetEval(TimeSpan budget)
    : AtomicCodeEval("perf.within_first_token_budget", "Within time-to-first-token budget", "performance", "1.0.0")
{
    private readonly TimeSpan _budget = budget > TimeSpan.Zero
        ? budget
        : throw new ArgumentOutOfRangeException(nameof(budget), budget, "A TTFT budget must be positive.");

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // TimeToFirstToken is null on a non-streaming run. That is a property of HOW the agent was
        // called, not of how fast it was, and it is the case this check most has to get right: a
        // non-streaming run has no first-token time and must not be scored as an instant one.
        if (input.Performance?.TimeToFirstToken is not { } ttft)
        {
            var reason =
                "this run recorded no time to first token — it was not streamed, or nothing measured it. "
                + "A run with no first-token time is not a run whose first token arrived instantly.";
            return NotApplicable(reason, new EvalEvidence("performance", "ttft", reason));
        }

        var within = ttft <= _budget;
        var summary = within
            ? $"the first token arrived after {ttft.TotalMilliseconds:F0}ms, inside its {_budget.TotalMilliseconds:F0}ms budget."
            : $"the first token arrived after {ttft.TotalMilliseconds:F0}ms, over its {_budget.TotalMilliseconds:F0}ms budget.";

        var scored = Build(
            value: within ? 1.0 : 0.0,
            passed: within,
            severity: within ? "none" : "low",
            dimensions: new Dictionary<string, double>
            {
                ["ttft_ms"] = ttft.TotalMilliseconds,
                ["budget_ms"] = _budget.TotalMilliseconds,
            },
            evidence: [new EvalEvidence("performance", "ttft", summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
