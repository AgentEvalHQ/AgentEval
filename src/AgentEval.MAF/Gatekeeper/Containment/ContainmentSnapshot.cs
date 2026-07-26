// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>The explicit result of a bounded containment-store read.</summary>
public enum ContainmentSnapshotState
{
    /// <summary>No record exists for the target.</summary>
    NotContained,

    /// <summary>A valid active record exists and calls must be blocked.</summary>
    Active,

    /// <summary>A valid audited release exists.</summary>
    Released,

    /// <summary>The store cannot prove current state; enforcement must fail closed.</summary>
    Indeterminate,
}

/// <summary>
/// Immutable bounded snapshot returned to gate hot paths. Indeterminate is distinct from NotContained and is
/// blocking by construction.
/// </summary>
public sealed class ContainmentSnapshot
{
    private ContainmentSnapshot(
        ContainmentTarget target,
        ContainmentSnapshotState state,
        ContainmentRecord? record,
        string? failureCode)
    {
        Target = target;
        State = state;
        Record = record;
        FailureCode = failureCode;
    }

    /// <summary>The queried target.</summary>
    public ContainmentTarget Target { get; }

    /// <summary>The explicit read state.</summary>
    public ContainmentSnapshotState State { get; }

    /// <summary>The current immutable record for Active/Released states.</summary>
    public ContainmentRecord? Record { get; }

    /// <summary>A bounded secret-free failure code for Indeterminate state.</summary>
    public string? FailureCode { get; }

    /// <summary>Whether enforcement must block: true for Active and Indeterminate.</summary>
    public bool MustBlock => State is ContainmentSnapshotState.Active or ContainmentSnapshotState.Indeterminate;

    /// <summary>Creates a confirmed no-record snapshot.</summary>
    public static ContainmentSnapshot NotContained(ContainmentTarget target)
        => new(
            target ?? throw new ArgumentNullException(nameof(target)),
            ContainmentSnapshotState.NotContained,
            record: null,
            failureCode: null);

    /// <summary>Creates an Active or Released snapshot from a validated record.</summary>
    public static ContainmentSnapshot FromRecord(ContainmentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ContainmentSnapshot(
            record.Target,
            record.Status == ContainmentStatus.Active
                ? ContainmentSnapshotState.Active
                : ContainmentSnapshotState.Released,
            record,
            failureCode: null);
    }

    /// <summary>Creates a fail-closed snapshot when the store cannot prove current state.</summary>
    public static ContainmentSnapshot Indeterminate(ContainmentTarget target, string failureCode)
        => new(
            target ?? throw new ArgumentNullException(nameof(target)),
            ContainmentSnapshotState.Indeterminate,
            record: null,
            ContainmentValidation.Token(failureCode, nameof(failureCode), maxLength: 64));
}
