// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Construction-time bounds and bootstrap policy for <see cref="JsonFileContainmentStore"/>.</summary>
public sealed class JsonFileContainmentStoreOptions
{
    /// <summary>The default maximum durable store size: 4 MiB.</summary>
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Whether construction may create a missing parent directory and an empty version-1 store.
    /// Defaults to false so a missing durable store cannot silently look clean.
    /// </summary>
    public bool BootstrapIfMissing { get; init; }

    /// <summary>The maximum number of target records. Defaults to 4096.</summary>
    public int MaxRecords { get; init; } = 4096;

    /// <summary>The maximum number of unexpired consumed release nonces. Defaults to 4096.</summary>
    public int MaxLiveReleaseNonces { get; init; } = 4096;

    /// <summary>The maximum serialized file size in bytes. Defaults to 4 MiB.</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;
}
