// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// GraphQL wrapper around a single <see cref="ComplianceEvidence"/> document
/// that surfaces the per-doc audit-chain verdict alongside the payload.
/// </summary>
/// <param name="Evidence">The evidence document as written to disk.</param>
/// <param name="ChainValid">
/// <c>true</c> when <c>Evidence.SourceRun.ManifestHash</c> equals the actual
/// source run's <c>RunManifest.ContentHash</c>; <c>false</c> when the source
/// run cannot be found OR its content hash differs (tamper signal).
/// </param>
/// <param name="ChainBreakReason">
/// <c>null</c> when the chain is valid. One of <c>"source-run-not-found"</c>
/// or <c>"hash-mismatch"</c> when broken — the SPA renders a corresponding
/// audit-chain badge on the evidence-detail page.
/// </param>
/// <remarks>
/// Plan-07 §7 ("Audit chain enforcement") requires every read path that
/// returns evidence to validate the per-doc chain. The aggregated
/// <c>ComplianceMatrix</c> already enforces this; this wrapper extends the
/// same guarantee to the single-evidence resolver
/// <see cref="Query.ComplianceEvidence"/>. Schema audit 2026-05-13 P1-2.
/// </remarks>
public sealed record ComplianceEvidenceWithChain(
    ComplianceEvidence Evidence,
    bool ChainValid,
    string? ChainBreakReason);
