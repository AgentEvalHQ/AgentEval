// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Results.Runs;
using static AgentEval.Indexing.EvidenceIndexRebuildPreparation;

namespace AgentEval.Indexing;

/// <summary>A recognized interrupted build, or a fully revalidated ready candidate.</summary>
public sealed record EvidenceIndexRebuildInspection(string Directory, bool Ready, PreparedEvidenceIndex? Candidate);

/// <summary>Revalidates or discards an explicitly selected owned rebuild directory after a restart.</summary>
public interface IEvidenceIndexRebuildRecovery
{
    /// <summary>Validates ownership and bounds; a ready receipt additionally requires digest and schema integrity.</summary>
    EvidenceIndexRebuildInspection Inspect(EvidenceIndexOptions target, string directory, CancellationToken cancellationToken = default);
    /// <summary>Discards only an owned candidate with recognized bounded files, including an interrupted or corrupt candidate.</summary>
    void Discard(EvidenceIndexOptions target, string directory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates recovery with preparation and candidate writer leases. Unrecognized ownership, links,
/// extra entries and oversized candidates refuse; target evidence and the active database are never deleted.
/// </summary>
public sealed class SqliteEvidenceIndexRebuildRecovery : IEvidenceIndexRebuildRecovery
{
    /// <inheritdoc />
    public EvidenceIndexRebuildInspection Inspect(EvidenceIndexOptions target, string directory, CancellationToken cancellationToken = default)
    {
        using var candidate = OpenOwned(target, directory, cancellationToken);
        return InspectOwned(target, candidate, cancellationToken);
    }

    /// <inheritdoc />
    public void Discard(EvidenceIndexOptions target, string directory, CancellationToken cancellationToken = default)
    {
        using var candidate = OpenOwned(target, directory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Once deletion begins finish the bounded cleanup rather than introducing a cancellation point
        // between the ownership receipt and its directory. Only prepared derived files are removed.
        foreach (var path in candidate.Files.Where(p => Path.GetFileName(p) is not (OwnerName or DatabaseName + ".lock")))
            File.Delete(path);
        candidate.ReleaseWriterLease();
        File.Delete(candidate.DatabasePath + ".lock");
        File.Delete(Path.Combine(candidate.Directory, OwnerName));
        System.IO.Directory.Delete(candidate.Directory);
    }

    internal static EvidenceIndexRebuildInspection InspectOwned(EvidenceIndexOptions target, OwnedCandidate candidate, CancellationToken cancellationToken)
    {
        var receiptPath = Path.Combine(candidate.Directory, ReceiptName);
        if (!Exists(receiptPath)) return new(candidate.Directory, false, null);
        var receipt = ReadReceipt<PreparedEvidenceIndex>(receiptPath, cancellationToken);
        if (!SamePath(receipt.Directory, candidate.Directory) || !SamePath(receipt.DatabasePath, candidate.DatabasePath) ||
            !SamePath(receipt.TargetDatabasePath, candidate.TargetPath) || receipt.Schema is null ||
            receipt.ContentDigest is null || receipt.ContentDigest.Length != 64 || receipt.ContentDigest.Any(c => !char.IsAsciiHexDigit(c)))
            throw new EvidenceIndexException("invalid-rebuild-receipt");
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            if (Exists(candidate.DatabasePath + suffix)) throw new EvidenceIndexException("rebuild-sidecars-present");
        if (!Exists(candidate.DatabasePath)) throw new EvidenceIndexException("missing-rebuild-database");
        using (var file = new FileStream(candidate.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (Hash(file, target.MaxDatabaseBytes, cancellationToken) != receipt.ContentDigest)
                throw new EvidenceIndexException("rebuild-digest-mismatch");
        var info = new SqliteEvidenceIndexSchema().InspectWhileOwned(new()
        {
            DatabasePath = candidate.DatabasePath, MaxDatabaseBytes = target.MaxDatabaseBytes, BusyTimeoutSeconds = target.BusyTimeoutSeconds
        }, cancellationToken);
        if (info != receipt.Schema) throw new EvidenceIndexException("rebuild-schema-mismatch");
        return new(candidate.Directory, true, receipt);
    }

    internal static OwnedCandidate OpenOwned(EvidenceIndexOptions target, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target.DatabasePath) || !Path.IsPathFullyQualified(target.DatabasePath) ||
            target.MaxDatabaseBytes is < (64 << 10) or > (64L << 30) || target.BusyTimeoutSeconds is < 1 or > 30 ||
            string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new EvidenceIndexException("invalid-rebuild-options");
        var targetPath = Path.GetFullPath(target.DatabasePath);
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var prefix = CandidatePrefix(targetPath);
        var name = Path.GetFileName(directory);
        if (!SamePath(Path.GetDirectoryName(directory), Path.GetDirectoryName(targetPath)) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new EvidenceIndexException("unowned-rebuild-directory");
        new EvidenceIndexRootPlanner().Plan([new("rebuild-recovery", directory, IndexedEvidenceKind.Aef)], cancellationToken: cancellationToken);
        var preparationLock = targetPath + ".rebuild.lock";
        CheckOptionalFile(preparationLock);
        var preparation = new FileStream(preparationLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        FileStream? writer = null;
        try
        {
            var owner = ReadReceipt<RebuildOwner>(Path.Combine(directory, OwnerName), cancellationToken);
            if (owner.Version != 1 || owner.CandidateId != name[prefix.Length..] || !SamePath(owner.TargetDatabasePath, targetPath))
                throw new EvidenceIndexException("unowned-rebuild-directory");
            var database = Path.Combine(directory, DatabaseName);
            CheckOptionalFile(database + ".lock");
            writer = new FileStream(database + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { OwnerName, ReceiptName, DatabaseName, DatabaseName + ".lock", DatabaseName + "-journal", DatabaseName + "-wal", DatabaseName + "-shm",
                    "promotion.json", "publish.sqlite", "previous.sqlite" };
            var files = new List<string>();
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filename = Path.GetFileName(path);
                if (files.Count == allowed.Count || !allowed.Contains(filename) || AefFolder.KindOf(path) != AefEntryKind.File)
                    throw new EvidenceIndexException("unexpected-rebuild-entry");
                var maximum = filename is OwnerName or ReceiptName or "promotion.json" or DatabaseName + ".lock" ? 32 << 10 : target.MaxDatabaseBytes;
                if (new FileInfo(path).Length > maximum) throw new EvidenceIndexException("rebuild-file-limit");
                files.Add(path);
            }
            return new(directory, database, targetPath, files, preparation, writer);
        }
        catch { writer?.Dispose(); preparation.Dispose(); throw; }
    }

    internal static bool SamePath(string? first, string? second) => first is not null && second is not null &&
        string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static T ReadReceipt<T>(string path, CancellationToken cancellationToken)
    {
        if (AefFolder.KindOf(path) != AefEntryKind.File) throw new EvidenceIndexException("unsafe-rebuild-path");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 32 << 10) throw new EvidenceIndexException("rebuild-receipt-limit");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new EvidenceIndexException("rebuild-receipt-limit");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
            Unique(json.RootElement);
            return JsonSerializer.Deserialize<T>(bytes) ?? throw new EvidenceIndexException("invalid-rebuild-receipt");
        }
        catch (JsonException) { throw new EvidenceIndexException("invalid-rebuild-receipt"); }

        static void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateObject())
                {
                    if (!names.Add(item.Name)) throw new EvidenceIndexException("invalid-rebuild-receipt");
                    Unique(item.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }

    internal sealed class OwnedCandidate(string directory, string databasePath, string targetPath, List<string> files,
        FileStream preparationLease, FileStream writerLease) : IDisposable
    {
        internal string Directory { get; } = directory;
        internal string DatabasePath { get; } = databasePath;
        internal string TargetPath { get; } = targetPath;
        internal List<string> Files { get; } = files;
        internal void ReleaseWriterLease() => writerLease.Dispose();
        public void Dispose() { writerLease.Dispose(); preparationLease.Dispose(); }
    }
}
