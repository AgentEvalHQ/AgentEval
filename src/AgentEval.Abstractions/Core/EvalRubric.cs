// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>The scale a <see cref="EvalRubric"/> asks the judge to score on.</summary>
public enum RubricScoreScale
{
    /// <summary>A number from 0.0 to 1.0.</summary>
    Unit = 0,

    /// <summary>An integer from 0 to 100.</summary>
    Percent = 1,
}

/// <summary>One row of a rubric's severity table: a score at or above <see cref="AtLeast"/> (0..1) has <see cref="Severity"/>.</summary>
/// <param name="AtLeast">The lowest score (0..1) of the row.</param>
/// <param name="Severity">none, low, medium, high or critical.</param>
public sealed record RubricSeverityBand(double AtLeast, string Severity);

/// <summary>
/// The rubric an LLM check grades with: the system prompt its judge is sent, and how the judge's reply is read.
/// </summary>
/// <remarks>
/// <para>
/// A rubric file states the reply it wants (<c>score</c>, <c>label</c>, <c>criteria_results</c>, <c>evidence</c>), the
/// scale of the score, and the bands that turn a score into a verdict. Before 0.43 the agentic checks never sent theirs:
/// every judge ran on a generic default prompt whose reply was read as a 0–100 score (#203 review, B9). This record
/// carries what the reader needs; the text itself is what the judge is sent.
/// </para>
/// <para>
/// The verdict is the band of the SCORE: at or above the check's pass threshold it passes; at or above
/// <see cref="ReviewAt"/> it is "needs review" (a warn, not passed); below, it fails. The judge's own label is kept as
/// evidence. A rubric that states no needs-review band has <see cref="ReviewAt"/> <see langword="null"/>.
/// </para>
/// </remarks>
public sealed record EvalRubric
{
    /// <summary>The prompt id the check declares, e.g. <c>agenteval.goal_decomposition_quality.v1</c>. Recorded as the result's <c>PromptId</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The rubric text, sent as the judge's system prompt.</summary>
    public required string Text { get; init; }

    /// <summary>The scale the rubric asks for.</summary>
    public required RubricScoreScale Scale { get; init; }

    /// <summary>The pass boundary the rubric states (0..1), or <see langword="null"/> when it states none. The check's own threshold decides at run time; a test keeps the two equal.</summary>
    public double? PassAt { get; init; }

    /// <summary>The lowest score (0..1) of the rubric's needs-review band, or <see langword="null"/> when it has none.</summary>
    public double? ReviewAt { get; init; }

    /// <summary>The rubric's severity table, highest <see cref="RubricSeverityBand.AtLeast"/> first; empty when it has none.</summary>
    public IReadOnlyList<RubricSeverityBand> SeverityBands { get; init; } = [];

    /// <summary>True when the rubric grades one of several named dimensions, and the judge must be told which.</summary>
    public bool Dimensional { get; init; }

    /// <summary>The severity the rubric's table gives a score (0..1), or <see langword="null"/> when it has no table.</summary>
    public string? SeverityFor(double score)
    {
        foreach (var band in SeverityBands)
        {
            if (score >= band.AtLeast)
                return band.Severity;
        }

        return SeverityBands.Count == 0 ? null : SeverityBands[^1].Severity;
    }

    /// <summary>Throws when the record cannot be read consistently: an empty id or text, a boundary off 0..1, a review band at or above the pass boundary, or a severity table out of order.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("A rubric needs an id.", nameof(Id));
        if (string.IsNullOrWhiteSpace(Text)) throw new ArgumentException($"Rubric '{Id}' has no text.", nameof(Text));
        static bool InUnit(double v) => double.IsFinite(v) && v >= 0.0 && v <= 1.0;
        if (PassAt is { } pass && !InUnit(pass)) throw new ArgumentOutOfRangeException(nameof(PassAt), pass, $"Rubric '{Id}': PassAt must be in [0, 1].");
        if (ReviewAt is { } review && !InUnit(review)) throw new ArgumentOutOfRangeException(nameof(ReviewAt), review, $"Rubric '{Id}': ReviewAt must be in [0, 1].");
        if (PassAt is { } p && ReviewAt is { } r && r >= p) throw new ArgumentException($"Rubric '{Id}': ReviewAt ({r}) must be below PassAt ({p}).");
        for (var i = 0; i < SeverityBands.Count; i++)
        {
            if (!InUnit(SeverityBands[i].AtLeast)) throw new ArgumentOutOfRangeException(nameof(SeverityBands), $"Rubric '{Id}': severity band {i} is off [0, 1].");
            if (i > 0 && SeverityBands[i].AtLeast >= SeverityBands[i - 1].AtLeast) throw new ArgumentException($"Rubric '{Id}': severity bands must be highest first.");
            if (SeverityBands[i].Severity is not ("none" or "low" or "medium" or "high" or "critical"))
                throw new ArgumentException($"Rubric '{Id}': unknown severity '{SeverityBands[i].Severity}'.");
        }
    }
}

/// <summary>A judge that can grade with a given <see cref="EvalRubric"/>.</summary>
public interface IRubricBindable
{
    /// <summary>A judge on the same model that sends <paramref name="rubric"/> and reads its reply on the rubric's scale.</summary>
    /// <param name="rubric">The rubric to grade with.</param>
    /// <param name="dimension">The dimension to grade, for a <see cref="EvalRubric.Dimensional"/> rubric; ignored otherwise.</param>
    IEvaluator WithRubric(EvalRubric rubric, string? dimension);
}

/// <summary>One piece of evidence a judge cited.</summary>
public sealed class JudgeEvidence
{
    /// <summary>Where it is (e.g. response, query, plan).</summary>
    public string Source { get; init; } = "";

    /// <summary>A short excerpt.</summary>
    public string Reference { get; init; } = "";

    /// <summary>Why it matters.</summary>
    public string Message { get; init; } = "";
}
