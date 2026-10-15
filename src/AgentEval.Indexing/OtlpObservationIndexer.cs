// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Observations;

namespace AgentEval.Indexing;

/// <summary>A source-scoped import refusal, containing stable identifiers and codes rather than filesystem paths.</summary>
public sealed record ObservationIndexIssue(string RecordId, string Code);
/// <summary>Import coverage of one captured inventory. Partial scans never imply deletion of absent sources.</summary>
public sealed record ObservationIndexImport(int InventoryRecords, int Processed, int Changed, int Unchanged,
    bool Complete, ImmutableArray<ObservationIndexIssue> Issues);
/// <summary>Indexes verified immutable observation records without promoting them to evaluation outcomes.</summary>
public interface IOtlpObservationIndexer
{
    /// <summary>Reads each selected record through the store's digest verification and commits each projection independently.</summary>
    ObservationIndexImport Import(string rootId, IOtlpObservationStore store, int maximumRecords = 4096,
        CancellationToken cancellationToken = default);
}
/// <summary>Bounded initial/incremental OTLP import. Stored mapping/content provenance is used without re-normalization.</summary>
public sealed class OtlpObservationIndexer(IEvidenceIndexWriter writer) : IOtlpObservationIndexer
{
    /// <inheritdoc />
    public ObservationIndexImport Import(string rootId, IOtlpObservationStore store, int maximumRecords = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maximumRecords is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumRecords),
            "Expected: 1..100000 observation records. Actual: invalid scan budget. Suggestions: select a finite budget. Because: imports must remain bounded.");
        // Validate the source namespace even for an empty inventory, without creating an index row.
        writer.Find(rootId, ".", cancellationToken);
        var inventory = store.List(cancellationToken);
        var issues = ImmutableArray.CreateBuilder<ObservationIndexIssue>();
        var changed = 0; var unchanged = 0; var processed = 0;
        foreach (var entry in inventory.Take(maximumRecords))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stop = false;
            try
            {
                if (entry.Id.Length != 70 || !entry.Id.StartsWith("batch-", StringComparison.Ordinal))
                    throw new EvidenceIndexException("invalid-observation-id");
                var record = store.Read(entry.Id, cancellationToken);
                var result = writer.Upsert(new(rootId, "records/" + entry.Id + ".json", entry.Id[6..],
                    IndexedEvidenceKind.Observation, IndexedVerification.Unverified, null, [], [], record), cancellationToken);
                if (result.Changed) changed++; else unchanged++;
            }
            catch (OtlpIngestionException error) { issues.Add(new(entry.Id, error.Code)); stop = error.Code == "busy"; }
            catch (EvidenceIndexException error)
            {
                issues.Add(new(entry.Id, error.Code));
                stop = error.Code is "source-limit" or "total-row-limit" or "size-limit" or "busy";
            }
            catch (IOException) { issues.Add(new(entry.Id, "source-unavailable")); }
            catch (UnauthorizedAccessException) { issues.Add(new(entry.Id, "source-unavailable")); }
            processed++;
            if (stop) break;
        }
        return new(inventory.Length, processed, changed, unchanged, processed == inventory.Length && issues.Count == 0, issues.ToImmutable());
    }
}
