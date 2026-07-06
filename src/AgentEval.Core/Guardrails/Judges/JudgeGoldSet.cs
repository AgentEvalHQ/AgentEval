// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>One labelled calibration case: a piece of text and whether a correct judge should block it.</summary>
/// <param name="Text">The turn text the judge inspects.</param>
/// <param name="ShouldBlock"><c>true</c> if this is an attack the judge must block; <c>false</c> if it is benign and must be allowed.</param>
/// <param name="Note">Optional human note (source, attack family, why).</param>
public sealed record JudgeGoldCase(string Text, bool ShouldBlock, string? Note = null);

/// <summary>
/// A per-axis calibration corpus for a judge — the "gold set" the <see cref="GateCalibrationHarness"/> scores a
/// judge against before it may go inline. It must be <b>both-directions</b>: attack cases (that must block) AND
/// benign cases (that must be allowed). A one-sided set can't measure both missed attacks and false alarms, so a
/// judge could look perfect while being a keyword matcher or a block-everything gate — the exact failure the
/// repo's oracle-honesty work guards against.
/// </summary>
public sealed class JudgeGoldSet
{
    /// <summary>The axis these cases calibrate (matches the rubric's <see cref="IJudgeRubric.Axis"/>).</summary>
    public string Axis { get; }

    /// <summary>The labelled cases (both directions).</summary>
    public IReadOnlyList<JudgeGoldCase> Cases { get; }

    /// <summary>Attack cases that must block.</summary>
    public int AttackCount { get; }

    /// <summary>Benign cases that must be allowed.</summary>
    public int BenignCount { get; }

    /// <summary>Creates the gold set. Requires at least one attack AND one benign case (both directions).</summary>
    public JudgeGoldSet(string axis, IEnumerable<JudgeGoldCase> cases)
    {
        if (string.IsNullOrWhiteSpace(axis))
        {
            throw new ArgumentException("axis must be non-empty.", nameof(axis));
        }

        ArgumentNullException.ThrowIfNull(cases);
        Axis = axis;
        Cases = cases.ToList();

        AttackCount = Cases.Count(c => c.ShouldBlock);
        BenignCount = Cases.Count - AttackCount;
        if (AttackCount == 0 || BenignCount == 0)
        {
            throw new ArgumentException(
                $"A gold set must be both-directions: got {AttackCount} attack and {BenignCount} benign cases (need ≥1 of each).",
                nameof(cases));
        }
    }
}
