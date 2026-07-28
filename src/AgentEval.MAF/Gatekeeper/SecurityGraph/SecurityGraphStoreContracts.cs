// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Immutable tenant-bound graph store read.</summary>
public sealed class SecurityGraphTenantSnapshot
{
    private const int MaximumSnapshotObservations = 100_000;
    private const int MaximumSnapshotGaps = 4096;
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(365);
    private readonly IReadOnlyList<SecurityGraphObservation> _observations;
    private readonly IReadOnlyList<SecurityGraphCoverageGap> _coverageGaps;

    private SecurityGraphTenantSnapshot(
        string tenant,
        TimeSpan window,
        DateTimeOffset capturedAtUtc,
        SecurityGraphCoverageState coverage,
        IEnumerable<SecurityGraphObservation> observations,
        IEnumerable<SecurityGraphCoverageGap> coverageGaps,
        string? failureCode)
    {
        Tenant = SecurityGraphValidation.Identity(
            tenant,
            nameof(tenant),
            ContainmentValidation.MaxTenantLength);
        if (window <= TimeSpan.Zero || window > MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        Window = window;
        CapturedAtUtc = ContainmentValidation.Utc(
            capturedAtUtc,
            nameof(capturedAtUtc));
        if (!Enum.IsDefined(coverage))
        {
            throw new ArgumentOutOfRangeException(nameof(coverage));
        }

        Coverage = coverage;
        var observationArray = (observations ??
            throw new ArgumentNullException(nameof(observations))).ToArray();
        var gapArray = (coverageGaps ??
            throw new ArgumentNullException(nameof(coverageGaps))).ToArray();
        if (observationArray.Length > MaximumSnapshotObservations ||
            gapArray.Length > MaximumSnapshotGaps)
        {
            throw new ArgumentException(
                "Security graph snapshot capacity was exceeded.");
        }

        var cutoff = capturedAtUtc - window;
        if (observationArray.Any(observation =>
                observation is null ||
                observation.AcceptedAtUtc < cutoff ||
                observation.AcceptedAtUtc > capturedAtUtc) ||
            gapArray.Any(gap =>
                gap is null ||
                gap.AtUtc < cutoff ||
                gap.AtUtc > capturedAtUtc))
        {
            throw new ArgumentException(
                "Security graph snapshot entries must be non-null and inside the requested window.");
        }

        _observations = Array.AsReadOnly(observationArray);
        _coverageGaps = Array.AsReadOnly(gapArray);
        FailureCode = failureCode;
    }

    /// <summary>The fixed normalized tenant.</summary>
    public string Tenant { get; }

    /// <summary>The requested rolling window.</summary>
    public TimeSpan Window { get; }

    /// <summary>The store clock used to cut the snapshot.</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>Whether the window is complete, known incomplete, or indeterminate.</summary>
    public SecurityGraphCoverageState Coverage { get; }

    /// <summary>Accepted observations within <see cref="Window"/>.</summary>
    public IReadOnlyList<SecurityGraphObservation> Observations => _observations;

    /// <summary>Known gap markers intersecting <see cref="Window"/>.</summary>
    public IReadOnlyList<SecurityGraphCoverageGap> CoverageGaps => _coverageGaps;

    /// <summary>Bounded failure token for an indeterminate snapshot.</summary>
    public string? FailureCode { get; }

    /// <summary>Creates a complete or incomplete determinate snapshot.</summary>
    public static SecurityGraphTenantSnapshot Determinate(
        string tenant,
        TimeSpan window,
        DateTimeOffset capturedAtUtc,
        IEnumerable<SecurityGraphObservation> observations,
        IEnumerable<SecurityGraphCoverageGap>? coverageGaps = null)
    {
        var gaps = coverageGaps?.ToArray() ?? [];
        return new SecurityGraphTenantSnapshot(
            tenant,
            window,
            capturedAtUtc,
            gaps.Length == 0
                ? SecurityGraphCoverageState.Complete
                : SecurityGraphCoverageState.Incomplete,
            observations,
            gaps,
            failureCode: null);
    }

    /// <summary>Creates a fail-closed indeterminate snapshot.</summary>
    public static SecurityGraphTenantSnapshot Indeterminate(
        string tenant,
        TimeSpan window,
        DateTimeOffset capturedAtUtc,
        string failureCode)
        => new(
            tenant,
            window,
            capturedAtUtc,
            SecurityGraphCoverageState.Indeterminate,
            observations: [],
            coverageGaps: [],
            ContainmentValidation.Token(
                failureCode,
                nameof(failureCode),
                ContainmentValidation.MaxReasonCodeLength));
}

/// <summary>Durable graph mutation outcome.</summary>
public enum SecurityGraphMutationDisposition
{
    /// <summary>Durable state changed.</summary>
    Applied,

    /// <summary>An identical idempotent event already exists.</summary>
    Unchanged,

    /// <summary>The event ID exists with different content.</summary>
    Conflict,

    /// <summary>The request was rejected and a durable coverage gap was recorded.</summary>
    RejectedWithGap,

    /// <summary>The durable outcome or current store state cannot be proven.</summary>
    Indeterminate,
}

/// <summary>Content-free result of a graph mutation.</summary>
public sealed class SecurityGraphMutationResult
{
    /// <summary>Creates a validated mutation result.</summary>
    public SecurityGraphMutationResult(
        SecurityGraphMutationDisposition disposition,
        string reasonCode)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Disposition = disposition;
        ReasonCode = ContainmentValidation.Token(
            reasonCode,
            nameof(reasonCode),
            ContainmentValidation.MaxReasonCodeLength);
    }

    /// <summary>The durable outcome class.</summary>
    public SecurityGraphMutationDisposition Disposition { get; }

    /// <summary>Bounded content-free outcome reason.</summary>
    public string ReasonCode { get; }
}

/// <summary>Tenant-bound persistent security graph event store.</summary>
public interface ISecurityGraphStore : IDisposable
{
    /// <summary>Returns a bounded immutable in-memory view; implementations must not perform I/O.</summary>
    SecurityGraphTenantSnapshot Read(TimeSpan window);

    /// <summary>Idempotently appends one content-free observation.</summary>
    ValueTask<SecurityGraphMutationResult> AppendAsync(
        SecurityGraphObservationRequest observation,
        CancellationToken cancellationToken = default);

    /// <summary>Durably records that accepted observations have a known ingestion gap.</summary>
    ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
        SecurityGraphCoverageGap gap,
        CancellationToken cancellationToken = default);
}
