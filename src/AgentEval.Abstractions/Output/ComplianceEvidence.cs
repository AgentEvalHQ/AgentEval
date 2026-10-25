// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AgentEval.Output;

/// <summary>Schema-mirroring record for a compliance <c>evidence.json</c> file.</summary>
public sealed record ComplianceEvidence(
    string SchemaVersion,
    string Regulation,
    SubjectIdentity Subject,
    DateTimeOffset GeneratedAt,
    SourceRunRef SourceRun,
    IReadOnlyList<EvidenceControl> Controls,
    EvidenceSummary Summary,
    Attestation Attestation);

/// <summary>Reference to the source run used to generate compliance evidence.</summary>
public sealed record SourceRunRef(string RunId, string ManifestHash);

/// <summary>Result for a single compliance control. PassRate is on the 0-1 scale, or null when not measured; a measured zero remains 0.</summary>
public sealed record EvidenceControl(
    string Id,
    string Title,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? PassRate,
    IReadOnlyList<string> ScenarioRefs,
    string? Notes,
    bool? RegressedFromBaseline = null);

/// <summary>Aggregated summary across all controls in an evidence document.</summary>
public sealed record EvidenceSummary(
    int ControlsTotal,
    int Passed,
    int Warnings,
    int Failed,
    string OverallStatus);

/// <summary>Attestation metadata for a compliance evidence document.</summary>
public sealed record Attestation(
    string AgentEvalVersion,
    string? ConfigurationId,
    string Evaluator,
    string EvaluatorModel);
