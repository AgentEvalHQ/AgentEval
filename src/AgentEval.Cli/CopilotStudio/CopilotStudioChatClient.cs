// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// Bridges <c>Microsoft.Agents.CopilotStudio.Client</c>'s conversational, streaming-activity API
/// (<c>StartConversationAsync</c> / <c>AskQuestionAsync</c> → <c>IAsyncEnumerable&lt;IActivity&gt;</c>, Bot
/// Framework activity schema) into an <see cref="IChatClient"/>, so it can be wrapped in a MAF <c>ChatClientAgent</c>
/// and handed to <see cref="CopilotStudioAgentFactory.FromAgent"/> exactly like every other agent this CLI drives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated shim, not a reuse of <c>AzureChatAgentFactory</c>'s plumbing:</b> Copilot Studio's wire model
/// is fundamentally different from a chat-completions endpoint. There is no single request/response call — the
/// SDK exposes one long-lived, stateful conversation (<c>StartConversationAsync</c> once, then repeated
/// <c>AskQuestionAsync</c> calls that the SDK auto-correlates to a server-side <c>conversationId</c>), and each
/// call streams zero-or-more Bot Framework <see cref="IActivity"/> frames rather than chat-completion deltas.
/// </para>
/// <para>
/// <b>Fidelity ceiling (text-only).</b> Only <c>Type == "message"</c> activities with non-empty <c>Text</c> are
/// surfaced — matches <see cref="CopilotStudioAgentFactory"/>'s documented <c>SutTier.TextOnly</c> /
/// <c>EvidenceFidelity.Verbal</c> ceiling. Adaptive cards, suggested actions, and other rich-content activities are
/// silently dropped rather than fabricated as text; a turn with no message-typed activity legitimately returns an
/// empty response rather than inventing content.
/// </para>
/// <para>
/// <b>Session semantics.</b> One instance == one Copilot Studio conversation. The very first call starts the
/// conversation (<c>emitStartConversationEvent: false</c> — this shim answers exactly the caller's first question;
/// it does not want a Topic-triggered greeting mixed into turn 1's transcript uninvited) and remembers the
/// server-issued <see cref="IActivity.Conversation"/>.<c>Id</c> for every subsequent
/// <c>AskQuestionAsync(question, conversationId)</c> call. This matches
/// <c>CopilotStudioRedTeamTarget.Validate</c>'s existing <c>--parallelism 1</c> gate (a live MCS session is
/// stateful/non-reentrant) — this class is not safe to call concurrently from multiple logical conversations, and
/// enforces that with an internal turn lock rather than relying solely on the caller's discipline.
/// </para>
/// <para>
/// <b>NOT independently live-verified.</b> The activity filtering/mapping logic below is unit-tested against a
/// fake <see cref="ICopilotStudioConversationClient"/> (see <c>CopilotStudioChatClientTests</c>), which proves the
/// bridge's shape is correct for the documented API contract. It has not been exercised against a real Copilot
/// Studio agent's actual wire traffic — see the CHANGELOG entry for what a pre-production smoke test must still
/// confirm (e.g. whether a real agent emits non-message activities on <c>StartConversationAsync</c> that should
/// also be surfaced, and the real shape of multi-activity turns).
/// </para>
/// </remarks>
internal sealed class CopilotStudioChatClient : IChatClient
{
    // Bounded wait for Dispose() to coordinate with an in-flight call still holding _turnLock (see Dispose's
    // own remarks) — long enough that a normally-slow network call finishes and releases cleanly, short
    // enough that a genuinely hung call cannot hang disposal forever.
    private static readonly TimeSpan DisposeLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ICopilotStudioConversationClient _client;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private string? _conversationId;
    private bool _started;
    private bool _disposed;

