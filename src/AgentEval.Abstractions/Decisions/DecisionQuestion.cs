// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>
/// A typed question for a decision model. Three shapes exist, and they are the provider-neutral
/// names for the three answer shapes a System One model returns (ADR-033): a yes/no probability
/// (<see cref="BinaryQuestion"/>), a distribution over named alternatives (<see cref="ChoiceQuestion"/>),
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
/// A yes/no question (TypeSafe calls this shape "noul" on the wire; the contract keeps the neutral name).
/// The answer is <see cref="BinaryAnswer.TrueProbability"/> — the model's probability that the proposition
/// is true — and <b>nothing else</b>: a binary answer carries no separate confidence field, so no
/// consumer should invent one.
/// </summary>
/// <param name="Instructions">The yes/no question.</param>
/// <param name="TrueCriteria">Optional: what a <c>yes</c> means, to sharpen the boundary.</param>
/// <param name="FalseCriteria">Optional: what a <c>no</c> means.</param>
public sealed record BinaryQuestion(
    string Instructions,
    string? TrueCriteria = null,
    string? FalseCriteria = null) : DecisionQuestion(Instructions);

/// <summary>
/// A one-of-N question. The answer names the highest-probability option and carries the whole
/// distribution plus a confidence derived from it.
/// </summary>
/// <param name="Instructions">The classification question.</param>
/// <param name="Criteria">The options, keyed by option id, each with a description. At least 2 entries; a provider may cap the count.</param>
public sealed record ChoiceQuestion(
    string Instructions,
    IReadOnlyDictionary<string, string> Criteria) : DecisionQuestion(Instructions)
{
    /// <summary>The fewest options a choice can offer; anything less is not a choice.</summary>
    public const int MinOptions = 2;

    /// <summary>The options; at least <see cref="MinOptions"/> entries, no blank key. Provider maximums are enforced by the transport.</summary>
    public IReadOnlyDictionary<string, string> Criteria { get; } = Validate(Criteria);

    private static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count < MinOptions)
            throw new ArgumentOutOfRangeException(nameof(Criteria), criteria.Count, $"A choice question needs at least {MinOptions} options.");
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
/// <param name="Criteria">Level descriptions in ascending order. At least 2 levels; a provider may cap the count.</param>
public sealed record ScoreQuestion(
    string Instructions,
    IReadOnlyList<string> Criteria) : DecisionQuestion(Instructions)
{
    /// <summary>The fewest levels an ordered scale can have; anything less is not a scale.</summary>
    public const int MinLevels = 2;

    /// <summary>The level descriptions, lowest first; at least <see cref="MinLevels"/> of them. Provider maximums are enforced by the transport.</summary>
    public IReadOnlyList<string> Criteria { get; } = Validate(Criteria);

    private static IReadOnlyList<string> Validate(IReadOnlyList<string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count < MinLevels)
            throw new ArgumentOutOfRangeException(nameof(Criteria), criteria.Count, $"A score question needs at least {MinLevels} levels.");
        for (var i = 0; i < criteria.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(criteria[i]))
                throw new ArgumentException($"Score level {i} has a blank description.", nameof(Criteria));
        }
        return criteria;
    }
}
