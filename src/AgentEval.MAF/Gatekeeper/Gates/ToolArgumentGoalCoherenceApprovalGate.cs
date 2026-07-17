// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Approval-flow half of the tool-argument/goal-coherence judge (see
/// <see cref="ToolArgumentGoalCoherenceJudge"/> for the axis, rubric, and why this is the ONLY seam that can
/// act on this judge's verdict for a specific pending tool call today — the deterministic hard-block seam
/// (<c>UseAgentEvalToolGate</c>) rejects LLM-cost gates at construction, and tool-gate shadow mode does not
/// exist yet). Auto-approves only on a confident-coherent verdict; a confident-incoherent verdict AND an
/// inconclusive/timeout verdict both escalate to a human — this gate never distinguishes "definitely bad" from
/// "not sure" in its outcome, because both already route to the same safe place (a human decides), matching
/// <see cref="IToolApprovalGate"/>'s own fail-closed philosophy.
/// </summary>
/// <remarks>
/// <b>Fixed goal.</b> The goal text is supplied once, at construction — a single instance is meant for one run
/// with one stated goal, not reused across a long-lived agent serving multiple different user goals over its
/// lifetime (build a fresh gate per goal instead). See <see cref="ToolArgumentGoalCoherenceRubric"/>'s own
/// remarks for why this is a disclosed v1 scope choice, not an oversight.
/// <para><b>Disclosed known limitations, not oversights:</b>
/// (1) <see cref="IToolApprovalGate.IsAutoApprovableAsync"/> returns a bare <see cref="bool"/> — the judge's
/// <see cref="GateVerdict.Reason"/> and <see cref="GateVerdict.Matches"/> evidence are computed but cannot be
/// threaded through to the human reviewing the resulting escalation
/// (<c>AgentEvalToolApprovalExtensions.RecordEscalation</c> only ever logs a generic message). Fixing this
/// needs a richer <see cref="IToolApprovalGate"/> contract shared by every approval gate, not a change scoped
/// to this one. (2) The one production call site
/// (<c>AgentEvalToolApprovalExtensions.AutoApproveWhenAllGatesAgree</c>) invokes
/// <see cref="IsAutoApprovableAsync"/> with no cancellation token — MAF's <c>AutoApprovalRules</c> delegate
/// signature has none to forward — so a judge call there always runs to its own internal
/// <see cref="JudgeGateOptions.Timeout"/> rather than reacting to the ambient run's cancellation. A caller
/// invoking this gate directly WITH a live token gets normal cancellation propagation (an
/// <see cref="OperationCanceledException"/> for its own token is never swallowed as an escalation — that
/// matches this codebase's own established convention, not a bug).</para>
/// </remarks>
public sealed class ToolArgumentGoalCoherenceApprovalGate : IToolApprovalGate
{
    private readonly IChatGate _judge;
    private readonly string _goal;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <summary>Creates the gate from a fast model and the fixed goal it judges every pending call against.</summary>
    /// <param name="fastModel">The fast/mini chat model the underlying judge calls.</param>
    /// <param name="goal">The agent's stated goal for this run. Must be non-empty.</param>
    /// <param name="options">
    /// Judge gate options (timeout, block threshold). Defaults applied when null.
    /// <see cref="JudgeGateOptions.FailClosedOnInconclusive"/> MUST NOT be set to <see langword="false"/> — this
    /// gate's entire safety story ("uncertainty always escalates, never auto-approves") depends on it, and
    /// silently accepting a caller override here (e.g. an options instance built for an observe-only
    /// deployment elsewhere) would let a model timeout/error auto-approve instead of escalate, and — because
    /// <paramref name="cache"/> defaults to <see langword="true"/> — cache that mistaken approval for the
    /// process lifetime. Passing <see langword="false"/> throws.
    /// </param>
    /// <param name="policyName">Evidence / policy name. Defaults to <c>judge:tool-argument-goal-coherence</c>.</param>
    /// <param name="cache">
    /// Wrap the underlying judge in an allow-only <see cref="JudgeVerdictCache"/> (default <see langword="true"/>).
    /// Pass <see langword="false"/> for a security-sensitive deployment where a stale cached auto-approval for
    /// the exact same (goal, tool, arguments) triple is unacceptable — e.g. every call must be freshly judged.
    /// </param>
    public ToolArgumentGoalCoherenceApprovalGate(
        IChatClient fastModel, string goal, JudgeGateOptions? options = null, string? policyName = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new ArgumentException("goal must be non-empty.", nameof(goal));
        }

        if (options is not null && !options.FailClosedOnInconclusive)
        {
            throw new ArgumentException(
                "options.FailClosedOnInconclusive must not be false — this gate must never auto-approve on a " +
                "judge timeout/error. Leave it at its default (true), or omit options entirely.",
                nameof(options));
        }

        _judge = ToolArgumentGoalCoherenceJudge.Create(fastModel, options, cache);
        _goal = goal;
        PolicyName = string.IsNullOrWhiteSpace(policyName) ? $"judge:{ToolArgumentGoalCoherenceJudge.Axis}" : policyName;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        string argumentsJson;
        try
        {
            argumentsJson = JsonSerializer.Serialize(call.Arguments, GateText.SerializerOptions);
        }
        catch (OperationCanceledException)
        {
            throw;   // honor cancellation — never swallow it
        }
        catch (OutOfMemoryException)
        {
            throw;   // let a truly fatal exception bubble
        }
        catch
        {
            // Cannot inspect the arguments ⇒ escalate (fail-closed), matching ArgumentPatternApprovalGate's own
            // precedent. Deliberately broader than a single exception type (System.Text.Json can throw several
            // shapes for a problematic argument graph — a reference cycle, a depth limit, a custom converter
            // throwing its own type) — an approval gate that can't affirm a call is routine must escalate
            // regardless of WHY the affirmation failed, not just for one specific exception type.
            return false;
        }

        var text = ToolArgumentGoalCoherenceRubric.FormatCase(_goal, call.Name, argumentsJson);
        var verdict = await _judge.InspectAsync(text, cancellationToken).ConfigureAwait(false);
        return verdict.Action == GateAction.Allow;
    }
}
