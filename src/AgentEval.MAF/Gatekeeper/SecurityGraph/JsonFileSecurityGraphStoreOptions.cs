// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Construction-time retention, capacity, and bootstrap policy for a JSON security-graph store.</summary>
public sealed class JsonFileSecurityGraphStoreOptions
{
    /// <summary>The default maximum durable store size: 16 MiB.</summary>
    public const long DefaultMaxFileBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Whether construction may create a missing parent directory and empty version-1 store.
    /// Defaults to false so missing durable state cannot silently appear complete.
    /// </summary>
    public bool BootstrapIfMissing { get; init; }

    /// <summary>Rolling retention. Defaults to 30 days.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Maximum retained observations. Defaults to 100,000.</summary>
    public int MaxObservations { get; init; } = 100_000;

    /// <summary>Maximum retained coverage-gap markers. Defaults to 4096.</summary>
    public int MaxCoverageGaps { get; init; } = 4096;

    /// <summary>Maximum serialized file size. Defaults to 16 MiB.</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>Maximum JSON nesting depth. Defaults to 8.</summary>
    public int MaxJsonDepth { get; init; } = 8;
}
