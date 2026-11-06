// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Auth;

/// <summary>
/// The host-owned token seam: the host decides how a delegated Work IQ access token is acquired (MSAL device
/// code, WAM, on-behalf-of, a cache) and owns the cache and sign-in policy. The provider calls this on every
/// request, treats the token as opaque, never persists or decodes it, and never forwards it to a host outside
/// the trust policy. Application-only tokens are not a documented Work IQ contract.
/// </summary>
public interface IWorkIqTokenProvider
{
    /// <summary>Returns a current bearer access token for the configured scope (<see cref="Microsoft365Config.Scope"/>).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
