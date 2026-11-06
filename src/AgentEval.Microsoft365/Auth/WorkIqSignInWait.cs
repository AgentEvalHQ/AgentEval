// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Auth;

/// <summary>
/// Whoever waits on a token while an interactive sign-in may start (a turn or subscription of <see cref="WorkIqConversation"/>):
/// told when a device-code sign-in starts, with the instant its code expires, and when the sign-in attempt ends (with a
/// token or not). A turn uses it so its deadline, which bounds network calls, never cuts a person's sign-in short.
/// </summary>
internal interface IWorkIqSignInWaitListener
{
    /// <summary>An interactive sign-in started; its code expires at <paramref name="expiresOn"/>. Must not throw.</summary>
    /// <param name="expiresOn">When the sign-in code expires.</param>
    void SignInStarted(DateTimeOffset expiresOn);

    /// <summary>The sign-in attempt ended (a token, a failure or a stop); a no-op when no sign-in started. Must not throw.</summary>
    void SignInEnded();
}

/// <summary>
/// The ambient <see cref="IWorkIqSignInWaitListener"/> of the current asynchronous flow: a turn enters a scope around its
/// calls, and the token provider that starts a device-code sign-in inside them (<see cref="WorkIqDeviceCodeTokenProvider"/>)
/// reports to it. Flow-local (<see cref="AsyncLocal{T}"/>), so concurrent turns of different conversations never see each
/// other's listener; a provider captures <see cref="Current"/> when it is called, before any callback of MSAL runs.
/// </summary>
internal static class WorkIqSignInWait
{
    /// <summary>How long past the code's expiry a turn keeps waiting, so MSAL's own <c>code_expired</c> ends the sign-in first.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static readonly AsyncLocal<IWorkIqSignInWaitListener?> s_current = new();

    /// <summary>The listener of the current flow, or null when nobody waits on a sign-in here.</summary>
    public static IWorkIqSignInWaitListener? Current => s_current.Value;

    /// <summary>Makes <paramref name="listener"/> the current flow's listener until the returned scope is disposed.</summary>
    /// <param name="listener">The listener.</param>
    public static IDisposable Enter(IWorkIqSignInWaitListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var previous = s_current.Value;
        s_current.Value = listener;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly IWorkIqSignInWaitListener? _previous;
        private int _disposed;

        public Scope(IWorkIqSignInWaitListener? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                s_current.Value = _previous;
            }
        }
    }
}
