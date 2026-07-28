// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>A durable node category in the tenant-bound security graph.</summary>
public enum SecurityGraphNodeKind
{
    /// <summary>An agent definition.</summary>
    Agent,

    /// <summary>A local tool.</summary>
    Tool,

    /// <summary>An explicitly identified MCP server.</summary>
    McpServer,

    /// <summary>A delegated or remote agent endpoint.</summary>
    AgentEndpoint,
}

/// <summary>A closed security observation kind.</summary>
public enum SecurityGraphSignalKind
{
    /// <summary>A call completed without an enforced Gatekeeper block.</summary>
    CallObserved,

    /// <summary>A call was blocked by enforced Gatekeeper policy.</summary>
    CallBlocked,

    /// <summary>Containment was durably applied.</summary>
    ContainmentApplied,

    /// <summary>Containment was durably released by authorized operator action.</summary>
    ContainmentReleased,
}

/// <summary>Completeness of the accepted observations in a tenant snapshot.</summary>
public enum SecurityGraphCoverageState
{
    /// <summary>No known ingestion gap intersects the requested window.</summary>
    Complete,

    /// <summary>At least one known ingestion gap intersects the requested window.</summary>
    Incomplete,

    /// <summary>The store cannot prove a valid current snapshot.</summary>
    Indeterminate,
}

/// <summary>Ordinal, bounded identity of one graph node inside a store's fixed tenant.</summary>
public sealed class SecurityGraphNode : IEquatable<SecurityGraphNode>
{
    /// <summary>Creates a validated node identity.</summary>
    public SecurityGraphNode(SecurityGraphNodeKind kind, string identifier)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Identifier = SecurityGraphValidation.Identity(
            identifier,
            nameof(identifier),
            ContainmentValidation.MaxIdentifierLength);
    }

    /// <summary>The closed node category.</summary>
    public SecurityGraphNodeKind Kind { get; }

    /// <summary>The normalized, tenant-local identifier.</summary>
    public string Identifier { get; }

    /// <inheritdoc />
    public bool Equals(SecurityGraphNode? other)
        => other is not null &&
           Kind == other.Kind &&
           string.Equals(Identifier, other.Identifier, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is SecurityGraphNode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            Kind,
            StringComparer.Ordinal.GetHashCode(Identifier));

    /// <summary>Value equality for graph nodes.</summary>
    public static bool operator ==(
        SecurityGraphNode? left,
        SecurityGraphNode? right)
        => ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

    /// <summary>Value inequality for graph nodes.</summary>
    public static bool operator !=(
        SecurityGraphNode? left,
        SecurityGraphNode? right)
        => !(left == right);

    /// <inheritdoc />
    public override string ToString() => $"SecurityGraphNode.{Kind}";
}

/// <summary>In-memory input for one idempotent graph append.</summary>
public sealed class SecurityGraphObservationRequest
{
    /// <summary>Creates a validated observation request.</summary>
    public SecurityGraphObservationRequest(
        string eventId,
        SecurityGraphNode? source,
        SecurityGraphNode destination,
        SecurityGraphSignalKind signal,
        string sessionIdentifier,
        string? evidenceReference = null)
    {
        EventId = ContainmentValidation.Token(
            eventId,
            nameof(eventId),
            ContainmentValidation.MaxNonceLength);
        Destination = destination ??
            throw new ArgumentNullException(nameof(destination));
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        if (signal is (SecurityGraphSignalKind.CallObserved or
                SecurityGraphSignalKind.CallBlocked) && source is null)
        {
            throw new ArgumentException(
                "A call observation requires a source node.",
                nameof(source));
        }

        if (signal is (SecurityGraphSignalKind.ContainmentApplied or
                SecurityGraphSignalKind.ContainmentReleased) && source is not null)
        {
            throw new ArgumentException(
                "A containment observation cannot have a call source.",
                nameof(source));
        }

        Source = source;
        Signal = signal;
        SessionIdentifier = SecurityGraphValidation.Identity(
            sessionIdentifier,
            nameof(sessionIdentifier),
            ContainmentValidation.MaxIdentifierLength);
        EvidenceReference = evidenceReference is null
            ? null
            : ContainmentValidation.Token(
                evidenceReference,
                nameof(evidenceReference),
                ContainmentValidation.MaxEvidenceReferenceLength);
    }

