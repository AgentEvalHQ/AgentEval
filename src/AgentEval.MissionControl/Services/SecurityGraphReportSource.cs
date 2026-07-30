// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;

namespace AgentEval.MissionControl.Services;

/// <summary>
/// Read-only Mission Control source for one already-computed or on-demand security graph report.
/// Implementations must not expose a graph mutation surface.
/// </summary>
public interface ISecurityGraphReportSource
{
    /// <summary>Returns the current report with bounded, I/O-free work, or <see langword="null"/> when this host is not configured.</summary>
    SecurityGraphReport? Read();
}

/// <summary>
/// Computes an honest report directly from a caller-owned, in-memory graph reader. The source does not own or
/// dispose the reader.
/// </summary>
public sealed class SecurityGraphStoreReportSource : ISecurityGraphReportSource
{
    private const int MaximumExpectedNodes = 4096;
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(365);
    private readonly string _tenant;
    private readonly ISecurityGraphReader _reader;
    private readonly SecurityGraphNode[] _expectedNodes;
    private readonly TimeSpan _window;
    private readonly TimeSpan _staleAfter;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a fixed-tenant, fixed-window report source.</summary>
    public SecurityGraphStoreReportSource(
        string tenant,
        ISecurityGraphReader reader,
        IReadOnlyCollection<SecurityGraphNode> expectedNodes,
        TimeSpan window,
        TimeSpan staleAfter,
        TimeProvider? timeProvider = null)
    {
        _tenant = new ContainmentTarget.TenantScope(tenant).Tenant;
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        ArgumentNullException.ThrowIfNull(expectedNodes);
        if (expectedNodes.Count is < 1 or > MaximumExpectedNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedNodes));
        }

        var unique = new HashSet<SecurityGraphNode>();
        foreach (var node in expectedNodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!unique.Add(node))
            {
                throw new ArgumentException(
                    "Expected security graph nodes must be unique.",
                    nameof(expectedNodes));
            }
        }

        if (window <= TimeSpan.Zero || window > MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        if (staleAfter <= TimeSpan.Zero || staleAfter > window)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        _expectedNodes = [.. unique];
        _window = window;
        _staleAfter = staleAfter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SecurityGraphReport Read()
    {
        var snapshot = _reader.Read(_window) ??
            throw new InvalidOperationException(
                "Security graph reader returned no snapshot.");
        if (!string.Equals(snapshot.Tenant, _tenant, StringComparison.Ordinal) ||
            snapshot.Window != _window)
        {
            throw new InvalidOperationException(
                "Security graph reader violated its fixed source contract.");
        }

        return AgenticSecurityGraph.Compute(
            snapshot,
            _expectedNodes,
            _staleAfter,
            _timeProvider);
    }
}

/// <summary>Default source used until a deployment explicitly supplies a read-only graph source.</summary>
internal sealed class UnconfiguredSecurityGraphReportSource : ISecurityGraphReportSource
{
    /// <inheritdoc />
    public SecurityGraphReport? Read() => null;
}
