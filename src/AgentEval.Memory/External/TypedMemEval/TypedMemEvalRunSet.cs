// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>An observed range for one count across a set of runs.</summary>
public sealed record TypedMemEvalBand(int Minimum, int Maximum, double Mean)
{
    /// <summary>Spread between the extremes. Zero means every run agreed exactly.</summary>
    public int Width => Maximum - Minimum;
}

/// <summary>
/// What a set of constant-configuration runs agreed and disagreed about.
/// </summary>
public sealed class TypedMemEvalRunSetSummary
{
    /// <summary>Runs summarised.</summary>
    public required int Runs { get; init; }

    /// <summary>The vertical every run measured.</summary>
    public required string Vertical { get; init; }

    /// <summary>The corpus every run used.</summary>
    public required string CorpusSha256 { get; init; }

    /// <summary>Outcome-count bands across the run set.</summary>
    public required IReadOnlyDictionary<TypedMemEvalOutcome, TypedMemEvalBand> Outcomes { get; init; }

    /// <summary>
    /// Questions whose outcome was not identical in every run. This is the number that says
    /// whether a band is evidence.
    /// </summary>
    /// <remarks>
    /// A two-run set can agree by coincidence and band to zero width, which reads as perfect
    /// stability and is not. Reported alongside the bands so a narrow band over a small run set is
    /// legible as weak evidence rather than strong.
    /// </remarks>
    public required int QuestionsWithFlips { get; init; }

    /// <summary>The ids of those questions, sorted, so a flip can be looked at rather than guessed at.</summary>
    public required IReadOnlyList<string> FlippedQuestionIds { get; init; }

    /// <summary>Questions present in every run.</summary>
    public required int QuestionsCompared { get; init; }

    /// <summary>
    /// True when the set holds only two runs. The ADR's floor is two and its recommendation is
    /// three; a caller rendering these bands should say which of the two it has.
    /// </summary>
    public bool AtMinimumRunCount => Runs == 2;
}

/// <summary>
/// Thrown when a caller asks for bands across runs that are not comparable.
/// </summary>
/// <remarks>
/// Banding non-comparable runs would manufacture stability out of a configuration change, which is
/// precisely the failure the answer-pinning work exists to prevent. Refusing is the whole feature.
/// </remarks>
public sealed class TypedMemEvalRunSetMismatchException : InvalidOperationException
{
    /// <summary>What differed between the runs.</summary>
    public string Dimension { get; }

    internal TypedMemEvalRunSetMismatchException(string dimension, string message)
        : base(message)
        => Dimension = dimension;
}

/// <summary>
/// Summarises repeated runs of one vertical into bands.
/// </summary>
/// <remarks>
/// Composes with <see cref="TypedMemEvalOptions.AnswerSeed"/>: pin the seed to measure the memory
/// system's own variance, vary it to measure the answer model's. What it will not do is band across
/// runs that differed in corpus, configuration, or what the provider did with the requested
/// sampling — those are different experiments, and averaging them produces a number describing
/// neither.
/// </remarks>
public static class TypedMemEvalRunSet
{
    /// <summary>Computes bands across a set of comparable runs.</summary>
    /// <param name="results">Two or more runs of the same vertical under the same configuration.</param>
    /// <exception cref="ArgumentException">When fewer than two runs are supplied.</exception>
    /// <exception cref="TypedMemEvalRunSetMismatchException">When the runs are not comparable.</exception>
    public static TypedMemEvalRunSetSummary Summarize(IReadOnlyList<ExternalBenchmarkResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count < 2)
        {
            throw new ArgumentException(
                "Banding needs at least two runs. One run is a point, and reporting it as a band " +
                "would claim a stability that was never measured.",
                nameof(results));
        }

        var typed = new List<TypedMemEvalReport>(results.Count);
        foreach (var result in results)
        {
            typed.Add(result.TypedOutcomes ?? throw new ArgumentException(
                $"Run '{result.BenchmarkName}' carries no TypedOutcomes, so it is not a TypedMemEval " +
                $"result and cannot be banded with ones that are.",
                nameof(results)));
        }