    public CopilotStudioChatClient(ICopilotStudioConversationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Synchronous validating wrapper — <c>async IAsyncEnumerable</c> methods do not run ANY of their body
    /// (including simple argument-validation lines at the top) until the caller's first
    /// <c>MoveNextAsync()</c>, so putting <see cref="ArgumentNullException"/>/<see cref="ObjectDisposedException"/>
    /// checks directly in an iterator method makes them surface during enumeration instead of at the call
    /// site — surprising and easy to miss (e.g. a caller that only ever calls <c>GetResponseAsync</c>, which
    /// awaits the enumerable, would still observe this correctly, but any caller that captures the
    /// <see cref="IAsyncEnumerable{T}"/> without enumerating it immediately would not see the error until
    /// much later, if at all). Validating here — before the real work moves into
    /// <see cref="GetStreamingResponseCoreAsync"/> — makes these throw eagerly, at the call site, like any
    /// other precondition check.
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var question = LastUserText(messages)
            ?? throw new InvalidOperationException(
                "CopilotStudioChatClient requires at least one user-role message with text to ask Copilot Studio.");

        return GetStreamingResponseCoreAsync(question, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseCoreAsync(
        string question,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A caller-supplied ChatOptions.ConversationId (the MEAI "stateful client" convention — see
        // https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai#stateless-vs-stateful-clients) takes
        // precedence over this instance's own memoized id: it means the caller is deliberately resuming a specific
        // server-side conversation rather than continuing whatever this instance last tracked.
        if (!string.IsNullOrEmpty(options?.ConversationId))
        {
            _conversationId = options.ConversationId;
            _started = true;
        }

        // Materialize the WHOLE turn (start, if needed, plus the ask) into a list while holding _turnLock,
        // then release the lock BEFORE yielding anything. Two independent reasons this matters:
        //   1. Retry safety (#1/#2 below) needs to see each network call's activities/failure as one unit.
        //   2. The lock must never be held across a `yield return` — a caller that doesn't fully enumerate
        //      the sequence (any path other than `await foreach`/`await using`, e.g. dropping the enumerator
        //      after a partial `MoveNextAsync` loop) would otherwise never reach the `finally` that releases
        //      it, wedging every future call on this instance forever. Yielding only AFTER the lock is
        //      released means an abandoned enumerator can, at worst, leave the already-fetched updates
        //      undelivered — it can no longer starve unrelated calls.
        var materialized = new List<IActivity>();
        string? turnConversationId;

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                // Idempotency-safety gate (P6 item B, hardened): a live Copilot Studio agent can execute real
                // side effects mid-turn. Only retry StartConversationAsync when ZERO activities were received
                // before the failure — if even one arrived, the conversation may already exist server-side,
                // and retrying would start a SECOND one, orphaning the first. See
                // CopilotStudioRetryPolicy.ExecuteIdempotentAsync and CopilotStudioPartialTurnException.
                var startActivities = await CopilotStudioRetryPolicy.ExecuteIdempotentAsync(
                    ct => DrainAsync(_client.StartConversationAsync(emitStartConversationEvent: false, cancellationToken: ct), ct),
                    cancellationToken).ConfigureAwait(false);

                foreach (var startActivity in startActivities)
                {
                    TrackConversationId(startActivity); // must happen before AskQuestionAsync reads _conversationId below
                }

                materialized.AddRange(startActivities);
                _started = true;
            }

            // Same idempotency-safety gate as Start: a 429 after ≥1 activity already streamed back means a
            // real answer may already have been produced server-side — resending the identical question
            // risks a duplicate real-world action, so this does NOT retry in that case (see
            // CopilotStudioPartialTurnException, thrown instead).
            var askActivities = await CopilotStudioRetryPolicy.ExecuteIdempotentAsync(
                ct => DrainAsync(_client.AskQuestionAsync(question, _conversationId, ct), ct),
                cancellationToken).ConfigureAwait(false);

            foreach (var activity in askActivities)
            {
                TrackConversationId(activity);
            }

            materialized.AddRange(askActivities);
            turnConversationId = _conversationId;
        }
        finally
        {
            ReleaseTurnLockSafely();
        }

        // Snapshot the conversation id INSIDE the lock (captured above, before release) rather than reading
        // the live _conversationId field from here on — once the lock is released, a second call on this
        // same instance (a caller violating the documented single-conversation-at-a-time contract) could
        // already be mutating _conversationId concurrently while this turn is still yielding. AsUpdates is
        // static and takes the snapshot explicitly for exactly this reason: it must stamp THIS turn's
        // updates with THIS turn's id, never a later turn's.
        foreach (var activity in materialized)
        {
            foreach (var update in AsUpdates(activity, turnConversationId))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// Materializes an activity stream into a list, capturing whatever was ALREADY received alongside a
    /// mid-stream failure (if any) instead of discarding it — <see cref="CopilotStudioRetryPolicy.ExecuteIdempotentAsync{T}"/>
    /// depends on the partial count to decide whether retrying is safe. Never throws except for an explicit
    /// caller-cancellation (<paramref name="ct"/> itself firing), which is never a "failure to retry" — it
    /// propagates immediately, matching <see cref="AgentEval.Core.RetryPolicy.ExecuteAsync{T}(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{T}},System.Threading.CancellationToken)"/>'s
    /// own convention.
    /// </summary>
    private static async Task<(IReadOnlyList<IActivity> Partial, Exception? Failure)> DrainAsync(IAsyncEnumerable<IActivity> source, CancellationToken ct)
    {
        var list = new List<IActivity>();
        try
        {
            await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            {
                list.Add(item);
            }

            return (list, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // explicit cancellation — never a retry candidate, propagate immediately
        }
        catch (Exception ex)
        {
            return (list, ex);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <summary>
    /// Coordinates with any in-flight call still holding <see cref="_turnLock"/> instead of disposing the
    /// semaphore out from under it. Without this, an in-flight call's own <c>finally</c> release
    /// (<see cref="ReleaseTurnLockSafely"/>) could race a concurrent <see cref="Dispose"/> and — pre-fix —
    /// throw <see cref="ObjectDisposedException"/> there, masking that call's REAL result or exception. The
    /// wait is bounded (<see cref="DisposeLockTimeout"/>): a hung network call must not hang disposal
    /// forever, so past the timeout this proceeds to dispose anyway (the residual race is caught and
    /// suppressed by <see cref="ReleaseTurnLockSafely"/>, which never lets that masking exception surface —
    /// the in-flight call's own real outcome, whatever it turns out to be, is what the caller sees either way).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _turnLock.Wait(DisposeLockTimeout);
        }
        catch (ObjectDisposedException)
        {
            return; // a concurrent Dispose() call already won this race — nothing left to do
        }

        if (_disposed)
        {
            return; // lost a race with a concurrent Dispose() while waiting for the lock
        }

        _disposed = true;
        _turnLock.Dispose();
        if (_client is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }
    }

    /// <summary>
    /// Releases <see cref="_turnLock"/>, suppressing <see cref="ObjectDisposedException"/> specifically —
    /// the one exception a concurrent <see cref="Dispose"/> winning the race could produce here, and the one
    /// that must never be allowed to replace/mask this call's own real result or exception (see
    /// <see cref="Dispose"/>'s remarks).
    /// </summary>
    private void ReleaseTurnLockSafely()
    {
        try
        {
            _turnLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Static and takes <paramref name="conversationId"/> explicitly (rather than reading the instance's
    /// mutable <see cref="_conversationId"/> field) — see the yield loop in
    /// <see cref="GetStreamingResponseCoreAsync"/> for why: this runs AFTER <see cref="_turnLock"/> is
    /// released, when a second call on this same instance could already be mutating that field concurrently.
    /// </summary>
    private static IEnumerable<ChatResponseUpdate> AsUpdates(IActivity activity, string? conversationId)
    {
        if (!string.Equals(activity.Type, ActivityTypes.Message, StringComparison.Ordinal))
        {
            yield break; // fidelity ceiling: non-message activities (typing, events, adaptive cards) are dropped, not fabricated as text.
        }

        if (string.IsNullOrEmpty(activity.Text))
        {
            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, activity.Text) { ConversationId = conversationId };
    }

    private void TrackConversationId(IActivity activity)
    {
        var id = activity.Conversation?.Id;
        if (!string.IsNullOrEmpty(id))
        {
            _conversationId = id;
        }
    }

    private static string? LastUserText(IEnumerable<ChatMessage> messages)
    {
        ChatMessage? last = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                last = message;
            }
        }

        return string.IsNullOrEmpty(last?.Text) ? null : last.Text;
    }
}
