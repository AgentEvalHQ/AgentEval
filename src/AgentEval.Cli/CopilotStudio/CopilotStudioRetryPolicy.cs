// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Runtime.ExceptionServices;
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

    /// <summary>
    /// Retries a streaming call ONLY when it fails with ZERO partial results already received — the
    /// idempotency-safety fix for <see cref="CopilotStudioChatClient"/>'s non-retry-safe network calls
    /// (<c>StartConversationAsync</c>/<c>AskQuestionAsync</c>). A live Copilot Studio agent can execute real
    /// side effects; if a 429-shaped failure happens AFTER at least one activity has already streamed back,
    /// that activity is evidence a real server-side action may already have happened, so blindly retrying
    /// (which would resend the identical question) risks duplicating it. This method deliberately does NOT
    /// reuse the generic, blind-retry-the-whole-call <see cref="RetryPolicy.ExecuteAsync{T}(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{T}},System.Threading.CancellationToken)"/>
    /// for that reason — it needs to see the PARTIAL result of a failed attempt to make this call, which the
    /// generic engine (used by many unrelated, non-streaming callers) has no reason to expose. Reuses
    /// <see cref="Default"/>'s tuning knobs (<see cref="RetryPolicy.MaxRetries"/>,
    /// <see cref="RetryPolicy.InitialDelayMs"/>, <see cref="RetryPolicy.BackoffMultiplier"/>,
    /// <see cref="RetryPolicy.MaxDelayMs"/>) as the single source of truth for backoff timing, and
    /// <see cref="IsRetryable"/> for 429-classification — only the retry LOOP shape differs.
    /// </summary>
    /// <typeparam name="T">The partial-result element type (<c>IActivity</c> for this shim's two calls).</typeparam>
    /// <param name="attempt">
    /// One attempt: returns whatever partial results were received before a failure (empty on full success)
    /// alongside the failure itself (<see langword="null"/> on success). Must never throw except for an
    /// explicit <see cref="OperationCanceledException"/> — see <c>CopilotStudioChatClient.DrainAsync</c>.
    /// </param>
    /// <param name="cancellationToken">Propagated to every attempt and to the between-attempt backoff delay.</param>
    public static async Task<IReadOnlyList<T>> ExecuteIdempotentAsync<T>(
        Func<CancellationToken, Task<(IReadOnlyList<T> Partial, Exception? Failure)>> attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var policy = Default;
        var currentDelay = policy.InitialDelayMs;
        var exceptions = new List<Exception>();

        for (var attemptNumber = 0; attemptNumber <= policy.MaxRetries; attemptNumber++)
        {
            var (partial, failure) = await attempt(cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return partial;
            }

            if (partial.Count > 0)
            {
                // The safety gate: something already streamed back before the transport failed. Do NOT
                // retry regardless of whether the failure itself is 429-shaped/retryable — surface it
                // honestly instead of silently resending the identical question.
                throw new CopilotStudioPartialTurnException(partial.Count, failure);
            }

            if (!IsRetryable(failure))
            {
                ExceptionDispatchInfo.Capture(failure).Throw(); // preserve the original stack/throw site
            }

            exceptions.Add(failure);
            if (attemptNumber == policy.MaxRetries)
            {
                break; // no more retries
            }

            await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
            currentDelay = Math.Min((int)(currentDelay * policy.BackoffMultiplier), policy.MaxDelayMs);
        }

        throw new RetryExhaustedException($"Operation failed after {policy.MaxRetries + 1} attempt(s).", exceptions);
    }
}

/// <summary>
/// A Copilot Studio streaming call failed partway through — at least one activity was already received
/// before the transport failure, so <see cref="CopilotStudioRetryPolicy.ExecuteIdempotentAsync{T}"/>
/// deliberately did NOT retry it: a partial response may already reflect a real, executed server-side
/// action (this shim wraps a LIVE conversational agent, not a stateless completion endpoint), and blindly
/// resending the identical question risks duplicating that action. The caller sees this instead of a
/// silently-retried duplicate call — see <c>CopilotStudioChatClient</c>'s remarks on non-idempotent retries.
/// </summary>
public sealed class CopilotStudioPartialTurnException : Exception
{
    /// <summary>How many activities were received on this turn before the underlying transport failure.</summary>
    public int PartialActivityCount { get; }

    /// <summary>Creates a new <see cref="CopilotStudioPartialTurnException"/>.</summary>
    public CopilotStudioPartialTurnException(int partialActivityCount, Exception innerException)
        : base(
            $"Copilot Studio returned {partialActivityCount} activity(ies) on this turn before the transport " +
            "failed. Not retrying automatically: a partial response may already reflect a real server-side " +
            "action, and resending the same question could duplicate it. See the inner exception for the " +
            "underlying transport failure; confirm whether the action already completed before retrying by hand.",
            innerException)
    {
        PartialActivityCount = partialActivityCount;
    }
}
