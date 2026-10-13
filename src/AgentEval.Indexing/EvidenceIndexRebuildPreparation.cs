// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Indexing;

/// <summary>A validated, detached replacement candidate. Preparation does not replace the configured index.</summary>
public sealed record PreparedEvidenceIndex(string Directory, string DatabasePath, string TargetDatabasePath,
    string ContentDigest, EvidenceIndexSchemaInfo Schema);

/// <summary>Builds an isolated replacement from authoritative sources before publication is attempted.</summary>
public interface IEvidenceIndexRebuildPreparation
{
    /// <summary>
    /// Calls the population function with a new bounded writer. It must return true only for complete
    /// source coverage; false, exceptions and cancellation discard this invocation's candidate.
    /// </summary>
    PreparedEvidenceIndex Prepare(EvidenceIndexOptions target, Func<IEvidenceIndexWriter, CancellationToken, bool> populate,
        EvidenceIndexWriteLimits? limits = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates a unique sibling directory, so even a corrupt target is left untouched. Persists a bounded
/// ready receipt after closing the writer, validating schema and flushing candidate bytes. Publication
/// must separately acquire target ownership and revalidate the receipt and candidate digest.
/// </summary>
public sealed class EvidenceIndexRebuildPreparation(IEvidenceIndexSchema schema) : IEvidenceIndexRebuildPreparation
{
    internal const string ReceiptName = "ready.json";
    internal const string DatabaseName = "index.sqlite";
    internal const string OwnerName = "owner.json";

    /// <inheritdoc />
    public PreparedEvidenceIndex Prepare(EvidenceIndexOptions target, Func<IEvidenceIndexWriter, CancellationToken, bool> populate,
        EvidenceIndexWriteLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(populate);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target.DatabasePath) || !Path.IsPathFullyQualified(target.DatabasePath) ||
            target.MaxDatabaseBytes is < (64 << 10) or > (64L << 30) || target.BusyTimeoutSeconds is < 1 or > 30)
            throw new EvidenceIndexException("invalid-rebuild-options");
        var targetPath = Path.GetFullPath(target.DatabasePath);
        var parent = Path.GetDirectoryName(targetPath)!;
        // Requiring an existing ordinary parent keeps directory creation confined to one owned candidate.
        new EvidenceIndexRootPlanner().Plan([new("rebuild-parent", parent, IndexedEvidenceKind.Aef)], cancellationToken: cancellationToken);
        CheckOptionalFile(targetPath);
        var preparationLock = targetPath + ".rebuild.lock";
        CheckOptionalFile(preparationLock);
        using var ownership = new FileStream(preparationLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var prefix = CandidatePrefix(targetPath);
        var entries = 0; var candidates = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entries > 4096) throw new EvidenceIndexException("rebuild-parent-entry-limit");
            if (Path.GetFileName(entry).StartsWith(prefix, StringComparison.Ordinal) && ++candidates >= 8)
                throw new EvidenceIndexException("rebuild-candidate-limit");
        }
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(parent, prefix + id);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new EvidenceIndexException("rebuild-collision");
        Directory.CreateDirectory(directory);
        var successful = false;
        try
        {
            WriteReceipt(Path.Combine(directory, OwnerName), new RebuildOwner(1, id, targetPath), cancellationToken);
            var candidateOptions = new EvidenceIndexOptions
            {
                DatabasePath = Path.Combine(directory, DatabaseName), MaxDatabaseBytes = target.MaxDatabaseBytes,
                BusyTimeoutSeconds = target.BusyTimeoutSeconds
            };
            using (var writer = new SqliteEvidenceIndexWriter(candidateOptions, schema, limits, cancellationToken))
            {
                if (!populate(writer, cancellationToken)) throw new EvidenceIndexException("incomplete-rebuild");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var info = schema.EnsureCurrent(candidateOptions, cancellationToken);
            // A closed DELETE-journal candidate should be self-contained. Never discard or detach SQLite
            // sidecars to force publication; a journal can be part of crash recovery.
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                if (Exists(candidateOptions.DatabasePath + suffix)) throw new EvidenceIndexException("rebuild-sidecars-present");
            string digest;
            using (var file = new FileStream(candidateOptions.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (file.Length > target.MaxDatabaseBytes) throw new EvidenceIndexException("size-limit");
                file.Flush(flushToDisk: true);
                digest = Hash(file, target.MaxDatabaseBytes, cancellationToken);
            }
            var prepared = new PreparedEvidenceIndex(directory, candidateOptions.DatabasePath, targetPath, digest, info);
            WriteReceipt(Path.Combine(directory, ReceiptName), prepared, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            successful = true;
            return prepared;
        }
        finally
        {
            if (!successful) RemoveOwnedCandidate(directory);
        }
    }

    internal static string CandidatePrefix(string targetPath)
    {
        var identity = OperatingSystem.IsWindows() ? targetPath.ToUpperInvariant() : targetPath;
        return ".ae-index-rebuild-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant() + "-";
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void CheckOptionalFile(string path)
    {
        if (Exists(path) && AefFolder.KindOf(path) != AefEntryKind.File) throw new EvidenceIndexException("unsafe-rebuild-path");
    }

    internal static string Hash(Stream file, long limit, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 << 10]; long bytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - bytes + 1));
            if (read == 0) break;
            bytes += read;
            if (bytes > limit) throw new EvidenceIndexException("size-limit");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void WriteReceipt<T>(string path, T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 32 << 10) throw new EvidenceIndexException("rebuild-receipt-limit");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(flushToDisk: true);
    }

    internal static void RemoveOwnedCandidate(string directory)
    {
        // Called only for this invocation's unique directory. Do not recurse into unexpected entries:
        // callbacks get a writer, but trusted filesystem ownership is still a required precondition.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { OwnerName, ReceiptName, DatabaseName, DatabaseName + ".lock", DatabaseName + "-journal", DatabaseName + "-wal", DatabaseName + "-shm" };
        var entries = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (entries.Count == allowed.Count || !allowed.Contains(Path.GetFileName(path)) || AefFolder.KindOf(path) != AefEntryKind.File)
                throw new EvidenceIndexException("unexpected-rebuild-entry");
            entries.Add(path);
        }
        foreach (var path in entries) File.Delete(path);
        Directory.Delete(directory);
    }

    internal sealed record RebuildOwner(int Version, string CandidateId, string TargetDatabasePath);
}
