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
            using var lease = AefStageLease.Acquire(stage);
            var state = AefStageState.Read(stage);
            if (state.Status is not ("staged" or "publishing" or "accepted") || state.Files is < 1 || state.Files > _limits.MaxFiles
                || state.Bytes < 0 || state.Bytes > _limits.MaxBytes)
                throw new AefFinalizationException("state", "stage is not a complete upload within current bounds");
            var content = Path.Combine(stage.Directory, "content");
            if (state.Status is "publishing" or "accepted")
            {
                if (!IsHash(state.RunHash) || !IsHash(state.SnapshotDigest))
                    throw new AefFinalizationException("state", "receipt lacks a complete verified snapshot identity");
                if (state.Status == "accepted" || !Path.Exists(content))
                {
                    string acceptedPath;
                    try { acceptedPath = _workspace.OpenAcceptedRun(state.RunHash!); }
                    catch (DirectoryNotFoundException)
                    { throw new AefFinalizationException("state", "recorded accepted evidence is missing"); }
                    var accepted = VerifySnapshot(acceptedPath, state, cancellationToken);
                    RequireReceiptMatch(state, accepted);
                    return RecordAccepted(stage, state, accepted, acceptedPath, alreadyAccepted: true);
                }
            }

            var snapshot = VerifySnapshot(content, state, cancellationToken);
            if (state.Status == "publishing") RequireReceiptMatch(state, snapshot);

            // Base run hashes exclude signatures and overlays. Deduplicate only after verifying the existing
            // directory and matching a fingerprint over every stored file, including those unsealed files.
            var existingPath = Path.Combine(_workspace.RunsDirectory, snapshot.RunHash);
            if (Path.Exists(existingPath))
            {
                existingPath = _workspace.OpenAcceptedRun(snapshot.RunHash);
                var existing = VerifySnapshot(existingPath, expected: null, cancellationToken);
                if (existing != snapshot)
                    throw new AefFinalizationException("conflict", "accepted hash has different evidence bytes");
                cancellationToken.ThrowIfCancellationRequested();
                return RecordAccepted(stage, state, snapshot, existingPath, alreadyAccepted: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AefStageState.Write(stage, "publishing", state.Files, state.Bytes, snapshot.RunHash, snapshot.Digest);
            string destination;
            try { destination = _workspace.PublishVerifiedStage(stageId, snapshot.RunHash, cancellationToken); }
            catch
            {
                // A failed rename leaves content staged. Preserve publishing state if storage cannot record rollback.
                try { AefStageState.Write(stage, "staged", state.Files, state.Bytes); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                throw;
            }
            // The rename is the commit point. A late cancellation or failed receipt write cannot unpublish evidence.
            return RecordAccepted(stage, state, snapshot, destination, alreadyAccepted: false);
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    private sealed record VerifiedSnapshot(string RunHash, string Digest);

    private VerifiedSnapshot VerifySnapshot(string root, AefStageState? expected, CancellationToken cancellationToken)
    {
        (IReadOnlyList<(string Path, long Size)> Files, long Bytes) inventory;
        try { inventory = AefIngestionInventory.Scan(root, _limits, cancellationToken); }
        catch (AefStagingException error) { throw new AefFinalizationException(error.Code, "content preflight refused the stage"); }
        if (expected is not null && (inventory.Files.Count != expected.Files || inventory.Bytes != expected.Bytes))
            throw new AefFinalizationException("state", "content inventory differs from completed upload");
        cancellationToken.ThrowIfCancellationRequested();
        var folder = AefRunFolder.Open(root);
        var verification = AefRunVerifier.Verify(folder, _verification);
        cancellationToken.ThrowIfCancellationRequested();
        if (verification.Outcome == AefOutcome.Unsealed)
            throw new AefFinalizationException("unsealed", "upload has no verified seal");
        if (verification.Outcome != AefOutcome.Intact || verification.RunHash is not { Sealed: true } hash)
            throw new AefFinalizationException("invalid", "AEF verification failed");
        if (OverlayChain.Verify(folder, AefRunDocuments.Read(folder)).Problems.Count != 0)
            throw new AefFinalizationException("invalid", "overlay-chain verification failed");
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotDigest(folder, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(hash.Value, snapshot);
    }

    // Application snapshot identity, not the AEF base run hash: the AEF manifest encoding applied to ALL files.
    internal static string SnapshotDigest(AefRunFolder folder, CancellationToken cancellationToken = default) =>
        AefManifest.Create(folder.Files.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AefManifestEntry(path, folder.Sha256(path), folder.Size(path));
        })).RunHash;

    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireReceiptMatch(AefStageState state, VerifiedSnapshot snapshot)
    {
        if (state.RunHash != snapshot.RunHash || state.SnapshotDigest != snapshot.Digest)
            throw new AefFinalizationException("conflict", "evidence differs from the recorded publication snapshot");
    }

    private static AefAcceptedRun RecordAccepted(AefIngestionStage stage, AefStageState state,
        VerifiedSnapshot snapshot, string destination, bool alreadyAccepted)
    {
        var persisted = true;
        try { AefStageState.Write(stage, "accepted", state.Files, state.Bytes, snapshot.RunHash, snapshot.Digest); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { persisted = false; }
        return new(stage.Id, snapshot.RunHash, destination, persisted, alreadyAccepted);
    }
}
