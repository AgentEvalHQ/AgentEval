// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using AgentEval.Indexing;
using AgentEval.Results;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Signatures;

namespace AgentEval.MissionControl.Core;

/// <summary>A host-configured checkpoint whose exact bytes are privately retained for verification.</summary>
public sealed class ConfiguredCheckpoint
{
    private readonly byte[] _manifest;
    private readonly byte[]? _envelope;
    /// <summary>Validates and copies bounded configuration bytes; callers cannot replace host authority through a query.</summary>
    public ConfiguredCheckpoint(string id, ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte>? envelope = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl) ||
            manifest.Length > 1 << 20 || envelope?.Length > 1 << 20)
            throw new ArgumentException("Expected: a checkpoint ID of 1..128 characters and manifest/envelope at most 1 MiB. Actual: invalid configuration. Suggestions: provide bounded explicit checkpoint configuration. Because: queries must not infer storage or remove resource limits.");
        _manifest = manifest.ToArray(); _envelope = envelope?.ToArray();
        var document = CheckpointVerifier.Read(_manifest);
        if (document["lanes"]!.AsArray().Count > 64)
            throw new ArgumentException("Expected: at most 64 checkpoint lanes. Actual: too many lanes. Suggestions: reduce checkpoint scope. Because: query work is bounded.");
        var subject = CheckpointSubject.Read(document);
        Descriptor = new(id, document["checkpointId"]!.GetValue<string>(), subject.Ref, subject.Version,
            document["state"]!.GetValue<string>(), document["outcome"]?.GetValue<string>(),
            Convert.ToHexString(SHA256.HashData(_manifest)).ToLowerInvariant());
    }
    /// <summary>Path-free checkpoint declarations, not a verification result.</summary>
    public CheckpointDescriptor Descriptor { get; }
    internal ReadOnlyMemory<byte> Manifest => _manifest;
    internal ReadOnlyMemory<byte>? Envelope => _envelope is null ? null : new ReadOnlyMemory<byte>(_envelope);
}
/// <summary>Configured identity and declared checkpoint metadata; no approval or trust is inferred.</summary>
public sealed record CheckpointDescriptor(string Id, string CheckpointId, string SubjectRef, string SubjectVersion,
    string DeclaredState, string? DeclaredOutcome, string ManifestDigest);
/// <summary>Verified explanation with explicit source selection and its index revision, absent for an empty selection.</summary>
public sealed record SelectedCheckpointExplanation(string CheckpointId, IReadOnlyList<string> SourceIds,
    string? IndexInstanceId, long? IndexGeneration, CheckpointExplanation Explanation);

/// <summary>Read-only checkpoint discovery and verification under host-configured authority.</summary>
public interface ICheckpointQueryService
{
    /// <summary>Lists at most sixteen configured checkpoint descriptors.</summary>
    QueryResult<IReadOnlyList<CheckpointDescriptor>> List(CancellationToken cancellationToken = default);
    /// <summary>Verifies one configured checkpoint against explicit source IDs without discovering additional evidence.</summary>
    QueryResult<SelectedCheckpointExplanation> Explain(string checkpointId, IReadOnlyList<string> sourceIds,
        AefTime at, CancellationToken cancellationToken = default);
}

