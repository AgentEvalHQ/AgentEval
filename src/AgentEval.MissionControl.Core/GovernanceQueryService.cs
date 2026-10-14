// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.Results;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;

namespace AgentEval.MissionControl.Core;

/// <summary>Shared governance computation over explicitly selected source IDs.</summary>
public interface IGovernanceQueryService
{
    /// <summary>Resolves configured requirements for an exact subject/version and time; never discovers a latest run implicitly.</summary>
    QueryResult<RequirementStatusReport> Status(RequirementSubject subject, IReadOnlyList<string> sourceIds,
        AefTime at, CancellationToken cancellationToken = default);
}
/// <summary>Uses host-configured catalogue/trust authority, bounded verified evidence and the existing pure status resolver.</summary>
public sealed class GovernanceQueryService : IGovernanceQueryService
{
    private readonly ConfiguredAefSelection? _selection;
    private readonly IBoundedRequirementEvidenceReader? _reader;
    private readonly IRequirementStatusResolver? _resolver;
    private readonly RequirementCatalogue? _catalogue;
    private readonly TrustPolicy? _policy;
    private readonly GovernanceQueryLimits _limits = new();

    /// <summary>Creates an explicitly unconfigured capability with no inferred catalogue or storage.</summary>
    public GovernanceQueryService() { }

    /// <summary>Accepts explicit host configuration and injected verification/status services.</summary>
    public GovernanceQueryService(IEvidenceSourceLookup lookup, IBoundedRequirementEvidenceReader reader,
        IRequirementStatusResolver resolver, RequirementCatalogue catalogue, IEnumerable<EvidenceIndexRoot> roots, TrustPolicy? policy = null,
        GovernanceQueryLimits? limits = null)
    {
        _selection = new(lookup, roots); _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue)); _policy = policy;
        _limits = limits ?? new(); _limits.Validate();
    }

    /// <inheritdoc />
    public QueryResult<RequirementStatusReport> Status(RequirementSubject subject, IReadOnlyList<string> sourceIds,
        AefTime at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject); ArgumentNullException.ThrowIfNull(sourceIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (_selection is null) return new(QueryState.NotConfigured, null, "not-configured");
        if (!at.IsValid || string.IsNullOrWhiteSpace(subject.Ref) || string.IsNullOrWhiteSpace(subject.Version) ||
            subject.Ref.Length > 1024 || subject.Version.Length > 1024)
            return new(QueryState.InvalidRequest, null, "invalid-governance-request");
        if (sourceIds.Count > _limits.MaxSources) return new(QueryState.ResourceLimit, null, "evidence-count-limit");
        var selectedIds = sourceIds.Take(_limits.MaxSources + 1).ToArray();
        if (selectedIds.Length > _limits.MaxSources) return new(QueryState.ResourceLimit, null, "evidence-count-limit");
        try
        {
            var evidence = new List<RequirementEvidence>(); long remaining = _limits.MaxCaptureBytes; long rows = 0;
            string? instance = null; long generation = -1;
            foreach (var id in selectedIds.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = _selection.Resolve(id, cancellationToken);
                if (resolved.Data is null) return new(resolved.State, null, resolved.Code);
                var location = resolved.Data; var selected = location.Selection;
                if (instance is not null && (instance != selected.InstanceId || generation != selected.Generation))
                    return new(QueryState.StaleCursor, null, "selection-changed");
                instance = selected.InstanceId; generation = selected.Generation;
                if (remaining == 0) return new(QueryState.ResourceLimit, null, "evidence-byte-limit");
                var captured = _reader!.Read(location.Directory, location.RunHash, at, _policy, remaining, cancellationToken);
                if (captured.CapturedBytes < 0 || captured.CapturedBytes > remaining || captured.ResultRows < 0)
                    throw new InvalidOperationException("Expected: bounded capture accounting. Actual: invalid reader accounting. Suggestions: correct the evidence reader. Because: query budgets must hold across adapters.");
                remaining -= captured.CapturedBytes; rows += captured.ResultRows;
                if (rows * _catalogue!.Requirements.Count > _limits.MaxRowRulePairs)
                    return new(QueryState.ResourceLimit, null, "governance-work-limit");
                evidence.Add(captured.Evidence);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var report = _resolver!.Resolve(_catalogue!, subject, evidence, at, RequirementEvidenceReader.PolicyDigest(_policy));
            cancellationToken.ThrowIfCancellationRequested();
            if (!QueryJsonBudget.Fits(report, _limits.MaxResponseBytes))
                return new(QueryState.ResourceLimit, null, "governance-response-limit");
            return new(QueryState.Available, report, null);
        }
        catch (EvidenceIndexException error)
        {
            return error.Code switch
            {
                "invalid-page-request" => new(QueryState.InvalidRequest, null, "invalid-source-id"),
                "snapshot-limit" or "page-byte-limit" or "size-limit" => new(QueryState.ResourceLimit, null, "evidence-byte-limit"),
                _ => new(QueryState.Unavailable, null, "evidence-unavailable")
            };
        }
        catch (FormatException) { return new(QueryState.Unavailable, null, "invalid-governance-evidence"); }
        catch (IOException) { return new(QueryState.Unavailable, null, "evidence-unavailable"); }
        catch (UnauthorizedAccessException) { return new(QueryState.Unavailable, null, "evidence-unavailable"); }
    }
}
