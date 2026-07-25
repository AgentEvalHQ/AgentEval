// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Reads the durable, cross-run refusal index a <see cref="GateReferenceLedger"/> appends (Phase 3, P3-8) and
/// aggregates it — block counts by tool / policy / config-fingerprint / severity / day — so an operator can see
/// block-rate-by-tool-over-time trends across many runs. A precondition for the Bulkhead security graph. Pure and
/// offline: it consumes the JSONL an earlier run wrote, so it needs neither a live run nor the trace.
/// </summary>
public static class GateReferenceIndexAggregator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Parses the JSONL refusal index from <paramref name="reader"/>, skipping blank or malformed lines.</summary>
    public static IReadOnlyList<GateReferenceLedger.GateReferenceIndexEntry> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var entries = new List<GateReferenceLedger.GateReferenceIndexEntry>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            GateReferenceLedger.GateReferenceIndexEntry? entry = null;
            try
            {
                entry = JsonSerializer.Deserialize<GateReferenceLedger.GateReferenceIndexEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // A corrupt/partial line must not sink the whole aggregation — skip it (best-effort offline read).
            }

            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Aggregates <paramref name="entries"/> into cross-run block-rate breakdowns.</summary>
    public static GateBlockAggregation Aggregate(IEnumerable<GateReferenceLedger.GateReferenceIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var total = 0;
        var byPolicy = new Dictionary<string, int>(StringComparer.Ordinal);
        var byTool = new Dictionary<string, int>(StringComparer.Ordinal);
        var byConfig = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal);
        var byDay = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            total++;
            Bump(byPolicy, entry.Policy);
            Bump(byTool, entry.ToolName ?? "(none)");
            Bump(byConfig, entry.ConfigFingerprint ?? "(none)");
            Bump(bySeverity, entry.Severity);
            Bump(byDay, entry.TsUtc.ToUniversalTime().ToString("yyyy-MM-dd"));
        }

        return new GateBlockAggregation(total, byPolicy, byTool, byConfig, bySeverity, byDay);
    }

    /// <summary>Reads and aggregates in one call.</summary>
    public static GateBlockAggregation ReadAndAggregate(TextReader reader) => Aggregate(Read(reader));

    private static void Bump(Dictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
}

/// <summary>Cross-run refusal aggregation (P3-8): total blocks plus breakdowns by policy, tool, config fingerprint, severity, and UTC day.</summary>
/// <param name="Total">Total refusals in the index.</param>
/// <param name="ByPolicy">Block count per gate policy.</param>
/// <param name="ByTool">Block count per tool (<c>"(none)"</c> for run-seam blocks with no tool).</param>
/// <param name="ByConfigFingerprint">Block count per gate-config fingerprint — the "which configuration produced these" axis.</param>
/// <param name="BySeverity">Block count per severity band.</param>
/// <param name="ByDay">Block count per UTC day (<c>yyyy-MM-dd</c>) — the over-time trend.</param>
public sealed record GateBlockAggregation(
    int Total,
    IReadOnlyDictionary<string, int> ByPolicy,
    IReadOnlyDictionary<string, int> ByTool,
    IReadOnlyDictionary<string, int> ByConfigFingerprint,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByDay);
