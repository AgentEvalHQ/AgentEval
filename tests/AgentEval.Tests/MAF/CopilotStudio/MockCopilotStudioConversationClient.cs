// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.CopilotStudio;
using Microsoft.Agents.Core.Models;

namespace AgentEval.Tests.MAF.CopilotStudio;

/// <summary>
/// A realistic, reusable mock Copilot Studio backend — a test double for
/// <c>ICopilotStudioConversationClient</c> (Stage 4 of the multi-stage marathon session, since no live
/// Copilot Studio system is available). Unlike the minimal delegate-based
/// <c>FakeCopilotStudioConversationClient</c> already local to <c>CopilotStudioChatClientTests</c>, this
/// mock supports SCRIPTED MULTI-TURN conversations (fluent builder, mirroring the repo's existing
/// <c>ScriptedChatClient</c> convention), a SERVER-ASSIGNED conversation id (matching real MCS behavior —
/// the id is issued on <c>StartConversationAsync</c>, not caller-chosen), and CONFIGURABLE ERROR INJECTION
/// (auth failure, rate-limit/429-shaped, malformed/empty activity stream, timeout via cancellation) so
/// downstream resilience code (retry/backoff, agent-fingerprint drift detection) can be exercised without
/// live credentials or network access.
/// </summary>
/// <remarks>
/// <b>Honesty boundary (repeated at every call site that uses this mock — see the P6/Track 2 tests):</b>
/// this proves the BRIDGING LOGIC and any downstream code built against
/// <c>ICopilotStudioConversationClient</c> is correct for the DOCUMENTED activity contract. It does NOT
/// prove a real Copilot Studio agent's actual wire traffic matches — that still requires a real Entra app
/// registration + a real non-prod MCS agent, which remains unavailable in this environment. Every test
/// using this mock says "verified against mock" — never "live-verified" — in its own doc comment.
/// </remarks>
internal sealed class MockCopilotStudioConversationClient : ICopilotStudioConversationClient
{
    private readonly List<Func<string, int, IReadOnlyList<IActivity>>> _turnScript = [];
    private readonly List<IActivity> _startScript = [];
    private readonly string _conversationId;
    private int _askCallCount;

    /// <summary>Total <see cref="StartConversationAsync"/> calls observed.</summary>
    public int StartCallCount { get; private set; }

    /// <summary>Total <see cref="AskQuestionAsync"/> calls observed.</summary>
    public int AskCallCount => _askCallCount;

    /// <summary>Every question asked, in order (for assertion / transcript reconstruction).</summary>
    public List<string> QuestionsAsked { get; } = [];

    /// <summary>The conversation id every <see cref="AskQuestionAsync"/> call was invoked with (null on the first turn).</summary>
    public List<string?> ConversationIdsPassedToAsk { get; } = [];

    /// <summary>
    /// When set, <see cref="StartConversationAsync"/> throws this exception instead of returning activities —
    /// simulates an auth failure or transport error at conversation start.
    /// </summary>
    public Exception? ThrowOnStart { get; set; }

    /// <summary>
    /// When set, <see cref="AskQuestionAsync"/> throws this exception on the Nth call (0-indexed, before any
    /// scripted turn is consulted) — simulates a rate-limit (429-shaped), auth expiry mid-conversation, or a
    /// transient transport failure a caller's retry/backoff logic must handle. ZERO activities are emitted
    /// first — for a failure AFTER some activities already streamed back, see
    /// <see cref="ThrowAfterActivitiesOnAskAtCallIndex"/>.
    /// </summary>
    public (int CallIndex, Exception Exception)? ThrowOnAskAtCallIndex { get; set; }

    /// <summary>
    /// When set, <see cref="AskQuestionAsync"/> emits the given activities on the Nth call (0-indexed) and
    /// THEN throws — simulates a mid-stream transport failure AFTER the server already sent something, i.e.
    /// a real side effect may already have happened. Retry-safety code (<c>CopilotStudioRetryPolicy.ExecuteIdempotentAsync</c>)
    /// must NOT retry this case, unlike <see cref="ThrowOnAskAtCallIndex"/>'s zero-activities case.
    /// </summary>
    public (int CallIndex, IReadOnlyList<IActivity> ActivitiesBeforeFailure, Exception Exception)? ThrowAfterActivitiesOnAskAtCallIndex { get; set; }

    /// <summary>
    /// When set, <see cref="StartConversationAsync"/> emits the given activities and THEN throws — the
    /// <c>StartConversationAsync</c> analogue of <see cref="ThrowAfterActivitiesOnAskAtCallIndex"/>.
    /// </summary>
    public (IReadOnlyList<IActivity> ActivitiesBeforeFailure, Exception Exception)? ThrowAfterActivitiesOnStart { get; set; }

