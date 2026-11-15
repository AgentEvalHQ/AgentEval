// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Core;

/// <summary>The score selected from a judge verdict. Selection never changes the original verdict.</summary>
public enum JudgeScorePolicy
{
    /// <summary>Use the judge's overall score (the compatibility default).</summary>
    JudgeOverall,
    /// <summary>Use 100 times the number of met criteria divided by the number of applicable declared criteria.</summary>
    CriteriaMetFraction
}

/// <summary>A stable criterion identity and its independently localizable instruction.</summary>
public sealed record JudgeCriterion
{
    /// <summary>Creates a criterion with a nonempty ASCII identifier and nonblank instruction.</summary>
    public JudgeCriterion(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !char.IsAsciiLetterOrDigit(id[0]) ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            throw new ArgumentException("Expected: a 1–128 character ASCII criterion ID starting with a letter or digit. Actual: invalid ID. " +
                "Suggestions: use letters, digits, underscores, hyphens or dots. Because: localized text must not serve as a stable ID.", nameof(id));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Expected: nonblank criterion text. Actual: no instruction. Suggestions: describe the criterion. " +
                "Because: a judge needs a criterion to assess.", nameof(text));
        Id = id;
        Text = text;
    }

    /// <summary>The case-sensitive stable identifier.</summary>
    public string Id { get; }
    /// <summary>The instruction shown to the judge.</summary>
    public string Text { get; }
}

/// <summary>Content-free evidence of score selection, independent of whether the judge's prompt identity is known.</summary>
public sealed record JudgeScoreSelection
{
    /// <summary>Versioned compatibility policy identifier.</summary>
    public const string OverallPolicyId = "agenteval.judge-overall.v1";
    /// <summary>Versioned criteria-fraction policy identifier.</summary>
    public const string FractionPolicyId = "agenteval.criteria-met-fraction.v1";
    /// <summary>The policy used for this invocation.</summary>
    public required string PolicyId { get; init; }
    /// <summary>The original overall score; never overwritten by selection.</summary>
    public int? RawOverallScore { get; init; }
    /// <summary>The exact selected score on 0–100, or null when it could not be measured.</summary>
    public double? SelectedScore { get; init; }
    /// <summary>Number of declared criteria.</summary>
    public int ExpectedCount { get; init; }
    /// <summary>Applicable denominator; null when the verdict set is incomplete or invalid.</summary>
    public int? ApplicableCount { get; init; }
    /// <summary>Met numerator; null when the verdict set is incomplete or invalid.</summary>
    public int? MetCount { get; init; }
    /// <summary>A stable reason code for an unmeasured selection.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Refuses to pool different score meanings. Missing evidence denotes the legacy overall policy.</summary>
    public static void RequireCompatible(IEnumerable<JudgeScoreSelection?> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Select(s => s?.PolicyId ?? OverallPolicyId).Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            throw new InvalidOperationException("Expected: one score policy per statistical comparison. Actual: incompatible score policies. " +
                "Suggestions: evaluate every arm with the same policy. Because: overall grades and criterion fractions measure different quantities.");
    }
}
