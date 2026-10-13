// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

internal static class AefIngestionInventory
{
    internal static (IReadOnlyList<(string Path, long Size)> Files, long Bytes) Scan(string root, AefStagingOptions limits, CancellationToken cancellationToken)
    {
        if (AefFolder.KindOf(root) != AefEntryKind.Folder)
            throw new AefStagingException("path", "non-ordinary content directory");
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        var files = 0;
        var inventory = new List<(string Path, long Size)>();
        long bytes = 0;
        // A valid 255-byte AEF path has at most 128 components. This also bounds unexpected empty directories.
        var maxEntries = checked(limits.MaxFiles * 128);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AefFolder.KindOf(directory) != AefEntryKind.Folder)
                throw new AefStagingException("path", "content directory changed type");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > maxEntries) throw new AefStagingException("limit", "content tree entry budget exceeded");
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (!AefPaths.IsValid(relative)) throw new AefStagingException("path", "invalid content path");
                switch (AefFolder.KindOf(entry))
                {
                    case AefEntryKind.Folder: pending.Push(entry); break;
                    case AefEntryKind.File:
                        var size = new FileInfo(entry).Length;
                        if (++files > limits.MaxFiles || size > limits.MaxFileBytes || size > limits.MaxBytes - bytes)
                            throw new AefStagingException("limit", "content file or byte budget exceeded");
                        bytes += size;
                        inventory.Add((relative, size));
                        break;
                    default: throw new AefStagingException("path", "link or special entry in content");
                }
            }
        }
        return (inventory.AsReadOnly(), bytes);
    }
}
