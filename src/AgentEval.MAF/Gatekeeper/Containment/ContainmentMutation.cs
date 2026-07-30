// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>The immutable input for an idempotent containment mutation.</summary>
public sealed record ContainmentRequest
{
    /// <summary>Creates a validated containment request.</summary>
    public ContainmentRequest(
        ContainmentTarget target,
        string reasonCode,
        string evidenceReference,
        string issuer)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
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
    }

    /// <summary>The boundary to contain.</summary>
    public ContainmentTarget Target { get; }

    /// <summary>A bounded reason-class token, never raw evidence.</summary>
    public string ReasonCode { get; }

    /// <summary>An opaque reference to operator-only evidence.</summary>
    public string EvidenceReference { get; }

    /// <summary>The normalized identity issuing containment.</summary>
    public string Issuer { get; }
}

/// <summary>The durable outcome class of a containment mutation.</summary>
public enum ContainmentMutationDisposition
{
    /// <summary>Durable state changed.</summary>
    Applied,

    /// <summary>An idempotent contain found the same target already active.</summary>
    Unchanged,

    /// <summary>A compare-and-swap release was stale, invalid, or replayed.</summary>
    Conflict,

    /// <summary>The durable outcome cannot be proven; subsequent reads must fail closed.</summary>
    Indeterminate,
}

/// <summary>An immutable containment mutation outcome carrying the store's resulting snapshot.</summary>
public sealed class ContainmentMutationResult
{
    private ContainmentMutationResult(
        ContainmentMutationDisposition disposition,
        ContainmentSnapshot snapshot)
    {
        Disposition = disposition;
        Snapshot = snapshot;
    }

    /// <summary>The mutation outcome class.</summary>
    public ContainmentMutationDisposition Disposition { get; }

    /// <summary>The store snapshot associated with the outcome.</summary>
    public ContainmentSnapshot Snapshot { get; }

    /// <summary>Creates a durable applied result.</summary>
    public static ContainmentMutationResult Applied(ContainmentSnapshot snapshot)
    {
        RequireRecordSnapshot(snapshot, nameof(snapshot));
        return new ContainmentMutationResult(ContainmentMutationDisposition.Applied, snapshot);
    }

    /// <summary>Creates an idempotent already-active result.</summary>
    public static ContainmentMutationResult Unchanged(ContainmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != ContainmentSnapshotState.Active)
        {
            throw new ArgumentException(
                "An unchanged containment result requires an active snapshot.",
                nameof(snapshot));
        }

        return new ContainmentMutationResult(ContainmentMutationDisposition.Unchanged, snapshot);
    }

    /// <summary>Creates a compare-and-swap conflict result.</summary>
    public static ContainmentMutationResult Conflict(ContainmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State == ContainmentSnapshotState.Indeterminate)
        {
            throw new ArgumentException(
                "A conflict result requires a determinate current snapshot.",
                nameof(snapshot));
        }

        return new ContainmentMutationResult(ContainmentMutationDisposition.Conflict, snapshot);
    }

    /// <summary>Creates an indeterminate mutation result.</summary>
    public static ContainmentMutationResult Indeterminate(ContainmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != ContainmentSnapshotState.Indeterminate)
        {
            throw new ArgumentException(
                "An indeterminate mutation result requires an indeterminate snapshot.",
                nameof(snapshot));
        }

        return new ContainmentMutationResult(ContainmentMutationDisposition.Indeterminate, snapshot);
    }

    private static void RequireRecordSnapshot(ContainmentSnapshot snapshot, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(snapshot, parameterName);
        if (snapshot.State is not (ContainmentSnapshotState.Active or ContainmentSnapshotState.Released))
        {
            throw new ArgumentException(
                "An applied mutation result requires an active or released record snapshot.",
                parameterName);
        }
    }
}
