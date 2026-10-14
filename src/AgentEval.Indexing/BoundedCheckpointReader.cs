// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Results;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Requirements;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Indexing;

/// <summary>An exact server-resolved run selection; never a remote filesystem binding.</summary>
public sealed record CheckpointRunSelection(string Directory, string RunHash);
/// <summary>One recomputed lane, separate from the checkpoint's declared decision.</summary>
public sealed record CheckpointLaneExplanation(string Lane, string RuleKind, string? EvidenceStatus,
    string? SubjectVersion, string? OldestClosedAt, IReadOnlyList<string> Axes, bool ReadsUnknown,
    IReadOnlyList<AefProblem> Problems);
/// <summary>Detached checkpoint provenance, declarations and verification evidence. Availability is not approval.</summary>
public sealed record CheckpointExplanation(string ManifestDigest, string PolicyDigest, string RequestedAt,
    string EvaluatedAt, string ManifestJson, IReadOnlyList<string> ManifestProblems,
    IReadOnlyList<AefProblem> Problems, IReadOnlyList<CheckpointLaneExplanation> Lanes,
    IReadOnlyList<string> SignedBy, IReadOnlyList<string> Anchors, bool EnvelopePresent,
    bool SignatureChecked, string? EnvelopeResult, IReadOnlyList<DsseSignatureCheck> Signatures);

/// <summary>Verifies configured checkpoint bytes against explicit, bounded owned run snapshots.</summary>
public interface IBoundedCheckpointReader
{
    /// <summary>Manifest/envelope each at most 1 MiB; at most eight run selections and 64 MiB aggregate capture.</summary>
    CheckpointExplanation Read(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte>? envelope,
        IReadOnlyList<CheckpointRunSelection> selections, AefTime at, TrustPolicy? policy,
        long maximumBytes = 64L << 20, CancellationToken cancellationToken = default);
}

/// <summary>Reuses the checkpoint verifier without discovering runs outside the explicit selection.</summary>
public sealed class BoundedCheckpointReader : IBoundedCheckpointReader
{
    /// <inheritdoc />
    public CheckpointExplanation Read(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte>? envelope,
        IReadOnlyList<CheckpointRunSelection> selections, AefTime at, TrustPolicy? policy,
        long maximumBytes = 64L << 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        cancellationToken.ThrowIfCancellationRequested();
        if (!at.IsValid) throw new EvidenceIndexException("invalid-checkpoint-time");
        if (manifest.Length > 1 << 20 || envelope?.Length > 1 << 20 || selections.Count > 8 ||
            maximumBytes is < 1 or > (64L << 20)) throw new EvidenceIndexException("checkpoint-limit");
        var bytes = manifest.ToArray();
        var signature = envelope?.ToArray();
        var document = CheckpointVerifier.Read(bytes);
        var laneCount = AefNode.Objects(document["lanes"]).Count();
        if (laneCount > 64) throw new EvidenceIndexException("checkpoint-limit");
        var snapshots = new List<AefReadSnapshot>();
        try
        {
            var stores = new List<AefRunStore>();
            long remaining = maximumBytes; long rows = 0;
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.RunHash is not { Length: 64 } hash || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
                    throw new EvidenceIndexException("invalid-checkpoint-selection");
                if (remaining == 0) throw new EvidenceIndexException("checkpoint-limit");
                var snapshot = AefReadSnapshot.Capture(selection.Directory,
                    new AefStagingOptions { MaxBytes = remaining, MaxFileBytes = remaining }, cancellationToken);
                snapshots.Add(snapshot); remaining -= snapshot.Bytes;
                var store = AefRunStore.Open(snapshot.FullName, policy);
                // Each configured selection must be one run, not a directory of implicitly discovered runs.
                if (store.Runs.Count != 1 || store.Runs[0].Directory != snapshot.FullName ||
                    store.Runs[0].RunHash.Value != hash || !store.Runs[0].Intact)
                    throw new EvidenceIndexException("invalid-checkpoint-evidence");
                rows += store.Runs[0].Documents.Results.Lines.Count;
                if (rows * laneCount > 100_000) throw new EvidenceIndexException("checkpoint-limit");
                stores.Add(store);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var verified = CheckpointVerifier.Verify(bytes, AefRunStore.Combine(stores),
                new() { At = at.ToString(), Policy = policy, Envelope = signature });
            var result = new CheckpointExplanation(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                RequirementEvidenceReader.PolicyDigest(policy), at.ToString(),
                AefNode.String(document["decisionInput"]?["evaluatedAt"]) ?? at.ToString(), document.ToJsonString(),
                verified.ManifestProblems.ToArray(), verified.Problems.ToArray(),
                verified.Lanes.Select(lane => new CheckpointLaneExplanation(lane.Lane, lane.Rule.Kind,
                    lane.Result is null ? null : LaneResult.StatusName(lane.Result.Status), lane.Result?.SubjectVersion,
                    lane.Result?.OldestClosedAt, lane.Result?.Axes?.ToArray() ?? [], lane.ReadsUnknown, lane.Problems.ToArray())).ToArray(),
                verified.SignedBy.ToArray(), verified.Anchors.ToArray(), signature is not null,
                verified.Signature is not null, verified.Signature?.EnvelopeResult?.ToString(),
                verified.Signature?.Signatures.ToArray() ?? []);
            cancellationToken.ThrowIfCancellationRequested();
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 2 << 20)
                throw new EvidenceIndexException("checkpoint-response-limit");
            return result;
        }
        catch (AefStagingException error) { throw new EvidenceIndexException("snapshot-" + error.Code); }
        finally { foreach (var snapshot in snapshots) snapshot.Dispose(); }
    }
}
