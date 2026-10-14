// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.Results;
using AgentEval.Results.Signatures;

namespace AgentEval.MissionControl.Core;

/// <summary>Verified source detail with the index revision used to select its seal; contains no physical location.</summary>
public sealed record SelectedResultDetail(string SourceId, string IndexInstanceId, long IndexGeneration, VerifiedResultDetail Result);
/// <summary>Shared source-ID based access to one verified result and its effective overlay state.</summary>
public interface IResultDetailQueryService
{
    /// <summary>Reads one exact result at an explicit UTC view time under the host-configured trust policy.</summary>
    QueryResult<SelectedResultDetail> Result(string sourceId, string resultId, AefTime at, CancellationToken cancellationToken = default);
}
/// <summary>Composes the shared configured location policy and bounded verifying detail reader.</summary>
public sealed class ResultDetailQueryService : IResultDetailQueryService
{
    private readonly ConfiguredAefSelection? _selection;
    private readonly IAefResultDetailReader? _reader;
    private readonly TrustPolicy? _policy;
    /// <summary>Creates an explicitly unconfigured capability without discovering storage.</summary>
    public ResultDetailQueryService() { }
    /// <summary>Configuration, including trust authority, is supplied by the host rather than by individual requests.</summary>
    public ResultDetailQueryService(IEvidenceSourceLookup lookup, IAefResultDetailReader reader,
        IEnumerable<EvidenceIndexRoot> roots, TrustPolicy? policy = null)
    {
        _selection = new(lookup, roots); _reader = reader ?? throw new ArgumentNullException(nameof(reader)); _policy = policy;
    }
    /// <inheritdoc />
    public QueryResult<SelectedResultDetail> Result(string sourceId, string resultId, AefTime at, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_selection is null) return new(QueryState.NotConfigured, null, "not-configured");
        if (!at.IsValid || string.IsNullOrWhiteSpace(resultId) || resultId.Length > 1024)
            return new(QueryState.InvalidRequest, null, "invalid-detail-request");
        try
        {
            var resolved = _selection.Resolve(sourceId, cancellationToken);
            if (resolved.Data is null) return new(resolved.State, null, resolved.Code);
            var location = resolved.Data;
            var detail = _reader!.Read(location.Directory, location.RunHash, resultId, at, _policy, cancellationToken);
            if (detail is null) return new(QueryState.NotFound, null, "result-not-found");
            return new(QueryState.Available, new(sourceId, location.Selection.InstanceId, location.Selection.Generation, detail), null);
        }
        catch (EvidenceIndexException error)
        {
            return error.Code switch
            {
                "invalid-page-request" or "invalid-detail-request" => new(QueryState.InvalidRequest, null, "invalid-detail-request"),
                "page-byte-limit" or "size-limit" or "snapshot-limit" => new(QueryState.ResourceLimit, null, "detail-byte-limit"),
                _ => new(QueryState.Unavailable, null, "evidence-unavailable")
            };
        }
        catch (FormatException) { return new(QueryState.Unavailable, null, "invalid-evidence"); }
        catch (IOException) { return new(QueryState.Unavailable, null, "evidence-unavailable"); }
        catch (UnauthorizedAccessException) { return new(QueryState.Unavailable, null, "evidence-unavailable"); }
    }
}
