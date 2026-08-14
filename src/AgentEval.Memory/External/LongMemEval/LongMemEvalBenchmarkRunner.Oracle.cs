// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.External.LongMemEval;

public partial class LongMemEvalBenchmarkRunner
{
    /// <summary>
    /// Runs the oracle arm on its own: the labelled evidence is projected into context and answered
    /// by a retrieval-bypassing reader that owns no memory provider.
    /// </summary>
    /// <param name="answerClient">Chat client that answers the projected questions. Required.</param>
    /// <param name="options">
    /// The usual benchmark options — selection, judging, provenance, answer sampling. Required.
    /// </param>
    /// <param name="oracleOptions">
    /// Evidence fraction and distractor count. Null runs the gold-only ceiling
    /// (<see cref="LongMemEvalOracleOptions.GoldOnly"/>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The same <see cref="ExternalBenchmarkResult"/> shape every other arm returns — a
    /// <see cref="QuestionResult"/> per question and a <see cref="SampleComposition"/> — plus
    /// <see cref="ExternalBenchmarkResult.OracleProjection"/> describing the corpus that was
    /// actually built.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The arm measures the dataset and the answer model, not a memory system: nothing is stored and
    /// nothing is retrieved. That is what makes it the ceiling the other arms are read against, and
    /// why it is worth every caller running the same one rather than each re-deriving it.
    /// </para>
    /// <para>
    /// Two controls move it off the ceiling deliberately.
    /// <see cref="LongMemEvalOracleOptions.DistractorSessions"/> adds non-evidence sessions from the
    /// question's own haystack, and <see cref="LongMemEvalOracleOptions.GoldSessionFraction"/> keeps
    /// only part of the evidence. Both are reproducible under
    /// <see cref="ExternalBenchmarkOptions.RandomSeed"/>, and both report what they realised, because
    /// a level that degraded nothing and a level whose degradation did not matter are different
    /// findings that a score alone cannot tell apart.
    /// </para>
    /// </remarks>
    public async Task<ExternalBenchmarkResult> RunOracleAsync(
        IChatClient answerClient,
        ExternalBenchmarkOptions options,
        LongMemEvalOracleOptions? oracleOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answerClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        oracleOptions ??= LongMemEvalOracleOptions.GoldOnly;
        oracleOptions.Validate();

        var loaded = LoadEntries(options);
        var projections = loaded.Entries
            .Select(entry => LongMemEvalOracleProjector.Project(entry, oracleOptions, options.RandomSeed))
            .ToArray();
        var projectedEntries = projections.Select(projection => projection.Entry).ToArray();
        var report = OracleProjectionReport.From(
            oracleOptions,
            projections.Select(projection => projection.Realised).ToArray());

        _logger.LogInformation(
            "LongMemEval oracle arm: {Questions} questions, evidence kept {Kept}/{Available}, " +
            "distractors added {Added} of {Requested} requested",
            report.Questions,
            report.GoldSessionsKept,
            report.GoldSessionsAvailable,
            report.DistractorSessionsAdded,
            report.RequestedDistractorSessions * report.Questions);

        return await RunSelectedAsync(
            new LongMemEvalOracleReader(answerClient),
            options,
            projectedEntries,
            executionLabel: "oracle",
            ct,
            loaded with { Entries = projectedEntries },
            report);
    }
}
