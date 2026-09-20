// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>
/// A typed answer from a decision model. Exactly one concrete shape per <see cref="DecisionQuestion"/>
/// shape; the transport guarantees the pairing and throws on a mismatch rather than coercing.
/// </summary>
public abstract record DecisionAnswer
{
    private protected DecisionAnswer() { }

    /// <summary>Guards a probability-like value: finite and within [0, 1].</summary>
    private protected static double EnsureUnit(double value, string member)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0
            ? value
            : throw new ArgumentOutOfRangeException(member, value, $"{member} must be a finite number in [0, 1].");

    /// <summary>Guards a distribution: every value a unit probability, no blank key.</summary>
    private protected static IReadOnlyDictionary<string, double> EnsureDistribution(IReadOnlyDictionary<string, double> probabilities, string member)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        foreach (var (key, p) in probabilities)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException($"{member} has a blank key.", member);
            EnsureUnit(p, $"{member}[{key}]");
        }
        return probabilities;
    }
}

/// <summary>The answer to a <see cref="NoulQuestion"/>: the probability that the answer is yes.</summary>
/// <param name="ProbabilityYes">P(yes), in [0, 1]. This IS the answer; there is no separate confidence.</param>
public sealed record NoulAnswer(double ProbabilityYes) : DecisionAnswer
{
    /// <summary>P(yes), finite and within [0, 1].</summary>
    public double ProbabilityYes { get; } = EnsureUnit(ProbabilityYes, nameof(ProbabilityYes));
}

/// <summary>The answer to a <see cref="ChoiceQuestion"/>: the winning option, the whole distribution, and a confidence.</summary>
/// <param name="Choice">The highest-probability option id.</param>
/// <param name="Probabilities">Probability per option id.</param>
/// <param name="Confidence">The provider's confidence in <paramref name="Choice"/>, derived from the distribution; in [0, 1].</param>
public sealed record ChoiceAnswer(
    string Choice,
    IReadOnlyDictionary<string, double> Probabilities,
    double Confidence) : DecisionAnswer
{
    /// <summary>The winning option id; never blank.</summary>
    public string Choice { get; } = string.IsNullOrWhiteSpace(Choice)
        ? throw new ArgumentException("A choice answer must name the chosen option.", nameof(Choice))
        : Choice;

    /// <summary>Probability per option id; every value in [0, 1].</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; } = EnsureDistribution(Probabilities, nameof(Probabilities));

    /// <summary>Confidence in the choice, in [0, 1].</summary>
    public double Confidence { get; } = EnsureUnit(Confidence, nameof(Confidence));
}

/// <summary>The answer to a <see cref="ScoreQuestion"/>: a probability-weighted score across the levels, the distribution, and a confidence.</summary>
/// <param name="Score">The probability-weighted score across the levels, as the provider reports it (its scale is the 0-based level index range, not [0, 1]).</param>
/// <param name="Probabilities">Probability per level, keyed by the level's index as the provider labels it — 0-based, so <c>"0"</c> is the first criterion (observed from TypeSafe, 2026-09-20).</param>
/// <param name="Legend">Optional: the level descriptions echoed back, keyed like <paramref name="Probabilities"/>.</param>
/// <param name="Confidence">The provider's confidence, in [0, 1].</param>
public sealed record ScoreAnswer(
    double Score,
    IReadOnlyDictionary<string, double> Probabilities,
    IReadOnlyDictionary<string, string>? Legend,
    double Confidence) : DecisionAnswer
{
    /// <summary>The weighted score; finite.</summary>
    public double Score { get; } = double.IsFinite(Score)
        ? Score
        : throw new ArgumentOutOfRangeException(nameof(Score), Score, "Score must be a finite number.");

    /// <summary>Probability per level; every value in [0, 1].</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; } = EnsureDistribution(Probabilities, nameof(Probabilities));

    /// <summary>Confidence in the score, in [0, 1].</summary>
    public double Confidence { get; } = EnsureUnit(Confidence, nameof(Confidence));
}
