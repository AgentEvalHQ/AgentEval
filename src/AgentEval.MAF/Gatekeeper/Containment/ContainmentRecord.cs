// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>The durable lifecycle state of a containment record.</summary>
public enum ContainmentStatus
{
    /// <summary>The target is contained.</summary>
    Active,

    /// <summary>An authorized compare-and-swap release completed.</summary>
    Released,
}

/// <summary>
/// Immutable version-1 containment record. It deliberately carries no raw prompt, argument, result, or secret
/// field; use the opaque evidence reference to resolve operator-only evidence.
/// </summary>
public sealed record ContainmentRecord
{
    /// <summary>The record schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates and validates an immutable containment record.</summary>
    public ContainmentRecord(
        ContainmentTarget target,
        ContainmentStatus status,
        DateTimeOffset containedAtUtc,
        DateTimeOffset? releasedAtUtc,
        string reasonCode,
        string evidenceReference,
        string issuer,
        string? reviewer,
        long version,
        string etag)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Containment status is undefined.");
        }

        Status = status;
        ContainedAtUtc = ContainmentValidation.Utc(containedAtUtc, nameof(containedAtUtc));
        ReleasedAtUtc = releasedAtUtc is null
            ? null
            : ContainmentValidation.Utc(releasedAtUtc.Value, nameof(releasedAtUtc));
        ReasonCode = ContainmentValidation.Token(
            reasonCode,
            nameof(reasonCode),
            ContainmentValidation.MaxReasonCodeLength);
        EvidenceReference = ContainmentValidation.Token(
            evidenceReference,
            nameof(evidenceReference),
            ContainmentValidation.MaxEvidenceReferenceLength);
        Issuer = ContainmentValidation.Identity(
            issuer,
            nameof(issuer),
            ContainmentValidation.MaxActorLength);
        Reviewer = reviewer is null
            ? null
            : ContainmentValidation.Identity(reviewer, nameof(reviewer), ContainmentValidation.MaxActorLength);

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Containment record version must be positive.");
        }

        Version = version;
        ETag = ContainmentValidation.Token(etag, nameof(etag), ContainmentValidation.MaxETagLength);

        if (status == ContainmentStatus.Active && (ReleasedAtUtc is not null || Reviewer is not null))
        {
            throw new ArgumentException(
                "An active containment record cannot carry release time or reviewer data.");
        }

        if (status == ContainmentStatus.Released)
        {
            if (ReleasedAtUtc is null || Reviewer is null)
            {
                throw new ArgumentException(
                    "A released containment record requires release time and reviewer data.");
            }

            if (ReleasedAtUtc < ContainedAtUtc)
            {
                throw new ArgumentException(
                    "A containment release time cannot precede its containment time.");
            }
        }
    }

    /// <summary>Always <see cref="CurrentSchemaVersion"/> for this type.</summary>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>The contained boundary.</summary>
    public ContainmentTarget Target { get; }

    /// <summary>The record lifecycle status.</summary>
    public ContainmentStatus Status { get; }

    /// <summary>When containment most recently became active, in UTC.</summary>
    public DateTimeOffset ContainedAtUtc { get; }

    /// <summary>When an audited release completed, in UTC; null while active.</summary>
    public DateTimeOffset? ReleasedAtUtc { get; }

    /// <summary>A bounded token identifying the containment reason class.</summary>
    public string ReasonCode { get; }

    /// <summary>An opaque reference to operator-only evidence.</summary>
    public string EvidenceReference { get; }

    /// <summary>The normalized identity that issued containment.</summary>
    public string Issuer { get; }

    /// <summary>The normalized operator identity that released containment.</summary>
    public string? Reviewer { get; }

    /// <summary>The positive monotonic record version used for compare-and-swap release.</summary>
    public long Version { get; }

    /// <summary>The bounded opaque entity tag for persistence-level optimistic concurrency.</summary>
    public string ETag { get; }
}
