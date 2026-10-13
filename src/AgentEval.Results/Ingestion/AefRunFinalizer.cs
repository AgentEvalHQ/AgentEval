// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

/// <summary>
/// Bounded preflight followed by the existing AEF verifier and overlay-chain checks. Publishes the original
/// content directory with a single no-overwrite rename. One finalization runs at a time per service instance.
/// </summary>
public sealed class AefRunFinalizer : IAefRunFinalizer
{
    private readonly IAefIngestionWorkspace _workspace;
    private readonly AefStagingOptions _limits;
    private readonly AefVerifyOptions? _verification;
    private int _active;

    /// <summary>Creates a finalizer. Trust policy is optional; absent policy does not establish signer trust.</summary>
    public AefRunFinalizer(IAefIngestionWorkspace workspace, AefStagingOptions? limits = null, AefVerifyOptions? verification = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _limits = limits ?? new();
        _limits.Validate();
        _verification = verification;
    }

    /// <inheritdoc />
    public AefAcceptedRun FinalizeStage(string stageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new AefFinalizationException("busy", "another verification is active");
        try
        {
            if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse)
                throw new IOException("Finalization requires a working native regular-file classifier on this platform.");
            var stage = _workspace.OpenStage(stageId);
            var lockPath = Path.Combine(stage.Directory, "finalize.lock");
            if (Path.Exists(lockPath) && AefFolder.KindOf(lockPath) != AefEntryKind.File)
                throw new AefFinalizationException("path", "non-regular stage lock");
            using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = AefStageState.Read(stage);
            if (state.Status != "staged" || state.Files is < 1 || state.Files > _limits.MaxFiles
                || state.Bytes < 0 || state.Bytes > _limits.MaxBytes)
                throw new AefFinalizationException("state", "stage is not a complete upload within current bounds");
            var content = Path.Combine(stage.Directory, "content");
            Preflight(content, state, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var folder = AefRunFolder.Open(content);
            var verification = AefRunVerifier.Verify(folder, _verification);
            cancellationToken.ThrowIfCancellationRequested();
            if (verification.Outcome == AefOutcome.Unsealed)
                throw new AefFinalizationException("unsealed", "upload has no verified seal");
            if (verification.Outcome != AefOutcome.Intact || verification.RunHash is not { Sealed: true } hash)
                throw new AefFinalizationException("invalid", "AEF verification failed");
            // Run integrity deliberately excludes overlay problems. Ingestion requires both to be valid.
            var chain = OverlayChain.Verify(folder, AefRunDocuments.Read(folder));
            if (chain.Problems.Count != 0)
                throw new AefFinalizationException("invalid", "overlay-chain verification failed");
            cancellationToken.ThrowIfCancellationRequested();
            AefStageState.Write(stage, "publishing", state.Files, state.Bytes, hash.Value);
            string destination;
            try { destination = _workspace.PublishVerifiedStage(stageId, hash.Value, cancellationToken); }
            catch
            {
                // A failed rename leaves content staged. Preserve publishing state if storage cannot record rollback.
                try { AefStageState.Write(stage, "staged", state.Files, state.Bytes); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                throw;
            }
            // The rename is the commit point. A late cancellation or failed receipt write cannot unpublish evidence.
            var receipt = true;
            try { AefStageState.Write(stage, "accepted", state.Files, state.Bytes, hash.Value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { receipt = false; }
            return new(stage.Id, hash.Value, destination, receipt);
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    private void Preflight(string root, AefStageState state, CancellationToken cancellationToken)
    {
        (IReadOnlyList<(string Path, long Size)> Files, long Bytes) inventory;
        try { inventory = AefIngestionInventory.Scan(root, _limits, cancellationToken); }
        catch (AefStagingException error) { throw new AefFinalizationException(error.Code, "content preflight refused the stage"); }
        if (inventory.Files.Count != state.Files || inventory.Bytes != state.Bytes)
            throw new AefFinalizationException("state", "content inventory differs from completed upload");
    }
}
