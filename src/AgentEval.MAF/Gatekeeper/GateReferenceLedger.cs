// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// An <see cref="IGateEvidenceSink"/> (Phase 3, P3-1) that indexes every gate finding into a
/// <see cref="GateVerdictResolver"/> for O(1) <c>referenceId</c> lookup, and — opt-in — appends every enforced
/// REFUSAL (an <c>action="Block"</c> record) as one JSON line to a caller-supplied index writer. Register it as a
/// Gatekeeper evidence sink so a refusal's opaque reference id can later be resolved back to the full record
/// (<c>agenteval trace find-reference &lt;id&gt;</c>) even when tracing is off — the resolver is fed here directly,
/// not from the trace.
/// </summary>
public sealed class GateReferenceLedger : IGateEvidenceSink
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TextWriter? _jsonlIndex;
    private readonly object _writeLock = new();

    /// <summary>Creates the ledger.</summary>
    /// <param name="resolver">The resolver every record is indexed into (a fresh bounded/TTL one if null).</param>
    /// <param name="jsonlIndex">Optional append-only JSONL sink for enforced refusals; null keeps the index in memory only.</param>
    public GateReferenceLedger(GateVerdictResolver? resolver = null, TextWriter? jsonlIndex = null)
    {
        Resolver = resolver ?? new GateVerdictResolver();
        _jsonlIndex = jsonlIndex;
    }

    /// <summary>The resolver this ledger feeds — call <see cref="GateVerdictResolver.TryResolve"/> on it to look a reference id up.</summary>
    public GateVerdictResolver Resolver { get; }

    /// <inheritdoc/>
    public void Record(GateEvidence evidence, int sequence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Resolver.Record(evidence);

        // Append only enforced REFUSALS to the durable index — the records an operator triages from a reference id.
        if (_jsonlIndex is not null && string.Equals(evidence.Action, "Block", StringComparison.Ordinal))
        {
            var line = JsonSerializer.Serialize(
                new GateReferenceIndexEntry(
                    evidence.ReferenceId, evidence.TimestampUtc, evidence.RunId, evidence.Policy, evidence.Stage,
                    evidence.ToolName, evidence.AgentName, evidence.Severity.ToString()),
                JsonOptions);
            lock (_writeLock)
            {
                _jsonlIndex.WriteLine(line);
                _jsonlIndex.Flush();
            }
        }
    }

    /// <summary>One line of the refusal index — the minimum an operator needs to correlate and triage a reference id.</summary>
    public sealed record GateReferenceIndexEntry(
        string ReferenceId,
        DateTimeOffset TsUtc,
        string? RunId,
        string Policy,
        string Stage,
        string? ToolName,
        string? AgentName,
        string Severity);
}
