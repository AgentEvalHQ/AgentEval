// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using static AgentEval.Indexing.EvidenceIndexRebuildPreparation;
using static AgentEval.Indexing.SqliteEvidenceIndexRebuildRecovery;

namespace AgentEval.Indexing;

/// <summary>Exact target bytes captured under the index ownership lease, or explicit absence.</summary>
public sealed record EvidenceIndexTargetCheckpoint(string DatabasePath, string? ContentDigest);
/// <summary>A promoted candidate; the old database remains in the owned candidate directory when present.</summary>
public sealed record EvidenceIndexPromotion(string DatabasePath, string InstanceId, string? PreviousDatabasePath, bool AlreadyPublished);

/// <summary>Captures a guarded target checkpoint and adopts a fully validated prepared index.</summary>
public interface IEvidenceIndexRebuildPromotion
{
    /// <summary>Requires an idle index and no SQLite sidecars. Does not open or repair database contents.</summary>
    EvidenceIndexTargetCheckpoint CaptureTarget(EvidenceIndexOptions target, CancellationToken cancellationToken = default);
    /// <summary>Refuses changed targets. Retains the previous database and supports retry after completed replacement.</summary>
    EvidenceIndexPromotion Promote(EvidenceIndexOptions target, string directory, EvidenceIndexTargetCheckpoint expected,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Requires cooperative readers/writers to honor the target .lock lease. Publishes only closed SQLite files
/// on the same filesystem; refuses sidecars rather than detaching recovery journals. File flushes do not
/// imply portable directory-entry durability against power loss.
/// </summary>
public sealed class SqliteEvidenceIndexRebuildPromotion : IEvidenceIndexRebuildPromotion
{
    /// <inheritdoc />
    public EvidenceIndexTargetCheckpoint CaptureTarget(EvidenceIndexOptions target, CancellationToken cancellationToken = default)
    {
        var path = ValidateTarget(target, cancellationToken);
        using var ownership = TargetLease(path);
        return CaptureOwned(target, path, cancellationToken);
    }

    /// <inheritdoc />
    public EvidenceIndexPromotion Promote(EvidenceIndexOptions target, string directory, EvidenceIndexTargetCheckpoint expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var path = ValidateTarget(target, cancellationToken);
        if (!SamePath(expected.DatabasePath, path)) throw new EvidenceIndexException("rebuild-target-mismatch");
        using var candidate = OpenOwned(target, directory, cancellationToken);
        var ready = InspectOwned(target, candidate, cancellationToken).Candidate ?? throw new EvidenceIndexException("rebuild-not-ready");
        using var ownership = TargetLease(path);
        var current = CaptureOwned(target, path, cancellationToken);
        var intentPath = Path.Combine(candidate.Directory, "promotion.json");
        var publishPath = Path.Combine(candidate.Directory, "publish.sqlite");
        var previousPath = Path.Combine(candidate.Directory, "previous.sqlite");
        var intent = new PromotionIntent(1, expected, ready.ContentDigest, ready.Schema.InstanceId);
        if (Exists(intentPath))
        {
            if (ReadReceipt<PromotionIntent>(intentPath, cancellationToken) != intent)
                throw new EvidenceIndexException("rebuild-promotion-conflict");
            if (current.ContentDigest == ready.ContentDigest)
            {
                VerifyPrevious(target, previousPath, expected.ContentDigest, cancellationToken);
                return new(path, ready.Schema.InstanceId, expected.ContentDigest is null ? null : previousPath, true);
            }
        }
        else
        {
            if (Exists(previousPath) || Exists(publishPath)) throw new EvidenceIndexException("unexpected-promotion-files");
            if (current != expected) throw new EvidenceIndexException("stale-rebuild-target");
            WriteReceipt(intentPath, intent, cancellationToken);
        }
        if (current != expected) throw new EvidenceIndexException("stale-rebuild-target");
        if (Exists(previousPath))
        {
            // Some replacement implementations can create the backup before replacing the target.
            // A crash there leaves both copies of the same old bytes. The intent and current target
            // have already matched; verify the backup before removing it so replacement can retry.
            VerifyPrevious(target, previousPath, expected.ContentDigest, cancellationToken);
            File.Delete(previousPath);
        }
        // A partial publish copy belongs to this validated intent and is never a usable database.
        if (Exists(publishPath)) File.Delete(publishPath);
        using (var input = new FileStream(candidate.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(publishPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var buffer = new byte[64 << 10]; long bytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, target.MaxDatabaseBytes - bytes + 1));
                if (read == 0) break;
                bytes += read;
                if (bytes > target.MaxDatabaseBytes) throw new EvidenceIndexException("size-limit");
                output.Write(buffer, 0, read);
            }
            output.Flush(flushToDisk: true); output.Position = 0;
            if (Hash(output, target.MaxDatabaseBytes, cancellationToken) != ready.ContentDigest)
                throw new EvidenceIndexException("rebuild-digest-mismatch");
        }
        cancellationToken.ThrowIfCancellationRequested();
        // No cancellation after this commit point: retry recognizes target digest plus persisted intent.
        if (expected.ContentDigest is null) File.Move(publishPath, path);
        else File.Replace(publishPath, path, previousPath);
        return new(path, ready.Schema.InstanceId, expected.ContentDigest is null ? null : previousPath, false);
    }

    private static string ValidateTarget(EvidenceIndexOptions target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target.DatabasePath) || !Path.IsPathFullyQualified(target.DatabasePath) ||
            target.MaxDatabaseBytes is < (64 << 10) or > (64L << 30) || target.BusyTimeoutSeconds is < 1 or > 30)
            throw new EvidenceIndexException("invalid-rebuild-options");
        var path = Path.GetFullPath(target.DatabasePath);
        new EvidenceIndexRootPlanner().Plan([new("rebuild-target", Path.GetDirectoryName(path)!, IndexedEvidenceKind.Aef)], cancellationToken: cancellationToken);
        CheckOptionalFile(path); CheckOptionalFile(path + ".lock");
        return path;
    }

    private static FileStream TargetLease(string path) => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static EvidenceIndexTargetCheckpoint CaptureOwned(EvidenceIndexOptions target, string path, CancellationToken cancellationToken)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            if (Exists(path + suffix)) throw new EvidenceIndexException("target-sidecars-present");
        if (!Exists(path)) return new(path, null);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(path, Hash(file, target.MaxDatabaseBytes, cancellationToken));
    }

    private static void VerifyPrevious(EvidenceIndexOptions target, string path, string? expectedDigest, CancellationToken cancellationToken)
    {
        if (expectedDigest is null)
        {
            if (Exists(path)) throw new EvidenceIndexException("unexpected-promotion-backup");
            return;
        }
        if (!Exists(path)) throw new EvidenceIndexException("missing-promotion-backup");
        using var file = File.OpenRead(path);
        if (Hash(file, target.MaxDatabaseBytes, cancellationToken) != expectedDigest)
            throw new EvidenceIndexException("promotion-backup-mismatch");
    }

    internal sealed record PromotionIntent(int Version, EvidenceIndexTargetCheckpoint Target, string CandidateDigest, string CandidateInstanceId);
}
