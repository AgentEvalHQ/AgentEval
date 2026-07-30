// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The <b>one canonical gate-evidence record</b> (Phase 3, F-C) that every Gatekeeper trace writer now projects,
/// replacing the divergent per-writer metadata dictionaries. A single shape carries the full "reconstructable why":
/// which gate fired, at which seam, against which tool/agent/iteration, when, how severe, and the
/// <see cref="GateProvenance"/> behind a threshold decision. It has exactly two projections and NEVER a parallel
/// structure: <see cref="ToMetadata"/> (the audit-visible trace value, a superset that stays compatible with
/// <c>GateMetadataReader</c>/<c>GlassBoxEvidence</c> because it keeps the <c>action</c> vocabulary and the
/// <c>gate.{stage}.{seq}.{policy}</c> key grammar), and — later, P4-1 — the versioned model-facing refusal envelope
/// derived from the same fields.
/// </summary>
public sealed record GateEvidence
{
    /// <summary>Short, opaque, correlatable id — echoed in structured refusals, withheld from camouflaged refusals, and looked up by the reference ledger (P3-1).</summary>
    public required string ReferenceId { get; init; }

    /// <summary>When the finding was recorded (UTC).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The seam: <c>tool</c> / <c>tool-result</c> / <c>run-pre</c> / <c>run-post</c> / <c>session</c> / <c>warning</c> / <c>approval</c>.</summary>
    public required string Stage { get; init; }

    /// <summary>The gate's stable policy name (the key's policy segment).</summary>
    public required string Policy { get; init; }

    /// <summary>What happened: <c>Block</c> / <c>Warn</c> / <c>Mutate</c> / <c>Redact</c> / <c>Reconcile</c> / <c>RequireApproval</c>. Only <c>Block</c> counts as an enforced block (readers key on this).</summary>
    public required string Action { get; init; }

    /// <summary>Triage severity, stamped at record time (P3-5).</summary>
    public GateSeverity Severity { get; init; } = GateSeverity.Routine;

    /// <summary>The run this finding belongs to (<see cref="AgentRunScope.RunId"/>), if a run scope was established.</summary>
    public string? RunId { get; init; }

    /// <summary>The invoking agent's name, if known.</summary>
    public string? AgentName { get; init; }

    /// <summary>The tool the finding concerns (tool/result seams), if any.</summary>
    public string? ToolName { get; init; }

    /// <summary>The function-calling iteration index (tool/result seams), if any.</summary>
    public int? Iteration { get; init; }

    /// <summary>Human-readable reason (audit-visible; never returned to the model). Already sensitive-axis-redacted by the writer when required.</summary>
    public string? Reason { get; init; }

    /// <summary>The reconstructable "why" behind a threshold decision (P3-3) — persisted here for the first time.</summary>
    public GateProvenance? Provenance { get; init; }

    /// <summary>Hash of the frozen gate list this run enforced (P3-6), so a record can be tied to the exact configuration that produced it.</summary>
    public string? ConfigFingerprint { get; init; }

    /// <summary>The ambient tool-call correlation id, if any (carries through the existing <c>correlationId</c> key).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Writer-specific fields that don't belong to the common schema (e.g. <c>matches</c>, <c>argsBefore/argsAfter</c>, <c>scrubbed</c>, <c>terminate</c>). Merged verbatim into the trace value.</summary>
    public IReadOnlyDictionary<string, object?>? Extra { get; init; }

    /// <summary>The trace key this evidence is written under: <c>gate.{stage}.{seq}.{policy}</c>.</summary>
    public string TraceKey(int seq) => $"gate.{Stage}.{seq}.{Policy}";

    /// <summary>
    /// The audit-visible trace value — a SUPERSET dictionary that keeps the fields the existing readers parse
    /// (<c>action</c>, <c>reason</c>, <c>referenceId</c>, …) and adds the F-C fields. Null-valued optional fields
    /// are omitted so a record stays as lean as the pre-F-C writers where nothing new applies.
    /// </summary>
    public Dictionary<string, object?> ToMetadata()
    {
        var value = new Dictionary<string, object?>
        {
            ["action"] = Action,
            ["reason"] = Reason,
            ["referenceId"] = ReferenceId,
            ["severity"] = Severity.ToString(),
            ["tsUtc"] = TimestampUtc.ToString("O"),
        };

        AddIfPresent(value, "runId", RunId);
        AddIfPresent(value, "agentName", AgentName);
        AddIfPresent(value, "toolName", ToolName);
        if (Iteration is { } iteration)
        {
            value["iteration"] = iteration;
        }

        AddIfPresent(value, "configFingerprint", ConfigFingerprint);
        AddIfPresent(value, "correlationId", CorrelationId);
        if (Provenance is not null)
        {
            value["provenance"] = ProvenanceToValue(Provenance);
        }

        if (Extra is not null)
        {
            foreach (var kv in Extra)
            {
                value[kv.Key] = kv.Value;   // writer-specific fields win (e.g. an already-redacted matches)
            }
        }

        return value;
    }

    private static void AddIfPresent(Dictionary<string, object?> value, string key, string? item)
    {
        if (!string.IsNullOrEmpty(item))
        {
            value[key] = item;
        }
    }

    // Flatten provenance to plain nested dictionaries/lists so it round-trips through AgentTrace's JSON without
    // depending on GateProvenance's type at read time.
    private static Dictionary<string, object?> ProvenanceToValue(GateProvenance provenance)
    {
        var value = new Dictionary<string, object?>
        {
            ["ruleName"] = provenance.RuleName,
            ["evidence"] = provenance.Evidence,
        };
        if (provenance.Threshold is { } threshold)
        {
            value["threshold"] = threshold;
        }

        if (provenance.ActualValue is { } actual)
        {
            value["actualValue"] = actual;
        }

        if (provenance.Contributing is { Count: > 0 } contributing)
        {
            value["contributing"] = contributing.Select(ProvenanceToValue).ToList();
        }

        return value;
    }
}
