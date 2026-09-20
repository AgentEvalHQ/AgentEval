// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>Why a decision call failed, classified so a caller can decide whether a retry is honest.</summary>
public enum DecisionFailureKind
{
    /// <summary>The provider could not classify the failure, or the failure is local (network, cancellation wrapper).</summary>
    Unknown = 0,

    /// <summary>Missing or rejected API key (HTTP 401 / 403). Never transient.</summary>
    Authentication,

    /// <summary>The request body failed the provider's validation (HTTP 400 / 422). Never transient — the same body will fail again.</summary>
    InvalidRequest,

    /// <summary>The provider rate-limited the caller (HTTP 429). Transient; back off before retrying.</summary>
    RateLimited,

    /// <summary>The provider is temporarily overloaded (HTTP 529). Transient.</summary>
    Overloaded,

    /// <summary>The provider failed on its side (HTTP 5xx). Transient.</summary>
    ProviderUnavailable,

    /// <summary>The provider answered 2xx with a body this client could not accept: missing answer, wrong answer type, probability outside [0, 1], unparseable JSON. Never transient, and never a score.</summary>
    InvalidResponse,
}

/// <summary>
/// Thrown by an <see cref="IDecisionClient"/> when a call did not yield a usable decision. Carries the
/// classification and the HTTP status so the caller can tell a retryable condition from a permanent
/// one; the message never contains the API key.
/// </summary>
public sealed class DecisionClientException : Exception
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="kind">The failure classification.</param>
    /// <param name="message">A message safe to log — never the API key, and never more than a bounded excerpt of a provider body.</param>
    /// <param name="statusCode">The HTTP status the provider returned, when one was.</param>
    /// <param name="innerException">The underlying exception, when one caused this.</param>
    public DecisionClientException(DecisionFailureKind kind, string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>Why the call failed.</summary>
    public DecisionFailureKind Kind { get; }

    /// <summary>The HTTP status the provider returned, or <see langword="null"/> when the failure happened before a response.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Whether a retry with the same request could honestly succeed. True for rate limiting, overload
    /// and provider-side errors; false for authentication, request validation and unusable responses.
    /// Any retry must remain observable in provenance — a silent retry distorts latency and cost.
    /// </summary>
    public bool IsTransient => Kind is DecisionFailureKind.RateLimited or DecisionFailureKind.Overloaded or DecisionFailureKind.ProviderUnavailable;
}
