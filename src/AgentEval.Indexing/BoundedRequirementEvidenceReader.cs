// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;

namespace AgentEval.Indexing;

/// <summary>A detached verified governance snapshot and the work consumed to capture it.</summary>
public sealed record CapturedRequirementEvidence(RequirementEvidence Evidence, long CapturedBytes, int ResultRows);
/// <summary>Verifies governance inputs from bounded owned copies rather than mutable live directories.</summary>
public interface IBoundedRequirementEvidenceReader
{
    /// <summary>Uses at most maximumBytes of source files, with a finite file count; the caller owns the aggregate query budget.</summary>
    CapturedRequirementEvidence Read(string directory, string runHash, AefTime at, TrustPolicy? policy,
        long maximumBytes, CancellationToken cancellationToken = default);
}
/// <summary>Composes the existing requirement evidence verifier with the shared ordinary-file snapshot capture.</summary>
public sealed class BoundedRequirementEvidenceReader(IRequirementEvidenceReader reader) : IBoundedRequirementEvidenceReader
{
    private readonly IRequirementEvidenceReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    /// <inheritdoc />
    public CapturedRequirementEvidence Read(string directory, string runHash, AefTime at, TrustPolicy? policy,
        long maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1 or > (64L << 20)) throw new EvidenceIndexException("snapshot-limit");
        try
        {
            using var snapshot = AefReadSnapshot.Capture(directory, new AefStagingOptions
            { MaxBytes = maximumBytes, MaxFileBytes = maximumBytes }, cancellationToken);
            var evidence = _reader.Read(new(snapshot.FullName, runHash), at, policy);
            cancellationToken.ThrowIfCancellationRequested();
            return new(evidence, snapshot.Bytes, evidence.Documents.Results.Lines.Count);
        }
        catch (AefStagingException error) { throw new EvidenceIndexException("snapshot-" + error.Code); }
    }
}