/// <summary>Uses the common source binding policy and bounded checkpoint reader, independent of requirements catalogues.</summary>
public sealed class CheckpointQueryService : ICheckpointQueryService
{
    private readonly ConfiguredAefSelection? _selection;
    private readonly IBoundedCheckpointReader? _reader;
    private readonly Dictionary<string, ConfiguredCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly TrustPolicy? _policy;
    /// <summary>Creates an explicitly unconfigured capability.</summary>
    public CheckpointQueryService() { }
    /// <summary>Accepts at most sixteen validated host checkpoints and configured evidence roots.</summary>
    public CheckpointQueryService(IEvidenceSourceLookup lookup, IBoundedCheckpointReader reader,
        IEnumerable<ConfiguredCheckpoint> checkpoints, IEnumerable<EvidenceIndexRoot> roots, TrustPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        var entries = checkpoints.Take(17).ToArray();
        if (entries.Length > 16 || entries.Select(c => c.Descriptor.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("Expected: at most sixteen distinct checkpoint IDs. Actual: invalid checkpoint list. Suggestions: remove duplicate IDs or reduce configuration. Because: checkpoint discovery must stay bounded.");
        _selection = new(lookup, roots); _reader = reader ?? throw new ArgumentNullException(nameof(reader)); _policy = policy;
        foreach (var checkpoint in entries) _checkpoints.Add(checkpoint.Descriptor.Id, checkpoint);
    }
    /// <inheritdoc />
    public QueryResult<IReadOnlyList<CheckpointDescriptor>> List(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_selection is null) return new(QueryState.NotConfigured, null, "not-configured");
        var items = _checkpoints.Values.Select(c => c.Descriptor).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (!QueryJsonBudget.Fits(items, 2 << 20)) return new(QueryState.ResourceLimit, null, "checkpoint-response-limit");
        return new(items.Length == 0 ? QueryState.Empty : QueryState.Available, items, null);
    }
    /// <inheritdoc />
    public QueryResult<SelectedCheckpointExplanation> Explain(string checkpointId, IReadOnlyList<string> sourceIds,
        AefTime at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (_selection is null) return new(QueryState.NotConfigured, null, "not-configured");
        if (!at.IsValid || string.IsNullOrWhiteSpace(checkpointId) || checkpointId.Length > 128)
            return new(QueryState.InvalidRequest, null, "invalid-checkpoint-request");
        if (!_checkpoints.TryGetValue(checkpointId, out var checkpoint)) return new(QueryState.NotFound, null, "checkpoint-not-found");
        if (sourceIds.Count > 8) return new(QueryState.ResourceLimit, null, "evidence-count-limit");
        var requested = sourceIds.Take(9).ToArray();
        if (requested.Length > 8) return new(QueryState.ResourceLimit, null, "evidence-count-limit");
        var ids = requested.Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            var selections = new List<CheckpointRunSelection>(); string? instance = null; long? generation = null;
            foreach (var id in ids)
            {
                var resolved = _selection.Resolve(id, cancellationToken);
                if (resolved.Data is null) return new(resolved.State, null, resolved.Code);
                var location = resolved.Data; var selected = location.Selection;
                if (instance is not null && (instance != selected.InstanceId || generation != selected.Generation))
                    return new(QueryState.StaleCursor, null, "selection-changed");
                instance = selected.InstanceId; generation = selected.Generation;
                selections.Add(new(location.Directory, location.RunHash));
            }
            var explanation = _reader!.Read(checkpoint.Manifest, checkpoint.Envelope, selections, at, _policy,
                cancellationToken: cancellationToken);
            var result = new SelectedCheckpointExplanation(checkpointId, ids, instance, generation, explanation);
            cancellationToken.ThrowIfCancellationRequested();
            if (!QueryJsonBudget.Fits(result, 2 << 20)) return new(QueryState.ResourceLimit, null, "checkpoint-response-limit");
            return new(QueryState.Available, result, null);
        }
        catch (EvidenceIndexException error)
        {
            return error.Code switch
            {
                "invalid-page-request" or "invalid-checkpoint-selection" => new(QueryState.InvalidRequest, null, "invalid-source-id"),
                "checkpoint-limit" or "checkpoint-response-limit" or "snapshot-limit" or "page-byte-limit" or "size-limit"
                    => new(QueryState.ResourceLimit, null, "checkpoint-resource-limit"),
                _ => new(QueryState.Unavailable, null, "checkpoint-evidence-unavailable")
            };
        }
        catch (FormatException) { return new(QueryState.Unavailable, null, "invalid-checkpoint-evidence"); }
        catch (IOException) { return new(QueryState.Unavailable, null, "checkpoint-evidence-unavailable"); }
        catch (UnauthorizedAccessException) { return new(QueryState.Unavailable, null, "checkpoint-evidence-unavailable"); }
    }
}
