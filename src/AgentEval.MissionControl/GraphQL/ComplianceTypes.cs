// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Per-regulation summary for the <c>Query.compliance</c> dashboard tile.
/// Plan-08 MC1.4.4.
/// </summary>
public sealed record ComplianceRegulationSummary(
    string Regulation,
    int SubjectCount,
    int EvidenceCount,
    DateTimeOffset? LastEvidenceAt,
    string? OverallStatus);

/// <summary>
/// One subject row in <see cref="ComplianceMatrix"/>. Kind comes from the subjects
/// store (compliance evidence on disk is keyed by name only — see plan-07 §8).
/// </summary>
public sealed record ComplianceMatrixSubject(string Name, SubjectKind Kind);

/// <summary>
/// One control column in <see cref="ComplianceMatrix"/>. Title is taken from the
/// most recent evidence's <see cref="EvidenceControl"/> for that control.
/// </summary>
public sealed record ComplianceMatrixControl(string Id, string Title);

/// <summary>
/// One cell in the regulation × subject × control matrix.
/// </summary>
/// <remarks>
/// <see cref="Timestamp"/> carries the raw on-disk timestamp directory name
/// (<c>yyyy-MM-dd_HH-mm-ss</c> format) so the SPA can build drill-through URLs
/// without converting <see cref="LastEvidenceAt"/> through JavaScript's
/// <c>Date</c> — which would silently shift to UTC and 404 against the local-clock
/// directory in any non-UTC workspace (CET, PST, JST, etc.).
/// </remarks>
public sealed record ComplianceMatrixCell(
    string SubjectName,
    string ControlId,
    string Status,
    double PassRate,
    DateTimeOffset LastEvidenceAt,
    string LastEvidenceRunId,
    string Timestamp,
    bool? RegressedFromBaseline,
    bool ChainValid = true,
    string? ChainBreakReason = null);
// ChainValid + ChainBreakReason added 2026-05-24 per plan-08 portal-review finding A1.
// The aggregate `ComplianceMatrix.AllChainsValid` boolean answered "is at least one
// cell broken?" without telling the SPA WHICH cell. A tampered cell whose pre-tamper
// status was `pass` would render as the green ✓ without any visual cue. The per-cell
// bit lets `ComplianceMatrix.tsx` overlay a "tamper detected" stripe on the affected
// cell while leaving honest cells alone. Defaults preserve backward-compat for any
// test/fixture that constructs cells without the new fields.

/// <summary>
/// The killer-feature endpoint: full subject × control matrix for a regulation,
/// computed from the latest evidence per subject. Plan-07 §10 / §8.
/// </summary>
public sealed record ComplianceMatrix(
    string Regulation,
    IReadOnlyList<ComplianceMatrixSubject> Subjects,
    IReadOnlyList<ComplianceMatrixControl> Controls,
    IReadOnlyList<ComplianceMatrixCell> Cells,
    bool AllChainsValid,
    DateTimeOffset? LastEvidenceAt);
