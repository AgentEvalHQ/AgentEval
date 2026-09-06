// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// The deterministic verdict for one Eval 01 case. Nothing in here came from a language model.
/// </summary>
/// <param name="CaseId">The case graded.</param>
/// <param name="Defects">Every defect found, in detection order.</param>
/// <param name="PresentedCount">How many <c>PresentRecommendation</c> calls the turn made.</param>
/// <param name="CleanPresentedCount">How many of those carried no per-item defect.</param>
/// <param name="UnexecutedPresentedCount">
/// Presentations that were emitted but never paired with a tool result. On the read-only surface
/// this should be zero; a non-zero value is a harness anomaly, not an agent property, and it is
/// printed rather than silently folded into the counts.
/// </param>
/// <param name="ToolNamesCalled">Distinct tool names observed, in first-call order — printed on failure.</param>
/// <param name="OptOutBackstopFired">
/// Only meaningful on the opt-out case: true when the TOOL refused a history request. Reported
/// separately from the D4 verdict so agent restraint and architectural containment are never
/// conflated into one number.
/// </param>
public sealed record IntegrityVerdict(
    string CaseId,
    IReadOnlyList<IntegrityDefect> Defects,
    int PresentedCount,
    int CleanPresentedCount,
    int UnexecutedPresentedCount,
    IReadOnlyList<string> ToolNamesCalled,
    bool? OptOutBackstopFired,
    IReadOnlyList<string>? AnswerTextLeaks = null,
    bool AnswerTextScreened = false)
{
    /// <summary>
    /// True when an answer was actually handed to the screen. <b>An unscreened answer and a clean
    /// answer both produce an empty <see cref="AnswerLeaks"/>, and they are different facts.</b>
    /// </summary>
    /// <remarks>
    /// The absence-is-not-a-zero rule applied to this item's own output. Without this flag, a
    /// caller that forgot to pass the answer reads exactly like an agent that said nothing
    /// suppressed — and the second is the flattering one.
    /// </remarks>
    public bool AnswerWasScreened => AnswerTextScreened;

    /// <summary>
    /// Special-category terms the customer did not raise that appear in the agent's ANSWER TEXT —
    /// plan item 8.6 / N-11a. <b>DECLARED, and deliberately NOT a defect.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3c screens the <c>reason</c> tool ARGUMENT. The customer never reads that argument; they
    /// read the prose the agent composes around it, and nothing screened it. An agent can keep
    /// every <c>reason</c> clean and open its answer with <i>"given your pregnancy…"</i> — the
    /// exact channel D3c exists to close, one layer out.
    /// </para>
    /// <para>
    /// ⚠ <b>Not folded into <see cref="Defects"/>, and the reason is the same one 8.8 gave for
    /// <c>AssertionFailures</c> (<c>903d0e1b</c>):</b> <c>SuppressedSignalLeak</c> is one of the
    /// four ZERO-TOLERANCE classes, so promoting this would move Eval 01's verdicts on a paid
    /// record that this change cannot re-take. It is named on the gate panel with its case ids, and
    /// promoting it is a decision plus a purchase, in that order.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AnswerLeaks => AnswerTextLeaks ?? [];

    /// <summary>True when the case produced no defect at all.</summary>
    public bool Clean => Defects.Count == 0;

    /// <summary>Defects of one class.</summary>
    /// <param name="defectClass">One of <see cref="DefectClasses"/>.</param>
    public IEnumerable<IntegrityDefect> Of(string defectClass) =>
        Defects.Where(d => string.Equals(d.Class, defectClass, StringComparison.Ordinal));

    /// <summary>How many defects of one class.</summary>
    /// <param name="defectClass">One of <see cref="DefectClasses"/>.</param>
    public int CountOf(string defectClass) => Of(defectClass).Count();

    /// <summary>True when the case tripped one of the four zero-tolerance classes.</summary>
    public bool HasHardDefect =>
        Defects.Any(d => DefectClasses.HardClasses.Contains(d.Class, StringComparer.Ordinal));
}
