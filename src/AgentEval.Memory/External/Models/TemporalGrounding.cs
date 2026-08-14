// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Whether conversation dates reach the agent as machine-readable timestamps, and whether the
/// harness keeps its own in-text date scaffolding.
/// </summary>
/// <remarks>
/// <para>
/// In the LongMemEval corpus a session's date exists in the dataset metadata and in the text the
/// harness renders — a <c>Session Date:</c> header and a <c>Current Date:</c> prefix. Nothing in
/// that forces an ingesting system to place the messages in time. A system that stamps every
/// message with ingestion time still answers temporal questions correctly, because the answer model
/// reads the dates out of the prompt. So a good temporal score is evidence about the reader, not
/// about the memory, and the benchmark cannot separate real bitemporal storage from none at all.
/// </para>
/// <para>
/// The modes here are meant to be run as a pair.
/// <see cref="TimestampsAndText"/> is the control — timestamps delivered, scaffolding intact — and
/// <see cref="TimestampsOnly"/> removes the scaffolding so the timestamps are the only remaining
/// source of dates. A system that honours them scores the same in both; one that reads dates out of
/// the text drops, and the size of the drop is the measurement.
/// </para>
/// </remarks>
public enum TemporalGroundingMode
{
    /// <summary>
    /// Dates appear only where they always did: dataset metadata and rendered text. Default, and
    /// byte-for-byte the historical behaviour.
    /// </summary>
    None = 0,

    /// <summary>
    /// Sessions carry real timestamps through
    /// <see cref="AgentEval.Core.ITimestampedHistoryInjectableAgent"/> <i>and</i> the in-text date
    /// scaffolding stays. The control arm: a system can pass by honouring either channel.
    /// </summary>
    TimestampsAndText = 1,

    /// <summary>
    /// Sessions carry real timestamps through
    /// <see cref="AgentEval.Core.ITimestampedHistoryInjectableAgent"/> and the harness's in-text
    /// date scaffolding is removed — no session-date headers, no "Current Date:" prefix. A system
    /// that does not place messages in time has nothing left to read.
    /// </summary>
    /// <remarks>
    /// This removes the scaffolding <i>AgentEval</i> adds. It does not, and cannot, remove dates the
    /// speakers themselves put in the conversation, and
    /// <see cref="TemporalGroundingReport.SessionsWithDateLikeContent"/> reports how many sessions
    /// still contain one.
    /// </remarks>
    TimestampsOnly = 2
}

/// <summary>
/// What time-grounding actually did to a run's corpus.
/// </summary>
public sealed class TemporalGroundingReport
{
    /// <summary>The mode the run used.</summary>
    public required TemporalGroundingMode Mode { get; init; }

    /// <summary>Questions whose history was time-grounded.</summary>
    public required int Questions { get; init; }

    /// <summary>Sessions handed to the agent with a real timestamp.</summary>
    public required int SessionsTimestamped { get; init; }

    /// <summary>Turns handed to the agent with a real timestamp.</summary>
    public required int TurnsTimestamped { get; init; }

    /// <summary>Whether the harness removed its own in-text date scaffolding.</summary>
    public required bool InTextDatesRemoved { get; init; }

    /// <summary>Earliest session timestamp in the run, or null when nothing was grounded.</summary>
    public DateTimeOffset? EarliestSessionTimestamp { get; init; }

    /// <summary>Latest session timestamp in the run, or null when nothing was grounded.</summary>
    public DateTimeOffset? LatestSessionTimestamp { get; init; }

    /// <summary>
    /// Sessions whose <i>content</i> still contains something that looks like a date, counted with a
    /// deliberately simple pattern.
    /// </summary>
    /// <remarks>
    /// A lower bound and an honest caveat rather than a metric. Removing the harness's scaffolding
    /// does not remove "I'm flying out on the 3rd of March" from a message a user wrote, so a
    /// non-zero count here is the part of the crutch this mode cannot take away. It counts explicit
    /// numeric dates and four-digit years; phrases like "next Tuesday" are not counted, and are
    /// exactly the phrases that require a timestamp to resolve.
    /// </remarks>
    public required int SessionsWithDateLikeContent { get; init; }
}
