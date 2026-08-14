// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External.LongMemEval;

public partial class LongMemEvalBenchmarkRunner
{
    /// <summary>
    /// Runs the embedded time-grounded probe: questions whose answers depend on the clock, over a
    /// corpus whose conversations contain no absolute dates at all.
    /// </summary>
    /// <param name="agent">
    /// The agent under test. It must implement
    /// <see cref="ITimestampedHistoryInjectableAgent"/> unless
    /// <paramref name="options"/> sets <see cref="TemporalGroundingMode.None"/>.
    /// </param>
    /// <param name="options">
    /// Null uses <see cref="LongMemEvalTimeGroundedCorpus.ProbeOptions"/> — timestamps only, in-text
    /// dates removed. Pass <see cref="LongMemEvalTimeGroundedCorpus.ControlOptions"/> for the
    /// control arm.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// The number this returns is only interesting next to its control. Run the probe and the
    /// control over the same agent: equal scores mean the system honours the timestamps it was
    /// given; a drop under the probe is the share of its temporal score that was coming from dates
    /// printed in the prompt.
    /// </para>
    /// <para>
    /// Twelve questions, authored by AgentEval, not comparable with LongMemEval scores. See
    /// <see cref="LongMemEvalTimeGroundedCorpus"/>.
    /// </para>
    /// </remarks>
    public async Task<ExternalBenchmarkResult> RunTimeGroundedAsync(
        IEvaluableAgent agent,
        ExternalBenchmarkOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        options ??= LongMemEvalTimeGroundedCorpus.ProbeOptions;
        options.Validate();

        var entries = LongMemEvalTimeGroundedCorpus.Load(options);
        return await RunSelectedAsync(agent, options, entries, executionLabel: null, ct);
    }

    /// <summary>
    /// Runs the time-grounded probe through the oracle arm, giving the ceiling for a corpus whose
    /// dates exist only as timestamps.
    /// </summary>
    /// <param name="answerClient">Chat client that answers the projected questions. Required.</param>
    /// <param name="options">Null uses <see cref="LongMemEvalTimeGroundedCorpus.ProbeOptions"/>.</param>
    /// <param name="oracleOptions">
    /// Evidence fraction and distractor count. Null runs the gold-only ceiling.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <see cref="LongMemEvalOracleReader"/> implements
    /// <see cref="ITimestampedHistoryInjectableAgent"/> by writing each turn's instant into its own
    /// prompt, which makes it a system that places messages in time perfectly, by construction. That
    /// is the point of a ceiling: it says what the answer model can do when the temporal information
    /// is genuinely present, so a lower score elsewhere is about the memory rather than the reader.
    /// </remarks>
    public async Task<ExternalBenchmarkResult> RunTimeGroundedOracleAsync(
        IChatClient answerClient,
        ExternalBenchmarkOptions? options = null,
        LongMemEvalOracleOptions? oracleOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answerClient);
        options ??= LongMemEvalTimeGroundedCorpus.ProbeOptions;
        options.Validate();
        oracleOptions ??= LongMemEvalOracleOptions.GoldOnly;
        oracleOptions.Validate();

        var projections = LongMemEvalTimeGroundedCorpus.Load(options)
            .Select(entry => LongMemEvalOracleProjector.Project(entry, oracleOptions, options.RandomSeed))
            .ToArray();

        return await RunSelectedAsync(
            new LongMemEvalOracleReader(answerClient),
            options,
            projections.Select(projection => projection.Entry).ToArray(),
            executionLabel: "oracle",
            ct,
            dataset: null,
            OracleProjectionReport.From(
                oracleOptions,
                projections.Select(projection => projection.Realised).ToArray()));
    }
}
