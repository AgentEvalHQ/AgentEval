// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Evals;

/// <summary>
/// The one owner of the legacy <c>Metadata["agent"]</c> convention: read it here, or not at all.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is a containment, not an endorsement.</b> Four benchmark families reach an agent by
/// fishing it out of <see cref="EvalInput.Metadata"/> under the string key <c>"agent"</c> and casting
/// with <c>as</c> — <c>PerformanceBenchmarkRegistration</c>, <c>OwaspBenchmarkRun</c>,
/// <c>NistBenchmarkRun</c>, <c>MitreBenchmarkRun</c>. Each had written the read out by hand, so one
/// convention had four implementations, four refusal messages and four chances to diverge; a rename
/// of the key would have compiled in all four and failed at runtime in all four. This type makes it
/// one implementation with one key.
/// </para>
/// <para>
/// <b>What replaced it, for anything new.</b> A subject is bound in a <c>BenchmarkArm</c> — typed,
/// once, with a signature — and <c>BenchmarkRunner</c> REFUSES an arm that puts an
/// <see cref="IEvaluableAgent"/>, an <c>IChatClient</c> or a <see cref="Delegate"/> in
/// <see cref="EvalInput.Metadata"/>, before any check runs. <see cref="EvalInput.Metadata"/> is data.
/// Nothing new should call this; it exists so the four that already do stop each keeping their own
/// copy of the rule.
/// </para>
/// <para>
/// ⚠ <b>An absent agent is not an empty one.</b> The families call this and, on
/// <see langword="false"/>, return a SKIPPED composite carrying <see cref="AbsentAgentReason"/> —
/// never a zero, and never a pass. "Nobody supplied a subject" and "the subject did nothing" are
/// different facts.
/// </para>
/// </remarks>
public static class EvalInputAgentBinding
{
    /// <summary>The metadata key the four legacy families read. Frozen in one place.</summary>
    public const string AgentMetadataKey = "agent";

    /// <summary>
    /// Reads the agent a legacy family was handed through <see cref="EvalInput.Metadata"/>.
    /// </summary>
    /// <param name="input">The input to read.</param>
    /// <param name="agent">The agent, when one was supplied under the canonical key.</param>
    /// <returns>
    /// <see langword="true"/> when an <see cref="IEvaluableAgent"/> was supplied;
    /// <see langword="false"/> when the key is absent, null, or holds something that is not one.
    /// </returns>
    /// <remarks>
    /// The three failure modes collapse to one answer on purpose: from the caller's side "no key",
    /// "a null under the key" and "a string under the key" are the same fact — <b>this run has no
    /// subject</b> — and the family's response to all three is identical.
    /// </remarks>
    public static bool TryReadAgent(EvalInput input, out IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Metadata is not null
            && input.Metadata.TryGetValue(AgentMetadataKey, out var raw)
            && raw is IEvaluableAgent bound)
        {
            agent = bound;
            return true;
        }

        agent = null!;
        return false;
    }

    /// <summary>
    /// The refusal text a family records when no agent was supplied, naming the typed way in.
    /// </summary>
    /// <param name="family">The family refusing, e.g. <c>"OWASP"</c>.</param>
    /// <param name="typedEntryPoint">The typed call that does not need this convention, e.g. <c>"OwaspBenchmarkRun.ScanAsync(agent)"</c>.</param>
    /// <returns>The reason, for the skipped result's recommendations.</returns>
    public static string AbsentAgentReason(string family, string typedEntryPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(typedEntryPoint);

        return $"{family} was given no subject: EvalInput.Metadata[\"{AgentMetadataKey}\"] holds no "
             + $"{nameof(IEvaluableAgent)}. This run measured NOTHING — it is not a zero and not a pass. "
             + $"Call {typedEntryPoint} directly, which takes the agent as a typed argument, or bind the "
             + "subject in a BenchmarkArm and let BenchmarkRunner drive it.";
    }
}
