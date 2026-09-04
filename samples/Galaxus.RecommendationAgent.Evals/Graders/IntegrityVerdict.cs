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
    bool? OptOutBackstopFired)
{
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
