// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Core;

/// <summary>
/// One injected exchange together with the instant it happened.
/// </summary>
/// <param name="UserMessage">The user turn.</param>
/// <param name="AssistantResponse">The assistant turn that answered it.</param>
/// <param name="Timestamp">
/// When the exchange occurred. This is the value an ingesting system is expected to store as the
/// message's own time, not the time it was ingested.
/// </param>
/// <param name="SessionIndex">
/// Zero-based index of the conversation session this exchange belongs to, so an implementation can
/// group turns into sessions without re-deriving boundaries from the text.
/// </param>
public readonly record struct TimestampedConversationTurn(
    string UserMessage,
    string AssistantResponse,
    DateTimeOffset Timestamp,
    int SessionIndex);

/// <summary>
/// A conversation history in which every exchange carries its own instant, plus the instant the
/// question is being asked.
/// </summary>
/// <remarks>
/// The two are separate because a temporal question needs both: "what did I say I would do" is
/// anchored by the turn's timestamp, and "has it happened yet" is anchored by
/// <see cref="QueryTime"/>. A system that has only one of them can answer neither honestly.
/// </remarks>
public sealed class TimestampedConversationHistory
{
    /// <summary>The exchanges to inject, in conversation order.</summary>
    public required IReadOnlyList<TimestampedConversationTurn> Turns { get; init; }

    /// <summary>
    /// The wall-clock instant at which the question is asked — the "now" against which
    /// already-happened and not-yet-happened are decided.
    /// </summary>
    public required DateTimeOffset QueryTime { get; init; }
}

/// <summary>
/// Capability interface for agents that accept conversation history with real per-message
/// timestamps, so a benchmark can test whether a memory system places messages in time.
/// </summary>
/// <remarks>
/// <para>
/// Conversation dates in the LongMemEval corpus reach an agent as text — a session-date header and a
/// "Current Date:" prefix — and nothing about that forces an ingesting system to place messages in
/// time. A system that stamps every message with ingestion time, or with a counter, still scores
/// well on temporal questions, because the model reads the dates out of the text. The consequence is
/// that the benchmark cannot tell a system with real bitemporal memory from one with none.
/// </para>
/// <para>
/// This interface is the channel that closes that gap. Under
/// <c>TemporalGroundingMode.TimestampsOnly</c> the harness removes its own in-text date scaffolding
/// and hands the dates here instead, so a system that ignores them has nothing left to read. An
/// implementation is expected to store <see cref="TimestampedConversationTurn.Timestamp"/> as the
/// message's valid time — the time it happened — rather than as the time it was ingested.
/// </para>
/// </remarks>
public interface ITimestampedHistoryInjectableAgent
{
    /// <summary>
    /// Injects conversation history whose messages carry real timestamps, without making LLM calls.
    /// </summary>
    /// <param name="history">The timestamped turns and the query time.</param>
    void InjectTimestampedConversationHistory(TimestampedConversationHistory history);
}
