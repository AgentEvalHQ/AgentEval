// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>
/// A typed question for a decision model. Three shapes exist, and they are the provider-neutral
/// names for the three answer shapes a System One model returns (ADR-033): a yes/no probability
/// (<see cref="NoulQuestion"/>), a distribution over named alternatives (<see cref="ChoiceQuestion"/>),
/// and a probability-weighted position on an ordered scale (<see cref="ScoreQuestion"/>).
/// </summary>
/// <param name="Instructions">What is being asked about the state. Plain text.</param>
public abstract record DecisionQuestion(string Instructions)
{
    /// <summary>What is being asked; never blank.</summary>
    public string Instructions { get; } = string.IsNullOrWhiteSpace(Instructions)
        ? throw new ArgumentException("A question needs instructions.", nameof(Instructions))
        : Instructions;
}

/// <summary>
/// A yes/no question. The answer is <see cref="NoulAnswer.ProbabilityYes"/> — the model's probability
/// that the answer is yes — and <b>nothing else</b>: a noul answer carries no separate confidence
/// field, so no consumer should invent one.
/// </summary>
/// <param name="Instructions">The yes/no question.</param>
/// <param name="TrueCriteria">Optional: what a <c>yes</c> means, to sharpen the boundary.</param>
/// <param name="FalseCriteria">Optional: what a <c>no</c> means.</param>
public sealed record NoulQuestion(
    string Instructions,
    string? TrueCriteria = null,
    string? FalseCriteria = null) : DecisionQuestion(Instructions);

/// <summary>
/// A one-of-N question. The answer names the highest-probability option and carries the whole
/// distribution plus a confidence derived from it.
/// </summary>
/// <param name="Instructions">The classification question.</param>
/// <param name="Criteria">The options, keyed by option id, each with a description. 2 to 255 entries.</param>
public sealed record ChoiceQuestion(
    string Instructions,
    IReadOnlyDictionary<string, string> Criteria) : DecisionQuestion(Instructions)
{
    /// <summary>The largest option count a System One model accepts.</summary>
    public const int MaxOptions = 255;

    /// <summary>The options; 2 to <see cref="MaxOptions"/> entries, no blank key.</summary>
    public IReadOnlyDictionary<string, string> Criteria { get; } = Validate(Criteria);

    private static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 2 or > MaxOptions)
            throw new ArgumentOutOfRangeException(nameof(Criteria), criteria.Count, $"A choice question needs 2 to {MaxOptions} options.");
        foreach (var (key, description) in criteria)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Choice option ids must not be blank.", nameof(Criteria));
            if (description is null)
                throw new ArgumentException($"Choice option '{key}' has a null description.", nameof(Criteria));
        }
        return criteria;
    }
}

/// <summary>
/// An ordered-scale question. <see cref="Criteria"/> describes each level from lowest to highest; the
/// answer is a probability-weighted score across those levels, the distribution, and a confidence.
/// </summary>
/// <param name="Instructions">The scoring question.</param>
/// <param name="Criteria">Level descriptions in ascending order. 2 to 10 levels.</param>
public sealed record ScoreQuestion(
    string Instructions,
    IReadOnlyList<string> Criteria) : DecisionQuestion(Instructions)
{
    /// <summary>The fewest levels a System One model accepts.</summary>
    public const int MinLevels = 2;

    /// <summary>The most levels a System One model accepts.</summary>
    public const int MaxLevels = 10;

    /// <summary>The level descriptions, lowest first; <see cref="MinLevels"/> to <see cref="MaxLevels"/> of them.</summary>
    public IReadOnlyList<string> Criteria { get; } = Validate(Criteria);

    private static IReadOnlyList<string> Validate(IReadOnlyList<string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < MinLevels or > MaxLevels)
            throw new ArgumentOutOfRangeException(nameof(Criteria), criteria.Count, $"A score question needs {MinLevels} to {MaxLevels} levels.");
        for (var i = 0; i < criteria.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(criteria[i]))
                throw new ArgumentException($"Score level {i} has a blank description.", nameof(Criteria));
        }
        return criteria;
    }
}
