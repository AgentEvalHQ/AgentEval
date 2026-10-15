// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Observations;

/// <summary>Finite per-request budgets for OTLP observations, independent of HTTP and AEF publication.</summary>
public sealed class OtlpIngestionLimits
{
    /// <summary>Maximum transport bytes, including compressed data. Default 4 MiB; range 1..64 MiB.</summary>
    public int MaxRequestBytes { get; init; } = 4 << 20;
    /// <summary>Maximum decoded bytes after decompression. Default 4 MiB; range 1..64 MiB.</summary>
    public int MaxDecodedBytes { get; init; } = 4 << 20;
    /// <summary>Maximum spans in a request. Default 4096; range 1..100000.</summary>
    public int MaxSpans { get; init; } = 4096;
    /// <summary>Maximum attributes on any resource, scope, span, event or link. Default 128; range 1..1024.</summary>
    public int MaxAttributes { get; init; } = 128;
    /// <summary>Maximum aggregate decoded collection items per request. Default 100000; range 1..1000000.</summary>
    public int MaxItems { get; init; } = 100_000;
    /// <summary>Maximum nested message/value depth. Default 32; range 8..64.</summary>
    public int MaxDepth { get; init; } = 32;
    /// <summary>Maximum UTF-8 bytes in a decoded string or bytes value. Default 16 KiB; range 1..1 MiB.</summary>
    public int MaxValueBytes { get; init; } = 16 << 10;
    /// <summary>Maximum active requests per receiver instance. Default 2; range 1..16.</summary>
    public int MaxConcurrentRequests { get; init; } = 2;
    /// <summary>Request timeout in seconds. Default 30; range 1..600. Synchronous phases observe it between operations.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Rejects invalid limits before a receiver reads input or reserves storage.</summary>
    public void Validate()
    {
        if (MaxRequestBytes is < 1 or > (64 << 20) || MaxDecodedBytes is < 1 or > (64 << 20)
            || MaxSpans is < 1 or > 100_000 || MaxAttributes is < 1 or > 1024
            || MaxItems is < 1 or > 1_000_000 || MaxDepth is < 8 or > 64
            || MaxValueBytes is < 1 or > (1 << 20) || MaxConcurrentRequests is < 1 or > 16
            || TimeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(OtlpIngestionLimits),
                "Expected: finite OTLP byte, item, depth, concurrency and timeout limits within documented ranges. " +
                "Actual: invalid configuration. Suggestions: use the documented defaults or valid bounded overrides. " +
                "Because: observations must not create unbounded memory or storage work.");
    }
}

/// <summary>A path-free OTLP refusal. Code is limit, encoding, malformed, busy or storage.</summary>
public sealed class OtlpIngestionException(string code, string actual) : IOException(
    $"Expected: a supported bounded OTLP request. Actual: {actual}. " +
    "Suggestions: correct the request or retry after capacity is available. Because: telemetry ingestion must preserve resource limits.")
{
    /// <summary>Stable refusal code for transport adapters.</summary>
    public string Code { get; } = code;
}
