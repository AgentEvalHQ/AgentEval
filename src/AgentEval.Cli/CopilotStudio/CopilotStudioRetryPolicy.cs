// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using AgentEval.Core;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// P6 item B (<c>strategy/CopilotStudio/Copilot-Studio-P6-Connector-Health-and-Resilience-Design.md</c> §1B):
/// 429-specific retry for <see cref="CopilotStudioChatClient"/>'s network calls. This is NOT a new retry
/// engine — <see cref="AgentEval.Core.RetryPolicy"/> already exists and is reused as-is; this class is only
/// the 429-classification predicate plus one pre-configured instance.
/// </summary>
/// <remarks>
/// <b>Exception-shape assumption, not yet live-verified:</b> the real
/// <c>Microsoft.Agents.CopilotStudio.Client.CopilotClient</c> is built on an <c>IHttpClientFactory</c>
/// (confirmed against the decompiled/reflected assembly — see <c>CopilotStudioAgentFactory</c>'s remarks),
/// so a transport-level 429 is assumed to surface the standard modern-.NET shape:
/// <see cref="HttpRequestException"/> with <see cref="HttpRequestException.StatusCode"/> ==
/// <see cref="HttpStatusCode.TooManyRequests"/>. This has been exercised against
/// <c>MockCopilotStudioConversationClient</c>'s existing rate-limit injection (built in Stage 4, unused for
/// this purpose until now) — not against a real Copilot Studio 429 response, which remains unavailable in
/// this environment (same "not independently live-verified" boundary as the rest of Track 1).
/// </remarks>
internal static class CopilotStudioRetryPolicy
{
    /// <summary>Retries only a 429-shaped <see cref="HttpRequestException"/> — a permanent failure (e.g. 401 auth) fails immediately.</summary>
    public static bool IsRetryable(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };

    /// <summary>A <see cref="RetryPolicy"/> pre-configured with <see cref="IsRetryable"/> as its <see cref="RetryPolicy.ShouldRetry"/> predicate.</summary>
    public static RetryPolicy Default { get; } = new() { ShouldRetry = IsRetryable };
}
