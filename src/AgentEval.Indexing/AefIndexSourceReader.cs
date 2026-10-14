// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Suites;

namespace AgentEval.Indexing;

/// <summary>Captures and validates an AEF folder before projecting scalar index fields.</summary>
public interface IAefIndexSourceReader
{
    /// <summary>Returns a detached projection of verified snapshot bytes; valid unsealed runs remain explicitly unverified.</summary>
    EvidenceIndexSource Read(string rootId, string relativePath, string directory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses a bounded private disk copy so verification and projection see the same bytes. Does not apply
/// trust-policy overlays or infer an overall run verdict from individual result states.
/// </summary>
public sealed class AefIndexSourceReader : IAefIndexSourceReader
{
    private readonly AefStagingOptions _limits;
    private readonly ISuiteIdentityReader _identity;

    /// <summary>Creates a reader with existing AEF file/byte budgets and a verifying suite-identity reader.</summary>
    public AefIndexSourceReader(ISuiteIdentityReader identity, AefStagingOptions? limits = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _limits = limits ?? new(); _limits.Validate();
    }

    /// <inheritdoc />
    public EvidenceIndexSource Read(string rootId, string relativePath, string directory, CancellationToken cancellationToken = default)
    {
        try
        {
            using var snapshot = AefReadSnapshot.Capture(directory, _limits, cancellationToken);
            var folder = AefRunFolder.Open(snapshot.FullName);
            var verification = AefRunVerifier.Verify(folder);
            cancellationToken.ThrowIfCancellationRequested();
            if (verification.Outcome == AefOutcome.Invalid) throw new EvidenceIndexException("invalid-aef");
            var documents = AefRunDocuments.Read(folder);
            if (!documents.AllRead || documents.Run is null || OverlayChain.Verify(folder, documents).Problems.Count != 0)
                throw new EvidenceIndexException("invalid-aef");
            var digest = AefRunFinalizer.SnapshotDigest(folder, cancellationToken);
            var identity = verification.Outcome == AefOutcome.Intact ? _identity.Read(snapshot.FullName, verification.RunHash!.Value.Value) : null;
            var run = documents.Run;
            var results = ImmutableArray.CreateBuilder<IndexedResult>();
            var metrics = ImmutableArray.CreateBuilder<IndexedMetric>();
            foreach (var line in documents.Results.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = line.Value;
                var resultId = AefNode.String(result["resultId"])!;
                var trial = AefNode.Number(result["trial"]);
                results.Add(new(resultId, AefNode.String(result["caseId"]), trial.HasValue ? checked((long)trial.Value) : null, AefNode.String(result["state"])));
                var ordinal = 0;
                foreach (var score in (result["scores"] as JsonArray ?? []))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    metrics.Add(new(resultId, AefNode.String(score?["metric"])!, ordinal++, AefNode.Number(score?["value"]), null,
                        null, "aef-base-reported:" + (AefNode.String(result["annotator"]?["kind"]) ?? "unreported")));
                }
            }
            // Completed producer execution is not proof that a planned suite was fully measured.
            var completeness = documents.Status is "aborted" or "running" ? "partial" : "unknown";
            var projection = new IndexedRun(documents.RunId!, identity?.RunHash, AefNode.String(run["subject"]?["ref"]),
                AefNode.String(run["suite"]?["ref"]), identity?.Definition?.Digest, AefNode.String(run["startedAt"]), null, completeness);
            cancellationToken.ThrowIfCancellationRequested();
            return new(rootId, relativePath, digest, IndexedEvidenceKind.Aef,
                verification.Outcome == AefOutcome.Intact ? IndexedVerification.Intact : IndexedVerification.Unverified,
                projection, results.ToImmutable(), metrics.ToImmutable(), null);
        }
        catch (AefStagingException error) { throw new EvidenceIndexException("snapshot-" + error.Code); }
        catch (FormatException) { throw new EvidenceIndexException("invalid-aef-identity"); }
    }
}
