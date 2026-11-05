// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// The host token source of the demo tests: hands out <see cref="Token"/> (the literal <c>fake-token</c>, so a leak into
/// the screen, a run-folder file or the zip is found by a plain substring search), counts calls, and throws
/// <see cref="Failure"/> instead when one is set (an MSAL sign-in failure such as AADSTS65001).
/// </summary>
internal sealed class FakeTokenProvider : IWorkIqTokenProvider
{
    /// <summary>The token handed out; it must never appear in any output of the demo.</summary>
    public const string Token = "fake-token";

    private int _calls;

    /// <summary>When set, every call throws this instead of returning <see cref="Token"/>.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times a token was requested.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc/>
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
        {
            throw Failure;
        }

        return new ValueTask<string>(Token);
    }
}
