// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// The cancellation sources of one turn: the caller's token, the abandonment token and the optional turn deadline,
/// linked into <see cref="Token"/>. The deadline can move while a device-code sign-in runs.
/// </summary>
internal sealed class TurnTokens : IDisposable
{
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource? _deadline;
    private readonly object _deadlineGate = new();

    private TurnTokens(CancellationTokenSource linked, CancellationTokenSource? deadline, CancellationToken abandon, CancellationToken caller, DateTimeOffset? deadlineDue)
    {
        _linked = linked;
        _deadline = deadline;
        Abandon = abandon;
        Caller = caller;
        DeadlineDue = deadlineDue;
    }

    /// <summary>The linked token every call and wait of the turn runs under.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>The abandonment token of the conversation when the turn started.</summary>
    public CancellationToken Abandon { get; }

    /// <summary>The caller's own token, kept apart from <see cref="Token"/> so a local stop is attributed to the caller and not to the budget.</summary>
    public CancellationToken Caller { get; }

    /// <summary>Whether the turn deadline fired.</summary>
    public bool DeadlineFired => _deadline?.IsCancellationRequested == true;

    /// <summary>When the deadline fires (clock time); null when the turn has none.</summary>
    public DateTimeOffset? DeadlineDue { get; private set; }

    /// <summary>
    /// Links the caller's token and the abandonment token. A positive <paramref name="turnDeadline"/> adds a deadline
    /// on <paramref name="clock"/>; zero or less means the turn has no deadline.
    /// </summary>
    /// <param name="caller">The caller's token.</param>
    /// <param name="abandon">The conversation's abandonment token.</param>
    /// <param name="turnDeadline">The turn deadline.</param>
    /// <param name="clock">The clock the deadline runs on.</param>
    public static TurnTokens Create(CancellationToken caller, CancellationToken abandon, TimeSpan turnDeadline, TimeProvider clock)
    {
        var deadline = turnDeadline > TimeSpan.Zero ? new CancellationTokenSource(turnDeadline, clock) : null;
        var linked = deadline is null
            ? CancellationTokenSource.CreateLinkedTokenSource(caller, abandon)
            : CancellationTokenSource.CreateLinkedTokenSource(caller, abandon, deadline.Token);
        return new TurnTokens(linked, deadline, abandon, caller, deadline is null ? null : clock.GetUtcNow() + turnDeadline);
    }

    /// <summary>
    /// Moves the deadline to <paramref name="until"/>. With <paramref name="extendOnly"/>, it moves only when that is
    /// later than the current deadline. Returns false when the turn has no deadline, it already fired, nothing changed,
    /// or the turn is over.
    /// </summary>
    /// <param name="until">The new deadline.</param>
    /// <param name="now">The current clock time.</param>
    /// <param name="extendOnly">Whether the deadline may only move later.</param>
    public bool MoveDeadline(DateTimeOffset until, DateTimeOffset now, bool extendOnly)
    {
        lock (_deadlineGate)
        {
            if (_deadline is null || _deadline.IsCancellationRequested || DeadlineDue is not { } due || (extendOnly && until <= due))
            {
                return false;
            }

            try
            {
                var delay = until - now;
                _deadline.CancelAfter(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
                DeadlineDue = until;
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _deadline?.Dispose();
    }
}
