// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;

namespace AgentEval.Microsoft365.Capture;

/// <summary>
/// A <see cref="DelegatingHandler"/> that authenticates every A2A request: <c>Authorization: Bearer</c> from the
/// <see cref="IWorkIqTokenProvider"/> (only for destinations the trust policy allows), <c>A2A-Version: 1.0</c>
/// (card requests included — Work IQ defaults to 0.3 without it), a <c>User-Agent</c> naming this package, and
/// capture of the <c>request-id</c> response header for correlation. A 401 surfaces
/// <see cref="WorkIqFailureCategory.Unauthorized"/> with a device-code hint and is never resent. Owned by lane A (M11).
/// </summary>
internal sealed class WorkIqAuthHandler : DelegatingHandler
{
    /// <summary>The request header that pins the protocol version.</summary>
    public const string ProtocolVersionHeader = "A2A-Version";

    /// <summary>The only protocol version sent.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>The response header carrying the service's correlation id.</summary>
    public const string RequestIdHeader = "request-id";

    /// <summary>Creates the handler.</summary>
    /// <param name="tokenProvider">The host-owned token source.</param>
    public WorkIqAuthHandler(IWorkIqTokenProvider tokenProvider)
    {
        TokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    /// <summary>The host-owned token source.</summary>
    public IWorkIqTokenProvider TokenProvider { get; }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M11)");
}
