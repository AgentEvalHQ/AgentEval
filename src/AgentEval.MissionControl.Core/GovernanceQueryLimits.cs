// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MissionControl.Core;

/// <summary>Host-owned limits that may tighten, but never exceed, the shared governance query ceilings.</summary>
public sealed class GovernanceQueryLimits
{
    /// <summary>Maximum requested source IDs, including duplicates: 1..8.</summary>
    public int MaxSources { get; init; } = 8;
    /// <summary>Aggregate ordinary-file capture bytes: 1..64 MiB.</summary>
    public long MaxCaptureBytes { get; init; } = 64L << 20;
    /// <summary>Maximum result rows multiplied by catalogue requirements: 1..100000.</summary>
    public long MaxRowRulePairs { get; init; } = 100_000;
    /// <summary>Maximum serialized status report bytes: 1..2 MiB, excluding a transport envelope.</summary>
    public int MaxResponseBytes { get; init; } = 2 << 20;
    internal void Validate()
    {
        if (MaxSources is < 1 or > 8 || MaxCaptureBytes is < 1 or > (64L << 20) ||
            MaxRowRulePairs is < 1 or > 100_000 || MaxResponseBytes is < 1 or > (2 << 20))
            throw new ArgumentOutOfRangeException(nameof(GovernanceQueryLimits), "Expected: governance limits within shared ceilings. Actual: invalid limits. Suggestions: lower host limits within their documented ranges. Because: callers must not remove resource bounds.");
    }
}
