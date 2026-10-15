// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Ingestion;

/// <summary>
/// Filesystem workspace with an exclusive process lease and bounded staging reservations.
/// Disposal releases ownership but retains staged bytes for explicit recovery; it never deletes evidence.
/// </summary>
/// <remarks>
/// The root is dedicated application storage, not an upload parameter or an existing output-store root.
/// Existing links in its ancestry or managed layout are rejected. The operating-system owner must prevent
/// untrusted local processes from replacing paths while this service uses them.
/// </remarks>
public sealed class AefIngestionWorkspace : IAefIngestionWorkspace
{
    private const string LockName = ".ingestion.lock";
    private const string VersionName = ".ingestion-version";
    private readonly object _gate = new();
    private readonly FileStream _lease;
    private readonly string _staging;
    private readonly string _receipts;
    private readonly int _capacity;
    private readonly int _acceptedCapacity;
    private readonly int _receiptCapacity;
    private bool _disposed;

    /// <summary>
    /// Opens or initializes a dedicated workspace. Refuses a nonempty unrecognized root, links,
    /// unsupported workspace versions, or ownership already held by another instance/process.
    /// </summary>
    public AefIngestionWorkspace(AefIngestionWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Root) || !Path.IsPathFullyQualified(options.Root))
            throw new ArgumentException("Expected: an absolute ingestion root. Actual: missing or relative root. " +
                "Suggestions: configure a dedicated absolute directory. Because: ingestion must not infer a writable viewer root.", nameof(options));
        if (options.MaxPendingStages is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Expected: 1..1024 pending stages. Actual: " +
                options.MaxPendingStages + ". Suggestions: configure a bounded capacity. Because: interrupted stages consume storage.");
        if (options.MaxAcceptedRuns is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Expected: 1..100000 accepted runs. Actual: invalid capacity. " +
                "Suggestions: configure bounded accepted storage. Because: publication consumes disk space.");
        if (options.MaxArchivedReceipts is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Expected: 1..100000 archived receipts. Actual: invalid capacity. " +
                "Suggestions: configure bounded receipt storage. Because: retry records consume disk space.");

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        if (Root == Path.GetPathRoot(Root))
            throw Failure("a dedicated subdirectory", "filesystem root", "choose application storage below the filesystem root");
        _capacity = options.MaxPendingStages;
        _acceptedCapacity = options.MaxAcceptedRuns;
        _receiptCapacity = options.MaxArchivedReceipts;
        EnsureDirectory(Root);
        _staging = Path.Combine(Root, ".staging");
        _receipts = Path.Combine(Root, ".receipts");
        RunsDirectory = Path.Combine(Root, "runs");
        var lockPath = Path.Combine(Root, LockName);
        RejectLinks(lockPath);
        _lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var versionPath = Path.Combine(Root, VersionName);
            RejectLinks(versionPath);
            if (!File.Exists(versionPath))
            {
                if (Directory.EnumerateFileSystemEntries(Root).Any(p => Path.GetFileName(p) != LockName))
                    throw Failure("an empty or initialized ingestion root", "unrecognized existing contents", "choose a separate empty directory");
                using var version = new FileStream(versionPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                version.Write("1\n"u8);
                version.Flush(flushToDisk: true);
            }
            else
            {
                using var version = new FileStream(versionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (version.Length != 2 || version.ReadByte() != '1' || version.ReadByte() != '\n')
                    throw Failure("workspace version 1", "unsupported version marker", "use storage initialized by a compatible ingestion service");
            }
            EnsureDirectory(_staging);
            EnsureDirectory(_receipts);
            EnsureDirectory(RunsDirectory);
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public string RunsDirectory { get; }

    /// <inheritdoc />
    public AefIngestionStage BeginStage(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(_staging);
            RejectLinks(_receipts);
            RejectLinks(RunsDirectory);
            // Count all entries conservatively. Never traverse a retained stage to reserve another.
            // Take bounds enumeration even if the directory was populated outside this service.
            if (Directory.EnumerateFileSystemEntries(_staging).Take(_capacity).Count() >= _capacity)
                throw Failure($"fewer than {_capacity} pending stages", "staging capacity reached", "recover or explicitly remove interrupted stages before retrying");

            string id;
            string directory;
            do
            {
                id = Guid.NewGuid().ToString("N");
                directory = Path.Combine(_staging, id);
            } while (Path.Exists(directory) || Path.Exists(Path.Combine(_receipts, id)));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectory(directory);
            return new AefIngestionStage(id, directory);
        }
    }

    /// <inheritdoc />
    public AefIngestionStage OpenStage(string stageId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (stageId is null || stageId.Length != 32 || !stageId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new AefFinalizationException("path", "invalid stage ID");
            var directory = Path.Combine(_staging, stageId);
            var archived = Path.Combine(_receipts, stageId);
            RejectLinks(directory);
            RejectLinks(archived);
            if (Path.Exists(directory) && Path.Exists(archived))
                throw new AefFinalizationException("state", "ambiguous pending and archived receipt identity");
            if (Directory.Exists(directory)) return new(stageId, directory);
            if (Directory.Exists(archived)) return new(stageId, archived);
            throw new DirectoryNotFoundException("Ingestion stage not found.");
        }
    }

    /// <inheritdoc />
    public string PublishVerifiedStage(string stageId, string runHash, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var stage = OpenStage(stageId);
            if (stage.Directory != Path.Combine(_staging, stageId))
                throw new AefFinalizationException("state", "an archived receipt cannot publish new evidence");
            if (runHash is null || runHash.Length != 64 || !runHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new AefFinalizationException("path", "invalid verified run hash");
            var source = Path.Combine(stage.Directory, "content");
            var destination = Path.Combine(RunsDirectory, runHash);
            RejectLinks(source);
            RejectLinks(destination);
            if (Path.Exists(destination)) throw new AefFinalizationException("conflict", "accepted hash already exists");
            if (Directory.EnumerateFileSystemEntries(RunsDirectory).Take(_acceptedCapacity).Count() >= _acceptedCapacity)
                throw new AefFinalizationException("capacity", "accepted storage capacity reached");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(source, destination);
            return destination;
        }
    }

    /// <inheritdoc />
    public string OpenAcceptedRun(string runHash)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (runHash is null || runHash.Length != 64 || !runHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new AefFinalizationException("path", "invalid accepted run hash");
            var path = Path.Combine(RunsDirectory, runHash);
            RejectLinks(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Accepted evidence not found.");
            return path;
        }
    }

    /// <inheritdoc />
    public bool ArchiveReceipt(string stageId, IDisposable stageLease)
    {
        ArgumentNullException.ThrowIfNull(stageLease);
        lock (_gate)
        {
            var stage = OpenStage(stageId);
            var destination = Path.Combine(_receipts, stageId);
            if (stage.Directory == destination) return true;
            var state = AefStageState.Read(stage);
            if (state.Status is not ("accepted" or "discarded"))
                throw new AefFinalizationException("state", "only terminal receipts can be archived");
            // Control-only directories prevent duplicate payloads or unexpected files from escaping staging limits.
            var entries = Directory.EnumerateFileSystemEntries(stage.Directory).Take(4).ToArray();
            if (entries.Length > 3) throw new AefFinalizationException("state", "unexpected receipt contents");
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name is not ("state.json" or "state.tmp" or "finalize.lock")
                    || Runs.AefFolder.KindOf(entry) != Runs.AefEntryKind.File
                    || new FileInfo(entry).Length > (name == "finalize.lock" ? 0 : 4096))
                    throw new AefFinalizationException("state", "receipt is not a bounded control-only directory");
            }
            RejectLinks(_receipts);
            if (Directory.EnumerateFileSystemEntries(_receipts).Take(_receiptCapacity).Count() >= _receiptCapacity) return false;
            // FileShare.None cannot stay open during a Windows directory rename. Workspace ownership keeps
            // new lookups/reservations out of this transition. A previously resolved competing lease can make
            // the move fail; in that case the receipt remains pending and retryable, without deleting proof.
            stageLease.Dispose();
            Directory.Move(stage.Directory, destination);
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lease.Dispose();
        }
    }

    private static void EnsureDirectory(string path)
    {
        RejectLinks(path);
        Directory.CreateDirectory(path);
        RejectLinks(path);
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Failure("ordinary directories and files", "a symbolic link or reparse point", "configure a root without linked path components");
        }
    }

    private static IOException Failure(string expected, string actual, string suggestion) =>
        new($"Expected: {expected}. Actual: {actual}. Suggestions: {suggestion}. Because: ingestion owns a separate bounded workspace.");
}
