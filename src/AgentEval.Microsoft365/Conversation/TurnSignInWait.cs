// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// The sign-in listener of one turn (blocking, streamed or a subscription). When a device-code sign-in starts inside
/// the turn, the turn deadline moves to the code's expiry plus <see cref="WorkIqSignInWait.Grace"/>, when that is
/// later. The person then has the code's whole lifetime. When the sign-in ends, the deadline restarts at
/// <see cref="WorkIqOptions.TurnDeadline"/> for the network part of the turn. Both moves are traced
/// (<c>turn.sign_in_wait</c>, <c>turn.sign_in_done</c>). A turn without a deadline, or whose deadline already fired, is
/// left alone.
/// </summary>
internal sealed class TurnSignInWait : IWorkIqSignInWaitListener
{
    private readonly TurnContext _ctx;
    private readonly TurnTokens _tokens;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _turnDeadline;
    private int _extended;

    public TurnSignInWait(TurnContext ctx, TurnTokens tokens, TimeProvider clock, TimeSpan turnDeadline)
    {
        _ctx = ctx;
        _tokens = tokens;
        _clock = clock;
        _turnDeadline = turnDeadline;
    }

    /// <summary>
    /// Makes a new listener of this turn the current flow's listener until the returned scope is disposed. The scope
    /// does not survive a <c>yield</c> of an async iterator, so a streamed turn enters it again after its stream.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="tokens">The turn's tokens, whose deadline moves.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="turnDeadline">The turn deadline (<see cref="WorkIqOptions.TurnDeadline"/>).</param>
    public static IDisposable Enter(TurnContext ctx, TurnTokens tokens, TimeProvider clock, TimeSpan turnDeadline) =>
        WorkIqSignInWait.Enter(new TurnSignInWait(ctx, tokens, clock, turnDeadline));

    public void SignInStarted(DateTimeOffset expiresOn)
    {
        var now = _clock.GetUtcNow();
        var until = expiresOn + WorkIqSignInWait.Grace;
        if (until <= now || !_tokens.MoveDeadline(until, now, extendOnly: true))
        {
            return;
        }

        Volatile.Write(ref _extended, 1);
        _ctx.Trace(
            WorkIqTraceStage.Auth,
            "turn.sign_in_wait",
            $"a device-code sign-in started during the turn: the turn now waits until the code expires at {expiresOn.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC " +
            $"(up to {(until - now).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s) instead of its {_turnDeadline} deadline, which bounds network calls only; nothing is sent before the sign-in finishes",
            WorkIqTraceLevel.Warning);
    }

    public void SignInEnded()
    {
        if (Interlocked.Exchange(ref _extended, 0) == 0)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        if (_tokens.MoveDeadline(now + _turnDeadline, now, extendOnly: false))
        {
            _ctx.Trace(
                WorkIqTraceStage.Auth,
                "turn.sign_in_done",
                $"the sign-in wait ended: the turn deadline restarts ({_turnDeadline}) for the network part of the turn");
        }
    }
}
