// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Ingestion;

/// <summary>Copies streamed files into isolated, bounded staging. It does not verify or publish evidence.</summary>
public interface IAefRunStager
{
    /// <summary>Consumes each file stream before requesting the next. The caller owns input streams.</summary>
    Task<AefStagedRun> StageAsync(IAsyncEnumerable<AefUploadFile> files, CancellationToken cancellationToken = default);
}

/// <summary>A relative AEF path and its streamed bytes. No local source path is accepted.</summary>
public sealed record AefUploadFile(string Path, Stream Content);

/// <summary>A completed upload, still unverified and not query-visible. Directory is local-only.</summary>
public sealed record AefStagedRun(string Id, string Directory, int FileCount, long Bytes);

/// <summary>Resource bounds for streamed staging, independent of protocol validation.</summary>
public sealed class AefStagingOptions
{
    /// <summary>Maximum files per stage, 1..100000. Default 1000.</summary>
    public int MaxFiles { get; init; } = 1_000;
    /// <summary>Maximum aggregate payload bytes, 1..1 GiB. Default 64 MiB.</summary>
    public long MaxBytes { get; init; } = 64L << 20;
    /// <summary>Maximum bytes per file, 1..MaxBytes. Default 64 MiB.</summary>
    public long MaxFileBytes { get; init; } = 64L << 20;
    /// <summary>Maximum concurrent uploads, 1..16. Default 2; excess calls fail without reserving a stage.</summary>
    public int MaxConcurrentUploads { get; init; } = 2;

    internal void Validate()
    {
        if (MaxFiles is < 1 or > 100_000 || MaxBytes is < 1 or > (1L << 30)
            || MaxFileBytes < 1 || MaxFileBytes > MaxBytes || MaxConcurrentUploads is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(AefStagingOptions), "Expected: files 1..100000, bytes 1..1GiB, " +
                "file bytes 1..total bytes, concurrency 1..16. Actual: invalid staging bounds. " +
                "Suggestions: configure bounded limits. Because: streamed ingestion must have finite resource use.");
    }
}

/// <summary>A structured refusal of an upload. Messages do not include uploaded contents or local paths.</summary>
public sealed class AefStagingException : IOException
{
    /// <summary>Creates a refusal with a stable code: busy, limit, path, conflict, or empty.</summary>
    public AefStagingException(string code, string actual) : base(
        $"Expected: a bounded upload with unique valid AEF paths. Actual: {actual}. " +
        "Suggestions: correct the upload or retry after capacity is available. Because: staging never overwrites evidence.") => Code = code;

    /// <summary>Stable refusal code.</summary>
    public string Code { get; }
}
