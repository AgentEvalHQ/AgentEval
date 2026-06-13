// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;                 // AgentResponse
using AgentEval.RedTeam.Evaluators;   // RefusalDetector

namespace AgentEval.RedTeam;

/// <summary>Whether the orchestrator should stop the conversation after a turn's evaluation.</summary>
public enum ConvergenceSignal
{
    /// <summary>Keep escalating.</summary>
    Continue,
    /// <summary>The objective was achieved — stop with a win.</summary>
    SucceededStop,
    /// <summary>The SUT is refusal-locked — stop honestly rather than burn the turn budget.</summary>
    RefusalLockStop,
}

/// <summary>The per-turn stop decision (signal + human-readable reason).</summary>
public readonly record struct ConvergenceDecision(ConvergenceSignal Signal, string Reason)
{
    /// <summary>The "keep going" decision.</summary>
    public static readonly ConvergenceDecision Continue = new(ConvergenceSignal.Continue, "continue");
}

/// <summary>Decides whether to stop a multi-turn conversation after a turn (pure; no internal state).</summary>
public interface IConvergenceDetector
{
    /// <summary>
    /// Decide the stop signal given the post-turn context, the agent's response, and the turn's verdict.
    /// <paramref name="context"/>.<c>History</c> is authoritative and already includes this turn; <paramref name="response"/>
    /// is a convenience alias for the latest assistant reply (e.g. for tool-trace-aware custom detectors).
    /// </summary>
    ConvergenceDecision Evaluate(MultiTurnContext context, AgentResponse response, EvaluationResult turnResult);
}

/// <summary>
/// Default policy (Wave C): stop on the first <see cref="EvaluationOutcome.Succeeded"/> turn, or after
/// <see cref="RefusalLockThreshold"/> consecutive assistant refusals (a refusal-locked SUT won't be cracked by more
/// turns — stopping early is honest and saves budget). Refusal-lock is intentionally a <i>lexical</i> signal
/// (best-effort, via the shared <see cref="RefusalDetector"/>) and orthogonal to the attack evaluator's success/resist
/// verdict; <see cref="RefusalDetector"/> errs toward non-refusal if a regex times out, so a hard-to-classify refusal
/// may not count toward the lock (the worst case is simply running more turns, not a false stop).
/// </summary>
public sealed class DefaultConvergenceDetector : IConvergenceDetector
{
    /// <summary>The shared default instance.</summary>
    public static readonly DefaultConvergenceDetector Instance = new();

    /// <summary>Consecutive refusals that constitute a refusal-lock. Default 2; clamped to a minimum of 1.</summary>
    public int RefusalLockThreshold
    {
        get => _refusalLockThreshold;
        init => _refusalLockThreshold = value < 1 ? 1 : value;
    }
    private readonly int _refusalLockThreshold = 2;

    /// <inheritdoc />
    public ConvergenceDecision Evaluate(MultiTurnContext context, AgentResponse response, EvaluationResult turnResult)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (turnResult.Outcome == EvaluationOutcome.Succeeded)
            return new(ConvergenceSignal.SucceededStop, "objective achieved this turn");

        // Count trailing consecutive assistant turns that are refusals (History already includes this turn).
        var streak = 0;
        for (var i = context.History.Count - 1; i >= 0; i--)
        {
            var t = context.History[i];
            if (!string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (RefusalDetector.IsRefusal(t.Content, out _))
                streak++;
            else
                break;
        }

        return streak >= RefusalLockThreshold
            ? new(ConvergenceSignal.RefusalLockStop, $"refusal-locked ({streak} consecutive refusals)")
            : ConvergenceDecision.Continue;
    }
}

/// <summary>
/// Stops ONLY on success — never on refusal-lock (Wave C). The policy for attacks like Crescendo whose technique is
/// to <i>persist through refusals</i>: keep escalating until the objective is met or the attack runs out of rungs /
/// hits <c>MaxTurns</c>. (Contrast <see cref="DefaultConvergenceDetector"/>, which gives up on a refusal-locked SUT.)
/// </summary>
public sealed class SuccessOnlyConvergenceDetector : IConvergenceDetector
{
    /// <summary>The shared default instance.</summary>
    public static readonly SuccessOnlyConvergenceDetector Instance = new();

    /// <inheritdoc />
    public ConvergenceDecision Evaluate(MultiTurnContext context, AgentResponse response, EvaluationResult turnResult)
        => turnResult.Outcome == EvaluationOutcome.Succeeded
            ? new(ConvergenceSignal.SucceededStop, "objective achieved this turn")
            : ConvergenceDecision.Continue;
}
