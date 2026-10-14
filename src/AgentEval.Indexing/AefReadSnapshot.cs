// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;
using AgentEval.Results.Runs;

namespace AgentEval.Indexing;

// One invocation owns the copied ordinary files. Provides a private copy for subsequent verification.
internal sealed class AefReadSnapshot(DirectoryInfo directory, long bytes) : IDisposable
{
    internal string FullName => directory.FullName;
    internal long Bytes { get; } = bytes;
    internal static AefReadSnapshot Capture(string directory, AefStagingOptions limits, CancellationToken cancellationToken)
        => Copy(directory, limits, cancellationToken);
    private static AefReadSnapshot Copy(string directory, AefStagingOptions limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new EvidenceIndexException("invalid-source-path");
        directory = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse) throw new EvidenceIndexException("unsupported-file-classifier");
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            if (AefFolder.KindOf(current) != AefEntryKind.Folder) throw new EvidenceIndexException("unsafe-source-path");
        DirectoryInfo? snapshot = null;
        try
        {
            var inventory = AefIngestionInventory.Scan(directory, limits, cancellationToken);
            snapshot = Directory.CreateTempSubdirectory("ae-index-snapshot-");
            var buffer = new byte[64 << 10];
            foreach (var (path, size) in inventory.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(snapshot.FullName, path.Replace('/', Path.DirectorySeparatorChar));
                if (AefFolder.KindOf(source) != AefEntryKind.File) throw new EvidenceIndexException("source-changed");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long copied = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size - copied + 1));
                    if (read == 0) break;
                    copied += read;
                    if (copied > size) throw new EvidenceIndexException("source-changed");
                    output.Write(buffer, 0, read);
                }
                if (copied != size) throw new EvidenceIndexException("source-changed");
            }
            return new(snapshot, inventory.Bytes);
        }
        catch
        {
            if (snapshot is not null) Directory.Delete(snapshot.FullName, recursive: true);
            throw;
        }
    }
    public void Dispose() => Directory.Delete(directory.FullName, recursive: true);
}