    /// <summary>
    /// When true, every <see cref="AskQuestionAsync"/> call blocks until the caller's own cancellation
    /// token fires — simulates a hung/never-quiescent server so timeout-handling code can be exercised.
    /// </summary>
    public bool HangUntilCancelled { get; set; }

    public MockCopilotStudioConversationClient(string? conversationId = null)
    {
        _conversationId = conversationId ?? $"mock-conv-{Guid.NewGuid():N}";
    }

    /// <summary>Fluent: script the NEXT turn's reply as a single message activity with this text.</summary>
    public MockCopilotStudioConversationClient WithReply(string text)
    {
        _turnScript.Add((_, _) => [Message(text)]);
        return this;
    }

    /// <summary>Fluent: script the NEXT turn to also emit a non-message activity (typing/event) BEFORE the message — tests the fidelity-ceiling filter.</summary>
    public MockCopilotStudioConversationClient WithReplyAndTypingIndicator(string text)
    {
        _turnScript.Add((_, _) => [NonMessage(ActivityTypes.Typing), Message(text)]);
        return this;
    }

    /// <summary>Fluent: script the NEXT turn with a custom activity factory, given the question text and 0-indexed call number.</summary>
    public MockCopilotStudioConversationClient WithTurn(Func<string, int, IReadOnlyList<IActivity>> factory)
    {
        _turnScript.Add(factory);
        return this;
    }

    /// <summary>Fluent: script <see cref="StartConversationAsync"/>'s own activity stream (default: empty — no unsolicited greeting).</summary>
    public MockCopilotStudioConversationClient WithStartActivities(params IActivity[] activities)
    {
        _startScript.Clear();
        _startScript.AddRange(activities);
        return this;
    }

    public async IAsyncEnumerable<IActivity> StartConversationAsync(
        bool emitStartConversationEvent, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StartCallCount++;
        if (ThrowOnStart is not null)
        {
            throw ThrowOnStart;
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowAfterActivitiesOnStart is { } midStreamStart)
        {
            foreach (var activity in midStreamStart.ActivitiesBeforeFailure)
            {
                yield return activity;
            }

            throw midStreamStart.Exception;
        }

        // The server assigns the conversation id on start — a real MCS agent chooses it, the caller doesn't.
        yield return Message(text: null, overrideConversationId: _conversationId);
        foreach (var activity in _startScript)
        {
            yield return activity;
        }
    }

    public async IAsyncEnumerable<IActivity> AskQuestionAsync(
        string question, string? conversationId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var callIndex = _askCallCount++;
        QuestionsAsked.Add(question);
        ConversationIdsPassedToAsk.Add(conversationId);

        if (ThrowOnAskAtCallIndex is { } thrown && thrown.CallIndex == callIndex)
        {
            throw thrown.Exception;
        }

        await Task.Yield();

        if (HangUntilCancelled)
        {
            // Simulates a server that never quiesces: block on the caller's own token so a caller-side
            // timeout/cancellation is what ends the stream, not this mock racing ahead of it.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break; // unreachable — Delay throws OperationCanceledException first
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowAfterActivitiesOnAskAtCallIndex is { } midStreamAsk && midStreamAsk.CallIndex == callIndex)
        {
            foreach (var activity in midStreamAsk.ActivitiesBeforeFailure)
            {
                yield return activity.Conversation?.Id is null or ""
                    ? WithConversationId(activity, _conversationId)
                    : activity;
            }

            throw midStreamAsk.Exception;
        }

        var activities = callIndex < _turnScript.Count
            ? _turnScript[callIndex](question, callIndex)
            : [Message($"[mock default reply — no script for turn {callIndex}]")];

        foreach (var activity in activities)
        {
            // Stamp the mock's server-assigned conversation id onto every emitted activity unless the
            // script already set one explicitly (a real server always stamps Conversation.Id).
            yield return activity.Conversation?.Id is null or ""
                ? WithConversationId(activity, _conversationId)
                : activity;
        }
    }

    // ── realistic activity builders ──

    /// <summary>Builds a real Bot Framework message activity, matching what <c>CopilotStudioChatClient</c> reads (<c>Type</c>, <c>Text</c>, <c>Conversation.Id</c>).</summary>
    public static Activity Message(string? text, string? overrideConversationId = null) => new()
    {
        Type = ActivityTypes.Message,
        Text = text,
        Conversation = overrideConversationId is null ? null : new ConversationAccount { Id = overrideConversationId },
    };

    /// <summary>Builds a non-message activity (typing indicator, event) — must be filtered out, never surfaced as text.</summary>
    public static Activity NonMessage(string type) => new()
    {
        Type = type,
        Text = "MUST NEVER SURFACE — non-message activity",
    };

    private static IActivity WithConversationId(IActivity activity, string conversationId)
    {
        if (activity is Activity concrete)
        {
            concrete.Conversation = new ConversationAccount { Id = conversationId };
            return concrete;
        }

        return activity; // defensive: only the concrete Activity type is mutated in practice
    }
}
