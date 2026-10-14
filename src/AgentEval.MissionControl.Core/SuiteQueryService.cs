// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.Results.Suites;

namespace AgentEval.MissionControl.Core;

/// <summary>Verified sealed identity plus the index revision used to select it. Contains no physical location.</summary>
public sealed record VerifiedSuiteEvidence(string SourceId, string IndexInstanceId, long IndexGeneration, SuiteIdentity Identity);
/// <summary>Strict identity compatibility; completeness, subject eligibility and statistical comparison remain separate.</summary>
public sealed record VerifiedSuiteComparison(VerifiedSuiteEvidence Baseline, VerifiedSuiteEvidence Candidate, SuiteCompatibility Compatibility);
/// <summary>Queries sealed suite evidence selected by source ID, never by a caller-supplied filesystem path.</summary>
public interface ISuiteQueryService
{
    /// <summary>Re-verifies a selected sealed run against the indexed run hash under an explicitly configured root.</summary>
    QueryResult<VerifiedSuiteEvidence> Identity(string sourceId, CancellationToken cancellationToken = default);
    /// <summary>Compares verified identities selected from the same index revision, without claiming score eligibility.</summary>
    QueryResult<VerifiedSuiteComparison> Compare(string baselineSourceId, string candidateSourceId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves persisted bindings against trusted configuration before invoking the verifying identity reader.</summary>
public sealed class SuiteQueryService : ISuiteQueryService
{
    private readonly ConfiguredAefSelection _selection;
    private readonly ISuiteIdentityReader _reader;
    private readonly ISuiteCompatibilityEvaluator _compatibility;

    /// <summary>Validates a bounded configured root set. Configuration grants access; indexed bindings alone do not.</summary>
    public SuiteQueryService(IEvidenceSourceLookup lookup, ISuiteIdentityReader reader,
        ISuiteCompatibilityEvaluator compatibility, IEnumerable<EvidenceIndexRoot> roots)
    {
        _selection = new(lookup, roots);
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
    }

    /// <inheritdoc />
    public QueryResult<VerifiedSuiteEvidence> Identity(string sourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var resolved = _selection.Resolve(sourceId, cancellationToken);
            if (resolved.Data is null) return new(resolved.State, null, resolved.Code);
            var selected = resolved.Data.Selection;
            var identity = _reader.Read(resolved.Data.Directory, resolved.Data.RunHash);
            cancellationToken.ThrowIfCancellationRequested();
            return new(QueryState.Available, new(sourceId, selected.InstanceId, selected.Generation, identity), null);
        }
        catch (EvidenceIndexException error)
        {
            return error.Code switch
            {
                "invalid-page-request" => new(QueryState.InvalidRequest, null, "invalid-source-id"),
                "page-byte-limit" => new(QueryState.ResourceLimit, null, "page-byte-limit"),
                "size-limit" => new(QueryState.ResourceLimit, null, "index-size-limit"),
                _ => new(QueryState.Unavailable, null, "evidence-unavailable")
            };
        }
        catch (FormatException) { return new(QueryState.Unavailable, null, "invalid-sealed-evidence"); }
        catch (IOException) { return new(QueryState.Unavailable, null, "evidence-unavailable"); }
        catch (UnauthorizedAccessException) { return new(QueryState.Unavailable, null, "evidence-unavailable"); }
    }

    /// <inheritdoc />
    public QueryResult<VerifiedSuiteComparison> Compare(string baselineSourceId, string candidateSourceId, CancellationToken cancellationToken = default)
    {
        var baseline = Identity(baselineSourceId, cancellationToken);
        if (baseline.Data is null) return new(baseline.State, null, baseline.Code);
        var candidate = baselineSourceId == candidateSourceId ? baseline : Identity(candidateSourceId, cancellationToken);
        if (candidate.Data is null) return new(candidate.State, null, candidate.Code);
        if (baseline.Data.IndexInstanceId != candidate.Data.IndexInstanceId || baseline.Data.IndexGeneration != candidate.Data.IndexGeneration)
            return new(QueryState.StaleCursor, null, "selection-changed");
        cancellationToken.ThrowIfCancellationRequested();
        var compatibility = _compatibility.Compare(baseline.Data.Identity, candidate.Data.Identity);
        return new(QueryState.Available, new(baseline.Data, candidate.Data, compatibility), null);
    }
}
