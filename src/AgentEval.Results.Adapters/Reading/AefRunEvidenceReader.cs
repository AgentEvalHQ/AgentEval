// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>Verified, detached documents for presentation readers. No legacy metadata or result selection is invented.</summary>
public sealed class AefRunEvidence
{
    internal AefRunEvidence(AefVerifiedRun run)
    {
        var documents = run.Documents;
        Info = run.Info with { Asserted = Array.AsReadOnly(run.Info.Asserted.ToArray()) };
        Bytes = run.Bytes;
        Header = Copy(documents.Run!);
        Metrics = Copy(documents.Metrics!);
        Summary = documents.Summary is { } summary ? Copy(summary) : null;
        Results = Array.AsReadOnly(documents.Results.Objects.Select(line => Copy(line.Value)).ToArray());
        Gates = Array.AsReadOnly(documents.Gates.Objects.Select(line => Copy(line.Value)).ToArray());
        Evidence = Array.AsReadOnly(documents.Evidence.Objects.Select(line => Copy(line.Value)).ToArray());
        StartedAt = ObservedTime("startedAt");
        EndedAt = ObservedTime("endedAt");
    }

    /// <summary>Integrity of the captured container, with importer claims and signature trust kept separate.</summary>
    public AefRunReadInfo Info { get; }
    /// <summary>Bytes captured and verified, including linked blobs. Useful for a shared workspace read budget.</summary>
    public long Bytes { get; }
    /// <summary>The complete recorded header, including importer placeholders identified by <see cref="Info"/>.</summary>
    public JsonElement Header { get; }
    /// <summary>All metric definitions; score meanings must be read from these definitions.</summary>
    public JsonElement Metrics { get; }
    /// <summary>The recorded summary, or null for an unfinished run without one.</summary>
    public JsonElement? Summary { get; }
    /// <summary>Every result in recorded order, including trials, parents and every score dimension.</summary>
    public IReadOnlyList<JsonElement> Results { get; }
    /// <summary>Recorded gate decisions; completion alone never implies a passing gate.</summary>
    public IReadOnlyList<JsonElement> Gates { get; }
    /// <summary>Evidence descriptors. This read does not expose blob content through filesystem paths.</summary>
    public IReadOnlyList<JsonElement> Evidence { get; }
    /// <summary>Recorded execution start, or null when absent or supplied by the importer.</summary>
    public DateTimeOffset? StartedAt { get; }
    /// <summary>Recorded execution end, or null when absent or supplied by the importer.</summary>
    public DateTimeOffset? EndedAt { get; }
    /// <summary>Observed elapsed execution time; null if either boundary was not recorded.</summary>
    public TimeSpan? Duration => StartedAt is { } start && EndedAt is { } end ? end - start : null;

    private DateTimeOffset? ObservedTime(string field) =>
        !Info.Asserted.Contains(field, StringComparer.Ordinal) && Header.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    private static JsonElement Copy(JsonNode node) => JsonSerializer.SerializeToElement(node);
}

/// <summary>Reads all AEF result evidence from one bounded, verified snapshot.</summary>
public interface IAefRunEvidenceReader
{
    /// <summary>Reads an exact run directory. Invalid evidence, exhausted budgets and cancellation throw.</summary>
    AefRunEvidence Read(string directory, long maxBytes = 64L << 20, CancellationToken ct = default);
}

/// <summary>Shared presentation boundary for native runs and converted legacy runs.</summary>
public sealed class AefRunEvidenceReader : IAefRunEvidenceReader
{
    /// <inheritdoc />
    public AefRunEvidence Read(string directory, long maxBytes = 64L << 20, CancellationToken ct = default)
    {
        using var run = AefVerifiedRun.Open(directory, ct, maxBytes);
        ct.ThrowIfCancellationRequested();
        return new(run);
    }
}