        RequireSame("vertical", typed.Select(t => t.Vertical));
        RequireSame("corpus SHA-256", typed.Select(t => t.CorpusSha256));
        RequireSame("judge prompt fingerprint", typed.Select(t => t.JudgePromptFingerprint));
        RequireSame("options fingerprint", results.Select(OptionsFingerprint));
        RequireSame("answer-sampling disposition", results.Select(SamplingFingerprint));

        // Only questions every run actually ran are compared. A run that sampled a different subset
        // would otherwise contribute flips that are really just absences.
        var perRun = results
            .Select(r => r.QuestionResults
                .Where(q => q.TypedOutcome is not null)
                .ToDictionary(q => q.QuestionId, q => q.TypedOutcome!.Outcome, StringComparer.Ordinal))
            .ToList();

        var shared = perRun
            .Skip(1)
            .Aggregate(
                new HashSet<string>(perRun[0].Keys, StringComparer.Ordinal),
                (acc, run) => { acc.IntersectWith(run.Keys); return acc; });

        var flipped = shared
            .Where(id => perRun.Select(run => run[id]).Distinct().Count() > 1)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var outcomes = Enum.GetValues<TypedMemEvalOutcome>()
            .ToDictionary(
                outcome => outcome,
                outcome =>
                {
                    var counts = perRun
                        .Select(run => shared.Count(id => run[id] == outcome))
                        .ToArray();
                    return new TypedMemEvalBand(counts.Min(), counts.Max(), counts.Average());
                });

        return new TypedMemEvalRunSetSummary
        {
            Runs = results.Count,
            Vertical = typed[0].Vertical,
            CorpusSha256 = typed[0].CorpusSha256,
            Outcomes = outcomes,
            QuestionsWithFlips = flipped.Length,
            FlippedQuestionIds = flipped,
            QuestionsCompared = shared.Count
        };
    }

    private static void RequireSame(string dimension, IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1)
        {
            throw new TypedMemEvalRunSetMismatchException(
                dimension,
                $"Runs differ in {dimension} ({string.Join(", ", distinct.Select(Shorten))}). They are " +
                $"different experiments, and a band across them would report a stability that no run " +
                $"measured.");
        }
    }

    private static string Shorten(string value)
        => value.Length > 16 ? value[..16] + "…" : value;

    /// <summary>
    /// The configuration dimensions that change what a run measures. Deliberately not every option:
    /// evidence-capture depth and raw-response retention change what is <i>recorded</i>, not what
    /// the system was asked, so banding across them is legitimate.
    /// </summary>
    private static string OptionsFingerprint(ExternalBenchmarkResult result)
    {
        var options = result.Options;
        var material = string.Join(
            "|",
            result.BenchmarkId,
            options.DatasetMode ?? "",
            options.TemporalGrounding.ToString(),
            options.HistoryInjectionMode.ToString(),
            options.MaxQuestions?.ToString(CultureInfo.InvariantCulture) ?? "",
            options.RandomSeed?.ToString(CultureInfo.InvariantCulture) ?? "",
            options.AnswerTemperature?.ToString("R", CultureInfo.InvariantCulture) ?? "",
            options.AnswerSeed?.ToString(CultureInfo.InvariantCulture) ?? "",
            options.JudgeTemperature?.ToString("R", CultureInfo.InvariantCulture) ?? "",
            options.JudgeVerdictProtocol.ToString(),
            options.MaxJudgeRetries.ToString(CultureInfo.InvariantCulture));
        // Not a security function: this identifies which configuration produced a run.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>
    /// What the provider actually did with the requested sampling.
    /// </summary>
    /// <remarks>
    /// Two runs that requested the same seed but where one reached the provider and the other was
    /// silently dropped are not repeats of one experiment — one was pinned and one was not. That is
    /// exactly the case a band would hide, so it is part of the comparability key rather than a
    /// footnote beside it.
    /// </remarks>
    private static string SamplingFingerprint(ExternalBenchmarkResult result)
    {
        var sampling = result.AnswerSampling;
        if (sampling is null)
            return "none";
        return string.Join(
            "|",
            sampling.Temperature.CarriedByEveryQuestion,
            sampling.Temperature.ConfirmedByEveryQuestion,
            sampling.Seed.CarriedByEveryQuestion,
            sampling.Seed.ConfirmedByEveryQuestion);
    }
}
