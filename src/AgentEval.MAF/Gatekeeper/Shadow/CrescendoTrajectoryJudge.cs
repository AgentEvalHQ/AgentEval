// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M4) — the STATEFUL half of the Crescendo-trajectory defense: an <see cref="IShadowJudge"/>
/// that tracks a rolling window of a session's turns and arms quarantine once enough of them have been flagged
/// as escalating relative to what came before (PyRIT's "Crescendo" pattern — gradual escalation across many
/// individually-innocuous-looking turns, rather than one obviously dangerous ask). Each call to
/// <see cref="JudgeAsync"/> judges exactly ONE new turn (see <c>AgentEvalShadowJudgeExtensions</c> — the pump
/// enqueues once per completed run) by asking the calibratable per-turn core
/// (<see cref="CrescendoTrajectoryTurnJudge"/>, built from <see cref="CrescendoTrajectoryRubric"/>) whether this
/// turn escalates relative to a summary of the prior tracked turns, then persists the updated trajectory back
/// into the session so the NEXT call can see it.
/// </summary>
/// <remarks>
/// <b>Why StateBag is safe here despite <c>RateLimitGate</c>'s own documented avoidance of it.</b>
/// <c>RateLimitGate</c> is an INLINE gate that can be invoked concurrently — multiple tool calls in flight on
/// the same run can race a StateBag read-modify-write, and StateBag has no atomic increment, so that gate uses
/// a <c>ConditionalWeakTable</c> instead. This judge has no such hazard: <see cref="ShadowJudgePump"/> drains
/// its queue with exactly ONE background consumer (<c>SingleReader = true</c>, a single <c>Task</c> looping
/// serially over <c>Channel.Reader.ReadAllAsync()</c>) — so at most one <see cref="JudgeAsync"/> call is ever
/// in flight for the ENTIRE pump, across every session, at any moment. A read-then-write of one session's
/// trajectory state can never race another call for the same session (or any other). This guarantee holds only
/// as long as exactly one <see cref="ShadowJudgePump"/> is wired per agent build — the codebase's own existing
/// convention (<c>GatekeeperOptions.ShadowJudgePump</c> is a single nullable slot, not a collection).
/// <para><b>Inherited limitation.</b> Like every StateBag consumer in this codebase (see <c>RateLimitGate</c>'s
/// own disclosure), tracking is keyed by <see cref="AgentSession"/> OBJECT IDENTITY — a caller that
/// reconstructs a fresh session instance per turn (rather than reusing one instance across a conversation)
/// resets the tracked trajectory every turn, and this judge can never detect an escalation.</para>
/// <para><b>Detection is eventual, not real-time.</b> A shadow judge never blocks the run it observes — an
/// escalating turn is only ever caught AFTER it already ran; arming quarantine only prevents the conversation
/// from being RESUMED on a later run (see <see cref="QuarantineGate"/>). <see cref="DefaultMaxTrackedTurns"/> and
/// <see cref="DefaultArmThreshold"/> are starting points, not empirically validated against a live gold set —
/// this judge's underlying per-turn axis is calibrated (<see cref="CrescendoTrajectoryTurnJudge.CalibrateAsync"/>),
/// but the multi-turn arm/no-arm behavior itself needs its own scripted integration coverage, since
/// <c>GateCalibrationHarness</c> only scores the single-text-block per-turn core.</para>
/// <para><b>An infra failure never counts as an escalation.</b> The per-turn core's underlying
/// <see cref="JudgeGateOptions.FailClosedOnInconclusive"/> is forced to <see langword="false"/> for the gate
/// this judge builds internally, regardless of what a caller passes in <see cref="JudgeGateOptions"/> — the
/// arm counter is a lifetime total that never resets, so if a judge TIMEOUT/model error instead fail-closed to
/// "escalating" (this gate's stateless sibling's own default), a run of transient LLM outages on an entirely
/// benign conversation could silently and irreversibly quarantine an innocent session. A shadow judge that
/// can't prove a turn safe should abstain from THIS axis, not treat its own infrastructure failure as evidence
/// of an attack.</para>
/// </remarks>
public sealed class CrescendoTrajectoryJudge : IShadowJudge
{
    /// <summary>The session <c>StateBag</c> key this judge persists its tracked trajectory under.</summary>
    public const string StateKey = "gatekeeper.crescendo.state";

