// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper.Egress;

/// <summary>
/// Thrown by <see cref="GatekeeperHttpMessageHandler"/> when a request (the original, or a redirect hop) is
/// blocked — a non-allow-listed host, a DNS resolution that failed/timed out, a resolved address that is
/// private/reserved, or a redirect chain exceeding <see cref="GatekeeperHttpEgressOptions.MaxRedirects"/>.
/// This is the network-layer counterpart to a tool gate's <c>Block</c> verdict: since this handler sits
/// underneath whatever <see cref="System.Net.Http.HttpClient"/> a tool's own implementation uses (not at the
/// MAF function-invocation seam), the natural, idiomatic "this call cannot proceed" signal for an
/// <see cref="System.Net.Http.HttpMessageHandler"/> is a thrown exception — mirroring how
/// <see cref="System.Net.Http.HttpClient"/> itself already throws on a DNS failure or a connection refusal, not a
/// sentinel return value a caller could accidentally ignore.
/// </summary>
public sealed class HttpEgressBlockedException : Exception
{
    /// <summary>The host that was blocked (the original request's host, or the redirect hop that failed).</summary>
    public string Host { get; }

    /// <summary>Creates the exception.</summary>
    public HttpEgressBlockedException(string host, string message)
        : base(message)
    {
        Host = host;
    }
}
