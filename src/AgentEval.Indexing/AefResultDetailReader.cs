// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;
using AgentEval.Results;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Requirements;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Indexing;

/// <summary>One sealed result and its verified effective state; overlay effects never rewrite sealed JSON.</summary>
public sealed record VerifiedResultDetail(string RunHash, string SnapshotDigest, string PolicyDigest, string At,
    string ResultId, string SealedJson, string SealedState, string EffectiveState, string? OverrideEvent,
    ImmutableArray<EffectiveReview> Reviews, ImmutableArray<EffectiveAssurance> Assurance, int UnsealedEvents);
/// <summary>Reads exact result detail from a bounded owned copy, including verified overlay effects.</summary>
public interface IAefResultDetailReader
{
    /// <summary>Returns null only when the result is absent from intact selected evidence. Directory is server configuration, never remote input.</summary>
    VerifiedResultDetail? Read(string directory, string expectedRunHash, string resultId, AefTime at,
        TrustPolicy? policy = null, CancellationToken cancellationToken = default);
}

/// <summary>Shares bounded snapshot capture with indexing and retains the original result alongside overlay-derived state.</summary>
public sealed class AefResultDetailReader : IAefResultDetailReader
{
    private readonly AefStagingOptions _limits;
    /// <summary>Uses the existing finite AEF file/byte capture limits; result responses additionally cap text at 2 MiB.</summary>
    public AefResultDetailReader(AefStagingOptions? limits = null) { _limits = limits ?? new(); _limits.Validate(); }

    /// <inheritdoc />
    public VerifiedResultDetail? Read(string directory, string expectedRunHash, string resultId, AefTime at,
        TrustPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!at.IsValid || string.IsNullOrWhiteSpace(resultId) || resultId.Length > 1024 ||
            expectedRunHash is not { Length: 64 } || expectedRunHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new EvidenceIndexException("invalid-detail-request");
        try
        {
            using var snapshot = AefReadSnapshot.Capture(directory, _limits, cancellationToken);
            var folder = AefRunFolder.Open(snapshot.FullName);
            var documents = AefRunDocuments.Read(folder);
            var verified = AefRunVerifier.Verify(folder, new AefVerifyOptions { Policy = policy });
            if (verified.Outcome != AefOutcome.Intact || verified.RunHash?.Value != expectedRunHash)
                throw new EvidenceIndexException("invalid-sealed-evidence");
            var chain = OverlayChain.Verify(folder, documents);
            // A well-formed tail after the last sealed batch is visible as unsealed, never effective.
            if (chain.Problems.Any(p => p.Path != "overlays/events.ndjson" || p.Code != "uncovered"))
                throw new EvidenceIndexException("invalid-overlay");
            cancellationToken.ThrowIfCancellationRequested();
            var result = documents.Results.Objects.Select(o => o.Value).SingleOrDefault(r => AefNode.String(r["resultId"]) == resultId);
            if (result is null) return null;
            var json = result.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > 2 << 20) throw new EvidenceIndexException("page-byte-limit");
            var view = EffectiveView.Compute(folder, documents, chain, at, policy);
            var changed = view.Results.SingleOrDefault(r => r.ResultId == resultId);
            var reviews = view.Reviews.Where(r => r.Target == "run" || r.Target == resultId).ToImmutableArray();
            var events = reviews.Select(r => r.Event).Append(changed?.Event).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var assurance = view.Assurance.Where(a => events.Contains(a.Event)).ToImmutableArray();
            var state = AefNode.String(result["state"])!;
            var digest = AefRunFinalizer.SnapshotDigest(folder, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var detail = new VerifiedResultDetail(expectedRunHash, digest, RequirementEvidenceReader.PolicyDigest(policy), at.ToString(), resultId,
                json, state, changed?.EffectiveState ?? state, changed?.Event, reviews, assurance, view.UnsealedEvents);
            if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(detail).Length > 2 << 20)
                throw new EvidenceIndexException("page-byte-limit");
            return detail;
        }
        catch (AefStagingException error) { throw new EvidenceIndexException("snapshot-" + error.Code); }
    }
}
