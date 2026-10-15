// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

/// <summary>Copies an explicitly selected local candidate into staging without publishing it.</summary>
public interface IAefLocalRunImporter
{
    /// <summary>Stages a source folder. HTTP callers must not be allowed to select arbitrary local roots.</summary>
    Task<AefStagedRun> StageDirectoryAsync(string sourceDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded local copying through the same streamed stager used by HTTP. Refuses links/special entries and
/// detects file-length changes. Source ownership must prevent hostile concurrent path replacement;
/// the copied evidence still requires protocol verification before publication.
/// </summary>
public sealed class AefLocalRunImporter : IAefLocalRunImporter
{
    private readonly IAefRunStager _stager;
    private readonly AefStagingOptions _limits;

    /// <summary>Creates a local importer with the same limits as its stager.</summary>
    public AefLocalRunImporter(IAefRunStager stager, AefStagingOptions? limits = null)
    {
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public Task<AefStagedRun> StageDirectoryAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Path.IsPathFullyQualified(sourceDirectory))
            throw new ArgumentException("Expected: an absolute local source. Actual: missing or relative path. " +
                "Suggestions: select an explicit discovered candidate. Because: local import must not infer filesystem scope.", nameof(sourceDirectory));
        sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        for (var current = sourceDirectory; current is not null; current = Path.GetDirectoryName(current))
            if (AefFolder.KindOf(current) != AefEntryKind.Folder)
                throw new AefStagingException("path", "linked or non-directory source ancestry");
        // Unlike discovery, copying opens files. On Unix do not rely on the BCL fallback that can call a pipe a file.
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse)
            throw new IOException("Local copying requires a working native regular-file classifier on this platform.");
        var inventory = AefIngestionInventory.Scan(sourceDirectory, _limits, cancellationToken);
        return _stager.StageAsync(ReadFiles(sourceDirectory, inventory.Files, cancellationToken), cancellationToken);
    }

    private static async IAsyncEnumerable<AefUploadFile> ReadFiles(string root, IReadOnlyList<(string Path, long Size)> files,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (AefFolder.KindOf(path) != AefEntryKind.File)
                throw new AefStagingException("path", "source entry changed type");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != file.Size) throw new IOException("Source length changed before copying.");
            yield return new(file.Path, input);
            if (input.Position != file.Size || input.Length != file.Size) throw new IOException("Source length changed while copying.");
        }
    }
}