    /// <summary>Opaque idempotency identity.</summary>
    public string EventId { get; }

    /// <summary>Call origin; required for call signals.</summary>
    public SecurityGraphNode? Source { get; }

    /// <summary>The observed or containment-affected node.</summary>
    public SecurityGraphNode Destination { get; }

    /// <summary>The closed signal kind.</summary>
    public SecurityGraphSignalKind Signal { get; }

    /// <summary>Raw durable session identity. Stores must hash and never persist or return it.</summary>
    public string SessionIdentifier { get; }

    /// <summary>Optional opaque pointer to operator-only evidence.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Durable, content-free accepted graph observation.</summary>
public sealed class SecurityGraphObservation
{
    /// <summary>Creates a validated durable observation.</summary>
    public SecurityGraphObservation(
        string eventId,
        DateTimeOffset acceptedAtUtc,
        SecurityGraphNode? source,
        SecurityGraphNode destination,
        SecurityGraphSignalKind signal,
        string sessionDigest,
        string? evidenceReference = null)
    {
        EventId = ContainmentValidation.Token(
            eventId,
            nameof(eventId),
            ContainmentValidation.MaxNonceLength);
        AcceptedAtUtc = ContainmentValidation.Utc(
            acceptedAtUtc,
            nameof(acceptedAtUtc));
        Destination = destination ??
            throw new ArgumentNullException(nameof(destination));
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        if (signal is (SecurityGraphSignalKind.CallObserved or
                SecurityGraphSignalKind.CallBlocked) && source is null)
        {
            throw new ArgumentException(
                "A call observation requires a source node.",
                nameof(source));
        }

        if (signal is (SecurityGraphSignalKind.ContainmentApplied or
                SecurityGraphSignalKind.ContainmentReleased) && source is not null)
        {
            throw new ArgumentException(
                "A containment observation cannot have a call source.",
                nameof(source));
        }

        Source = source;
        Signal = signal;
        SessionDigest = SecurityGraphValidation.SessionDigest(
            sessionDigest,
            nameof(sessionDigest));
        EvidenceReference = evidenceReference is null
            ? null
            : ContainmentValidation.Token(
                evidenceReference,
                nameof(evidenceReference),
                ContainmentValidation.MaxEvidenceReferenceLength);
    }

    /// <summary>Opaque idempotency identity.</summary>
    public string EventId { get; }

    /// <summary>Store-generated acceptance time.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    /// <summary>Call origin; required for call signals.</summary>
    public SecurityGraphNode? Source { get; }

    /// <summary>The observed or containment-affected node.</summary>
    public SecurityGraphNode Destination { get; }

    /// <summary>The closed signal kind.</summary>
    public SecurityGraphSignalKind Signal { get; }

    /// <summary>Base64url HMAC-SHA-256 session digest.</summary>
    public string SessionDigest { get; }

    /// <summary>Optional opaque pointer to operator-only evidence.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Content-free durable marker that accepted graph coverage has a known gap.</summary>
public sealed class SecurityGraphCoverageGap
{
    /// <summary>Creates a caller request; the store replaces time with its authoritative clock.</summary>
    public SecurityGraphCoverageGap(string reasonCode, int count = 1)
    {
        ReasonCode = ContainmentValidation.Token(
            reasonCode,
            nameof(reasonCode),
            ContainmentValidation.MaxReasonCodeLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Count = count;
    }

    private SecurityGraphCoverageGap(
        DateTimeOffset atUtc,
        string reasonCode,
        int count)
        : this(reasonCode, count)
    {
        AtUtc = ContainmentValidation.Utc(atUtc, nameof(atUtc));
    }

    /// <summary>Creates a store-accepted durable gap marker.</summary>
    public static SecurityGraphCoverageGap Accepted(
        DateTimeOffset atUtc,
        string reasonCode,
        int count = 1)
        => new(atUtc, reasonCode, count);

    /// <summary>Store-generated gap time; default on a not-yet-accepted request.</summary>
    public DateTimeOffset AtUtc { get; }

    /// <summary>Bounded closed-by-convention reason token, never dropped content.</summary>
    public string ReasonCode { get; }

    /// <summary>Saturating count of coalesced gaps in the same reason/time bucket.</summary>
    public int Count { get; }
}
