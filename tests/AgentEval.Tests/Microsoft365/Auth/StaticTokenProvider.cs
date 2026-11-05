// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;

namespace AgentEval.Tests.Microsoft365.Auth;

/// <summary>
/// A test <see cref="IWorkIqTokenProvider"/>: returns a fixed, JWT-looking token (so a leak into a trace or a
/// capture is detectable by its <c>eyJ</c> prefix), counts calls, and can be told to throw instead.
/// </summary>
internal sealed class StaticTokenProvider : IWorkIqTokenProvider
{
    /// <summary>A syntactically JWT-looking value that is not a real token.</summary>
    public const string DefaultToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJhcGk6Ly93b3JraXEuc3ZjLmNsb3VkLm1pY3Jvc29mdCIsInRpZCI6InRlbmFudC0xIn0.c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWduYXR1cmUtc2lnbmF0dXJl";

    private int _calls;

    public StaticTokenProvider(string token = DefaultToken)
    {
        Token = token;
    }

    /// <summary>The token handed out.</summary>
    public string Token { get; set; }

    /// <summary>When set, every call throws this instead of returning the token.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times a token was requested.</summary>
    public int Calls => Volatile.Read(ref _calls);

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