    /// <summary>
    /// Default rolling window of raw (untruncated-by-LLM, only length-capped) turn excerpts kept in the summary
    /// handed to the per-turn judge — bounds StateBag payload size and prompt cost. Older turns roll off the
    /// window, but every escalating turn still counts toward the arm threshold for the life of the session (the
    /// arm counter is NOT windowed) — a slow-burn attack spread across dozens of turns, most of them benign
    /// filler, still trips the threshold even after its early escalating turns scroll out of the summary.
    /// Overridable per instance via the constructor, matching every other configurable Gatekeeper threshold in
    /// this codebase (e.g. <c>ToolResultSizeGate.DefaultMaxLength</c>).
    /// </summary>
    public const int DefaultMaxTrackedTurns = 5;

    /// <summary>Default number of flagged escalating turn-shifts (across the session's lifetime) that arms quarantine. Overridable per instance via the constructor.</summary>
    public const int DefaultArmThreshold = 3;

    private const int MaxTurnExcerptChars = 500;

    private readonly IChatGate _turnJudge;
    private readonly int _maxTrackedTurns;
    private readonly int _armThreshold;

    /// <summary>Creates the trajectory judge from a fast model. The underlying per-turn gate is never cached (each turn is unique).</summary>
    /// <param name="fastModel">The fast/mini chat model the underlying per-turn judge calls.</param>
    /// <param name="options">
    /// Timeout/BlockThreshold/MaxOutputTokens for the underlying per-turn judge. <see cref="JudgeGateOptions.FailClosedOnInconclusive"/>
    /// is always forced to <see langword="false"/> for this judge regardless of what's passed here — see the
    /// class remarks for why.
    /// </param>
    /// <param name="maxTrackedTurns">Rolling summary window size. Default <see cref="DefaultMaxTrackedTurns"/>.</param>
    /// <param name="armThreshold">Lifetime escalating-turn count that arms quarantine. Default <see cref="DefaultArmThreshold"/>.</param>
    public CrescendoTrajectoryJudge(
        IChatClient fastModel, JudgeGateOptions? options = null, int maxTrackedTurns = DefaultMaxTrackedTurns, int armThreshold = DefaultArmThreshold)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        if (maxTrackedTurns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrackedTurns), maxTrackedTurns, "must be at least 1.");
        }

        if (armThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(armThreshold), armThreshold, "must be at least 1.");
        }

        _turnJudge = CrescendoTrajectoryTurnJudge.Create(fastModel, BuildTurnJudgeOptions(options), cache: false);
        _maxTrackedTurns = maxTrackedTurns;
        _armThreshold = armThreshold;
    }

    /// <inheritdoc/>
    public async Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var newTurnText = FormatTurn(context.InputText, context.ResponseText);
        if (string.IsNullOrWhiteSpace(newTurnText))
        {
            return ShadowVerdict.Clean("empty turn — nothing to judge");
        }

        // No session ⇒ no way to persist or recall prior turns, so trajectory tracking is impossible for this
        // call. Still worth a single-turn opinion for the verdict sink, but never arm on it alone — arming is
        // a statement about a PATTERN across turns, and one unpersisted turn is not a pattern.
        if (context.Session is null)
        {
            var soloVerdict = await _turnJudge.InspectAsync(
                CrescendoTrajectoryRubric.FormatCase("(no prior turns tracked)", newTurnText), cancellationToken).ConfigureAwait(false);
            return ShadowVerdict.Clean(soloVerdict.Action == GateAction.Block
                ? "turn escalates, but no session to track a trajectory against — not arming on a single turn"
                : "no escalation");
        }

        var state = ReadState(context.Session);
        var summarySoFar = Summarize(state.Turns, state.TotalTurns);

        var verdict = await _turnJudge
            .InspectAsync(CrescendoTrajectoryRubric.FormatCase(summarySoFar, newTurnText), cancellationToken)
            .ConfigureAwait(false);

        var escalated = verdict.Action == GateAction.Block;
        var wasAlreadyArmed = state.EscalationCount >= _armThreshold;
        var escalationCount = state.EscalationCount + (escalated ? 1 : 0);
        var turns = Append(state.Turns, new CrescendoTurnRecord(newTurnText, escalated));
        WriteState(context.Session, new CrescendoState(turns, escalationCount, state.TotalTurns + 1));

        // Arm exactly once, on the turn that CROSSES the threshold — not on every subsequent turn while already
        // armed (quarantine is sticky in StateBag; re-arming on a later, possibly non-escalating turn would
        // overwrite the audit trail with a misleading "latest shift" that didn't actually happen this turn).
        if (!wasAlreadyArmed && escalationCount >= _armThreshold)
        {
            return ShadowVerdict.Compromise(
                $"crescendo trajectory: {escalationCount} escalating turn-shift(s) observed (arm threshold {_armThreshold}); " +
                $"latest shift: {verdict.Reason ?? "escalation detected"}");
        }

        return ShadowVerdict.Clean(wasAlreadyArmed
            ? "no new action — session already quarantined from a prior turn"
            : escalated
                ? $"turn escalates but below arm threshold ({escalationCount}/{_armThreshold})"
                : "no escalation");
    }

    /// <summary>
    /// Builds the per-turn judge's options with <see cref="JudgeGateOptions.FailClosedOnInconclusive"/> forced
    /// to <see langword="false"/> — see the class remarks. Every other option is taken from
    /// <paramref name="callerOptions"/> (or its default) unchanged.
    /// </summary>
    private static JudgeGateOptions BuildTurnJudgeOptions(JudgeGateOptions? callerOptions)
    {
        var baseOptions = callerOptions ?? new JudgeGateOptions();
        return new JudgeGateOptions
        {
            Timeout = baseOptions.Timeout,
            BlockThreshold = baseOptions.BlockThreshold,
            MaxOutputTokens = baseOptions.MaxOutputTokens,
            FailClosedOnInconclusive = false,
        };
    }

    private static string FormatTurn(string? inputText, string? responseText)
    {
        var input = string.IsNullOrWhiteSpace(inputText) ? null : Truncate(inputText, MaxTurnExcerptChars);
        var response = string.IsNullOrWhiteSpace(responseText) ? null : Truncate(responseText, MaxTurnExcerptChars);

        return (input, response) switch
        {
            (null, null) => string.Empty,
            (not null, null) => $"User: {input}",
            (null, not null) => $"Agent: {response}",
            _ => $"User: {input}\nAgent: {response}",
        };
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…(truncated)");

    /// <summary>
    /// Renders the tracked window with each turn's TRUE lifetime index (not its position within the window) —
    /// so once the window rolls over, turn 8 of a real conversation is still labeled "Turn 8", not relabeled
    /// "Turn 1"; the rubric's prompt treats "Turn 1"/"no prior summary" as meaning the genesis turn specifically.
    /// Flags a previously-escalating turn inline ("[flagged]") so the judge can see a REPEATED shift, not just
    /// the latest one — the exact shape the rubric's own gold set already hand-authors for continuation cases.
    /// </summary>
    private static string Summarize(IReadOnlyList<CrescendoTurnRecord> turns, int totalTurns)
    {
        if (turns.Count == 0)
        {
            return "(no prior turns tracked)";
        }

        var firstAbsoluteIndex = totalTurns - turns.Count + 1;
        return string.Join("\n", turns.Select((t, i) =>
        {
            var absoluteIndex = firstAbsoluteIndex + i;
            return t.Escalated ? $"Turn {absoluteIndex} [flagged]: {t.Text}" : $"Turn {absoluteIndex}: {t.Text}";
        }));
    }

    private IReadOnlyList<CrescendoTurnRecord> Append(IReadOnlyList<CrescendoTurnRecord> turns, CrescendoTurnRecord next)
    {
        var updated = turns.Count < _maxTrackedTurns
            ? new List<CrescendoTurnRecord>(turns)
            : new List<CrescendoTurnRecord>(turns.Skip(turns.Count - _maxTrackedTurns + 1));
        updated.Add(next);
        return updated;
    }

    private static CrescendoState ReadState(AgentSession session) =>
        session.StateBag.TryGetValue<CrescendoState>(StateKey, out var state, JsonSerializerOptions.Default) && state is not null
            ? state
            : CrescendoState.Empty;

    private static void WriteState(AgentSession session, CrescendoState state) =>
        session.StateBag.SetValue(StateKey, state, JsonSerializerOptions.Default);
}

/// <summary>One tracked turn in a session's Crescendo trajectory: the formatted excerpt and whether it was flagged as an escalation.</summary>
internal sealed record CrescendoTurnRecord(string Text, bool Escalated);

/// <summary>
/// A session's persisted Crescendo-trajectory state — the rolling window of tracked turns, the
/// lifetime-running count of flagged escalations, and the lifetime total turn count (see
/// <see cref="CrescendoTrajectoryJudge.DefaultMaxTrackedTurns"/> for why the window and the counters are
/// decoupled).
/// </summary>
internal sealed record CrescendoState(IReadOnlyList<CrescendoTurnRecord> Turns, int EscalationCount, int TotalTurns)
{
    /// <summary>The state of a session with no tracked turns yet.</summary>
    public static CrescendoState Empty { get; } = new([], 0, 0);
}
