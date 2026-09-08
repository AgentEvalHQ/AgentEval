// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;

namespace AgentEval.Benchmarks;

/// <summary>
/// An arm: a name, and a way to observe one case. The subject is bound HERE, typed, once.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Typed and once, because the alternative is a string-keyed bag.</b> The recorded failure
/// shape is an agent, a client or a delegate stashed in a metadata dictionary and fished back out by
/// key: it compiles, it has no contract, and it breaks at the first rename with a null-reference
/// three layers from the cause. <see cref="Observe"/> is a function with a signature, so a wrong arm
/// does not compile.
/// </para>
/// <para>
/// <b>An arm is not a subject.</b> The live agent, a deliberately-degraded control, a shuffled-gold
/// baseline and an oracle are all arms; only some of them have an agent behind them at all. Defining
/// the arm as "a way to produce an <see cref="EvalInput"/> for a case" is what lets a control be
/// written without inventing a fake agent to satisfy a parameter.
/// </para>
/// </remarks>
/// <param name="ArmId">Stable identity — the live agent, a control, a baseline, an oracle.</param>
/// <param name="Observe">Runs one case and projects the result into an <see cref="EvalInput"/>.</param>
public sealed record BenchmarkArm(string ArmId, Func<TestCase, CancellationToken, Task<EvalInput>> Observe)
{
    /// <inheritdoc cref="BenchmarkArm(string, Func{TestCase, CancellationToken, Task{EvalInput}})"/>
    public string ArmId { get; init; } = !string.IsNullOrWhiteSpace(ArmId)
        ? ArmId
        : throw new ArgumentException("An arm needs an id: it is what pairs one arm's cases with another's.", nameof(ArmId));

    /// <inheritdoc cref="BenchmarkArm(string, Func{TestCase, CancellationToken, Task{EvalInput}})"/>
    public Func<TestCase, CancellationToken, Task<EvalInput>> Observe { get; init; } =
        Observe ?? throw new ArgumentNullException(nameof(Observe));

    /// <summary>
    /// The ordinary arm: run the case through a harness against an agent, and project the result.
    /// </summary>
    /// <param name="armId">Stable identity for this arm.</param>
    /// <param name="harness">The harness that runs the case.</param>
    /// <param name="agent">The subject.</param>
    /// <param name="options">
    /// Evaluation options, kept reachable on purpose. <see cref="EvaluationOptions.IncludeApprovalGatedToolCalls"/>
    /// (default <see langword="false"/>) is the one switch that turns a <see langword="null"/>
    /// <see cref="EvalInput.ToolCalls"/> into a record: with it off, a run whose calls were all
    /// approval-gated reaches the checks as "no recorder could see this", and every tool check
    /// declines rather than scoring a zero. Hiding this parameter would make that outcome look like
    /// the agent's behaviour instead of the harness's setting.
    /// </param>
    /// <returns>An arm that observes each case through <paramref name="harness"/>.</returns>
    public static BenchmarkArm FromHarness(
        string armId,
        IEvaluationHarness harness,
        IEvaluableAgent agent,
        EvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(agent);

        return new(armId, async (c, ct) =>
            c.ToEvalInput(await harness.RunEvaluationAsync(agent, c, options, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// The arm with no agent behind it: a control, a replay, a fixture, an oracle.
    /// </summary>
    /// <param name="armId">Stable identity for this arm.</param>
    /// <param name="observe">Produces the input for one case.</param>
    /// <returns>An arm that observes each case through <paramref name="observe"/>.</returns>
    public static BenchmarkArm From(string armId, Func<TestCase, CancellationToken, Task<EvalInput>> observe) =>
        new(armId, observe);
}
