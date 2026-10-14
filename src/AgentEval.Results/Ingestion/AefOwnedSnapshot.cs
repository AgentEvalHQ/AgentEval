// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

// One invocation owns the copied ordinary files. Provides a private copy for subsequent verification.
internal sealed class AefOwnedSnapshot(DirectoryInfo directory, long bytes) : IDisposable
{
    internal string FullName => directory.FullName;
    internal long Bytes { get; } = bytes;
    internal static AefOwnedSnapshot Capture(string directory, AefStagingOptions limits, CancellationToken cancellationToken)
        => Copy(directory, limits, cancellationToken);
    private static AefOwnedSnapshot Copy(string directory, AefStagingOptions limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits.Validate();
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new AefSnapshotException("invalid-source-path");
        directory = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse) throw new AefSnapshotException("unsupported-file-classifier");
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            if (AefFolder.KindOf(current) != AefEntryKind.Folder) throw new AefSnapshotException("unsafe-source-path");
        DirectoryInfo? snapshot = null;
        try
        {
            var inventory = AefIngestionInventory.Scan(directory, limits, cancellationToken);
            snapshot = Directory.CreateTempSubdirectory("ae-evidence-snapshot-");
            var buffer = new byte[64 << 10];
            foreach (var (path, size) in inventory.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(snapshot.FullName, path.Replace('/', Path.DirectorySeparatorChar));
                if (AefFolder.KindOf(source) != AefEntryKind.File) throw new AefSnapshotException("source-changed");
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
                    if (copied > size) throw new AefSnapshotException("source-changed");
                    output.Write(buffer, 0, read);
                }
                if (copied != size) throw new AefSnapshotException("source-changed");
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

internal sealed class AefSnapshotException(string code) : IOException(
    $"Expected: stable ordinary evidence files. Actual: {code}. Suggestions: select an ordinary bounded run. Because: verification needs owned bytes.")
{
    internal string Code { get; } = code;
}
