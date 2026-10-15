// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Indexing;

/// <summary>Bounds legacy projection input. Unrelated traces and reports are not consumed.</summary>
public sealed class LegacyIndexReadLimits
{
    /// <summary>Maximum scenario directory entries inspected, default 4096, range 1..100000.</summary>
    public int MaxScenarioEntries { get; init; } = 4096;
    /// <summary>Maximum bytes in any consumed file, default 16 MiB, range 1..64 MiB.</summary>
    public int MaxFileBytes { get; init; } = 16 << 20;
    /// <summary>Maximum aggregate consumed bytes, default 64 MiB, range 1..256 MiB.</summary>
    public int MaxTotalBytes { get; init; } = 64 << 20;
    /// <summary>Maximum combined result and metric rows, default 100000, range 1..1000000.</summary>
    public int MaxProjectedRows { get; init; } = 100000;
}

/// <summary>Reads detached, bounded legacy output-store documents into explicitly unverified index fields.</summary>
public interface ILegacyIndexSourceReader
{
    /// <summary>Projects manifest, optional summary and immediate scenario JSON files from one legacy run directory.</summary>
    EvidenceIndexSource Read(string rootId, string relativePath, string directory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects schemaVersion 1.0 output-store envelopes. The snapshot digest binds only consumed files;
/// it is not the legacy contentHash, an AEF seal, or a claim of filesystem snapshot atomicity.
/// Raw scenario input/output and evaluation trees are not indexed.
/// </summary>
public sealed class LegacyIndexSourceReader : ILegacyIndexSourceReader
{
    private readonly LegacyIndexReadLimits _limits;

    /// <summary>Creates a legacy reader with explicit file, aggregate byte and directory-entry budgets.</summary>
    public LegacyIndexSourceReader(LegacyIndexReadLimits? limits = null)
    {
        _limits = limits ?? new();
        if (_limits.MaxScenarioEntries is < 1 or > 100000 || _limits.MaxFileBytes is < 1 or > (64 << 20) ||
            _limits.MaxTotalBytes is < 1 or > (256 << 20) || _limits.MaxFileBytes > _limits.MaxTotalBytes ||
            _limits.MaxProjectedRows is < 1 or > 1000000)
            throw new EvidenceIndexException("invalid-legacy-limits");
    }

    /// <inheritdoc />
    public EvidenceIndexSource Read(string rootId, string relativePath, string directory, CancellationToken cancellationToken = default)
    {
        // Shared root validation rejects linked ancestry and relative/missing paths.
        var root = new EvidenceIndexRootPlanner().Plan([new(rootId, directory, IndexedEvidenceKind.Legacy)], cancellationToken: cancellationToken).ScanRoots[0];
        directory = root.Directory;
        long bytes = 0;
        var consumed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var results = ImmutableArray.CreateBuilder<IndexedResult>();
        var metrics = ImmutableArray.CreateBuilder<IndexedMetric>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        JsonDocument? Load(string relative, bool optional = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            AefEntryKind kind;
            try { _ = File.GetAttributes(path); kind = AefFolder.KindOf(path); }
            catch (Exception error) when (optional && (error is FileNotFoundException or DirectoryNotFoundException)) { return null; }
            if (kind != AefEntryKind.File) throw new EvidenceIndexException("unsafe-legacy-file");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = input.Length;
            if (length > _limits.MaxFileBytes || length > _limits.MaxTotalBytes - bytes)
                throw new EvidenceIndexException("legacy-byte-limit");
            var data = new byte[checked((int)length)];
            var offset = 0;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(data, offset, Math.Min(64 << 10, data.Length - offset));
                if (read == 0) throw new EvidenceIndexException("source-changed");
                offset += read;
            }
            if (input.ReadByte() != -1) throw new EvidenceIndexException("source-changed");
            bytes += length;
            consumed.Add(relative, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
            var document = JsonDocument.Parse(data, new() { MaxDepth = 64 });
            try { UniqueProperties(document.RootElement, cancellationToken); return document; }
            catch { document.Dispose(); throw; }
        }
        try
        {
            using var manifest = Load("manifest.json")!;
            var m = manifest.RootElement;
            if (Text(m, "schemaVersion") != "1.0") throw new EvidenceIndexException("unsupported-legacy-version");
            var run = Object(m, "run"); var subject = Object(m, "subject");
            var runId = Text(run, "runId"); var verdict = Verdict(run);
            var declaredCount = NonnegativeInteger(run, "scenarioCount");
            var subjectKind = Text(subject, "kind");
            if (subjectKind is not ("agent" or "workflow")) throw new EvidenceIndexException("invalid-legacy");
            var subjectId = subjectKind + ":" + Text(subject, "name");
            var timestamp = Text(run, "timestamp");
            if (!DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)) throw new EvidenceIndexException("invalid-legacy");
            using var summary = Load("summary.json", optional: true);
            if (summary is not null)
            {
                var s = summary.RootElement;
                if (Text(s, "schemaVersion") != "1.0" || Text(s, "runId") != runId || Verdict(s) != verdict)
                    throw new EvidenceIndexException("inconsistent-legacy-summary");
                if (NonnegativeInteger(Object(s, "stats"), "total") != declaredCount)
                    throw new EvidenceIndexException("inconsistent-legacy-summary");
                // This reserved row is an aggregate container, never a scenario: CaseId and Trial stay null.
                CheckRows(); results.Add(new("legacy:summary", null, null, verdict));
                AddMetrics(Object(s, "metrics"), "legacy:summary", "legacy-run-summary", metrics);
            }
            var scenarioDirectory = Path.Combine(directory, "scenarios");
            AefEntryKind? scenarioKind = null;
            try { _ = File.GetAttributes(scenarioDirectory); scenarioKind = AefFolder.KindOf(scenarioDirectory); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            if (scenarioKind is not (null or AefEntryKind.Folder)) throw new EvidenceIndexException("unsafe-legacy-file");
            var entries = 0;
            if (scenarioKind == AefEntryKind.Folder)
            foreach (var path in Directory.EnumerateFileSystemEntries(scenarioDirectory, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > _limits.MaxScenarioEntries) throw new EvidenceIndexException("legacy-entry-limit");
                if (AefFolder.KindOf(path) != AefEntryKind.File) throw new EvidenceIndexException("unsafe-legacy-file");
                if (!Path.GetFileName(path).EndsWith(".json", StringComparison.Ordinal)) continue;
                using var document = Load("scenarios/" + Path.GetFileName(path))!;
                var scenario = document.RootElement;
                var id = Text(scenario, "id");
                if (!ids.Add(id)) throw new EvidenceIndexException("duplicate-legacy-scenario");
                var resultId = "legacy:scenario:" + id;
                var passed = scenario.GetProperty("passed").GetBoolean();
                CheckRows(); results.Add(new(resultId, id, null, passed ? "PASS" : "FAIL"));
                AddMetrics(Object(scenario, "metrics"), resultId, "legacy-scenario-metric", metrics);
                CheckRows();
                metrics.Add(new(resultId, "legacy:scenario-score", int.MaxValue, Number(scenario.GetProperty("score")), null, passed, "legacy-scenario-score"));
            }
            var completeness = verdict == "PENDING" || summary is null || ids.Count != declaredCount ? "partial" : "unknown";
            var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { format = "legacy-index-inputs/1", files = consumed }))).ToLowerInvariant();
            return new(rootId, relativePath, digest, IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
                new(runId, null, subjectId, null, null, timestamp, verdict, completeness), results.ToImmutable(), metrics.ToImmutable(), null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new EvidenceIndexException("invalid-legacy"); }

        void CheckRows()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count + metrics.Count >= _limits.MaxProjectedRows) throw new EvidenceIndexException("legacy-row-limit");
        }
        void AddMetrics(JsonElement values, string resultId, string provenance, ImmutableArray<IndexedMetric>.Builder destination)
        {
            var ordinal = 0;
            foreach (var metric in values.EnumerateObject())
            {
                CheckRows();
                if (string.IsNullOrWhiteSpace(metric.Name)) throw new EvidenceIndexException("invalid-legacy");
                destination.Add(new(resultId, metric.Name, ordinal++, Number(metric.Value), null, null, provenance));
            }
        }
    }

    private static JsonElement Object(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind != JsonValueKind.Object) throw new EvidenceIndexException("invalid-legacy");
        return item;
    }
    private static string Text(JsonElement value, string name)
    {
        var text = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new EvidenceIndexException("invalid-legacy");
        return text;
    }
    private static long NonnegativeInteger(JsonElement value, string name)
    {
        var number = value.GetProperty(name).GetInt64();
        if (number < 0) throw new EvidenceIndexException("invalid-legacy");
        return number;
    }
    private static string Verdict(JsonElement value)
    {
        var verdict = Text(value, "verdict");
        if (verdict is not ("PASS" or "FAIL" or "WARN" or "PENDING")) throw new EvidenceIndexException("invalid-legacy");
        return verdict;
    }
    private static double Number(JsonElement value)
    {
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new EvidenceIndexException("invalid-legacy");
        return number;
    }
    private static void UniqueProperties(JsonElement value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new EvidenceIndexException("ambiguous-legacy-json");
                UniqueProperties(property.Value, cancellationToken);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item, cancellationToken);
    }
}
