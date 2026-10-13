// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

/// <summary>Inspects and explicitly reconciles application-owned ingestion stages.</summary>
public interface IAefIngestionRecovery
{
    /// <summary>Inspects at most maxStages pending entries (1..1024), without mutating them.</summary>
    AefRecoveryInspection Inspect(int maxStages = 64, CancellationToken cancellationToken = default);
    /// <summary>Reconciles publication or marks an abandoned receiving stage interrupted after acquiring its lease.</summary>
    AefRecoveryResult Recover(string stageId, CancellationToken cancellationToken = default);
    /// <summary>Explicitly discards failed, interrupted, or staged-but-unaccepted scratch data. Never discards accepted evidence or active uploads.</summary>
    AefRecoveryResult DiscardStage(string stageId, CancellationToken cancellationToken = default);
}

/// <summary>A path-free stage inspection item. A null ID denotes an unexpected pending entry.</summary>
public sealed record AefStageInspection(string? StageId, string Status);
/// <summary>A bounded non-atomic inspection. Complete means all enumerated pending entries fit the requested bound.</summary>
public sealed record AefRecoveryInspection(IReadOnlyList<AefStageInspection> Stages, bool Complete);
/// <summary>A path-free recovery outcome. Discarded receipts preserve retry identity without retaining upload payload.</summary>
public sealed record AefRecoveryResult(string StageId, string Status, bool ReceiptArchived, string? RunHash = null);

/// <summary>Lease-protected recovery with no automatic deletion. Discard requires an explicit call and a known unaccepted state.</summary>
public sealed class AefIngestionRecovery : IAefIngestionRecovery
{
    private readonly IAefIngestionWorkspace _workspace;
    private readonly IAefRunFinalizer _finalizer;
    private readonly AefStagingOptions _limits;
    private int _active;

    /// <summary>Creates recovery using the same workspace, finalizer and bounds as ingestion.</summary>
    public AefIngestionRecovery(IAefIngestionWorkspace workspace, IAefRunFinalizer finalizer, AefStagingOptions? limits = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public AefRecoveryInspection Inspect(int maxStages = 64, CancellationToken cancellationToken = default)
        => RunExclusive(() => InspectCore(maxStages, cancellationToken));

    private AefRecoveryInspection InspectCore(int maxStages, CancellationToken cancellationToken)
    {
        if (maxStages is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxStages),
            "Expected: 1..1024 pending entries. Actual: invalid limit. Suggestions: choose a bounded scan. Because: recovery must have finite resource use.");
        RequireClassifier();
        var root = Path.Combine(_workspace.Root, ".staging");
        if (AefFolder.KindOf(root) != AefEntryKind.Folder)
            throw new AefFinalizationException("path", "non-ordinary pending directory");
        var items = new List<AefStageInspection>();
        var complete = true;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == maxStages) { complete = false; break; }
            var id = Path.GetFileName(path);
            if (!IsId(id)) { items.Add(new(null, "unexpected-entry")); continue; }
            try
            {
                var stage = _workspace.OpenStage(id);
                using var lease = AefStageLease.Acquire(stage);
                var state = AefStageState.Read(stage);
                var status = state.Status is "receiving" or "staged" or "publishing" or "accepted"
                    or "failed" or "interrupted" or "discarding" or "discarded" ? state.Status : "unknown";
                items.Add(new(id, status));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { items.Add(new(id, "unavailable")); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(items.AsReadOnly(), complete);
    }

    /// <inheritdoc />
    public AefRecoveryResult Recover(string stageId, CancellationToken cancellationToken = default)
        => RunExclusive(() => RecoverCore(stageId, cancellationToken));

    private AefRecoveryResult RecoverCore(string stageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireClassifier();
        var stage = _workspace.OpenStage(stageId);
        using (var lease = AefStageLease.Acquire(stage))
        {
            var state = AefStageState.Read(stage);
            if (state.Status == "receiving")
            {
                // Receiving plus a free shared lease means the upload owner has stopped. Missing metadata
                // is deliberately not treated this way: a newly reserved upload may not have its lease yet.
                cancellationToken.ThrowIfCancellationRequested();
                AefStageState.Write(stage, "interrupted", state.Files, state.Bytes);
                return new(stage.Id, "interrupted", false);
            }
            if (state.Status == "discarded")
                return new(stage.Id, "discarded", TryArchive(stage.Id, lease));
            if (state.Status is "failed" or "interrupted" or "staged" or "discarding")
                return new(stage.Id, state.Status, false);
            if (state.Status is not ("publishing" or "accepted"))
                throw new AefFinalizationException("state", "unknown recovery state");
        }
        // Finalization reacquires the same lease and re-verifies authoritative evidence before changing state.
        var accepted = _finalizer.FinalizeStage(stageId, cancellationToken);
        return new(stageId, "accepted", accepted.ReceiptArchived, accepted.RunHash);
    }

    /// <inheritdoc />
    public AefRecoveryResult DiscardStage(string stageId, CancellationToken cancellationToken = default)
        => RunExclusive(() => DiscardCore(stageId, cancellationToken));

    private AefRecoveryResult DiscardCore(string stageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireClassifier();
        var stage = _workspace.OpenStage(stageId);
        using var lease = AefStageLease.Acquire(stage);
        var state = AefStageState.Read(stage);
        if (state.Status == "discarded") return new(stage.Id, "discarded", TryArchive(stage.Id, lease));
        if (state.Status is not ("failed" or "interrupted" or "staged" or "discarding"))
            throw new AefFinalizationException("state", "discard requires a known unaccepted upload");
        var expected = Path.Combine(_workspace.Root, ".staging", stage.Id);
        if (!string.Equals(Path.GetFullPath(stage.Directory), Path.GetFullPath(expected),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AefFinalizationException("path", "discard is outside the pending stage");
        var content = Path.Combine(stage.Directory, "content");
        if (Path.Exists(content)) AefIngestionInventory.Scan(content, _limits, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        AefStageState.Write(stage, "discarding", state.Files, state.Bytes);
        // Intent is durable before deletion. A crash or storage failure can resume this same owned subtree.
        if (Path.Exists(content)) Directory.Delete(content, recursive: true);
        AefStageState.Write(stage, "discarded", state.Files, state.Bytes);
        return new(stage.Id, "discarded", TryArchive(stage.Id, lease));
    }

    private bool TryArchive(string stageId, IDisposable lease)
    {
        try { return _workspace.ArchiveReceipt(stageId, lease); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private T RunExclusive<T>(Func<T> operation)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new AefFinalizationException("busy", "another recovery operation is active");
        try { return operation(); }
        finally { Volatile.Write(ref _active, 0); }
    }

    private static bool IsId(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void RequireClassifier()
    {
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse)
            throw new IOException("Recovery requires a working native regular-file classifier on this platform.");
    }
}
