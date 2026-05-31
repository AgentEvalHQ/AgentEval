// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Compliance.Core;

namespace AgentEval.Compliance.Gdpr.Reporting;

/// <summary>
/// GDPR-specific evidence wrapper that extends the plan-01 <see cref="ComplianceEvidence"/>
/// with pillar/article roll-ups, critical findings, recommendations, and a disclaimer.
/// </summary>
public sealed record GdprComplianceEvidence(
    ComplianceEvidence Base,
    string Preset,
    IReadOnlyList<string> DomainPacks,
    EvalResult CompositeTree,
    GdprSummary Summary,
    IReadOnlyList<EvalResult> CriticalFindings,
    IReadOnlyList<Recommendation> Recommendations,
    string Disclaimer,
    GdprAttestation GdprAttestation);

/// <summary>
/// Roll-up summary of a GDPR benchmark run, broken down by pillar and article.
/// </summary>
public sealed record GdprSummary(
    double OverallScore,
    string OverallStatus,
    IReadOnlyDictionary<string, GdprPillarSummary> PerPillar,
    IReadOnlyDictionary<string, GdprArticleSummary> PerArticle);

/// <summary>Summary for a single GDPR pillar.</summary>
public sealed record GdprPillarSummary(double Score, string Status, IReadOnlyList<string> CriticalFails);

/// <summary>Summary for a single GDPR article composite.</summary>
public sealed record GdprArticleSummary(double Score, string Status, int ScenarioCount, int ScenariosFailed, string Severity);

/// <summary>Attestation block for the GDPR evidence wrapper.</summary>
public sealed record GdprAttestation(string JudgeMode, IReadOnlyDictionary<string, string> PromptVersions);

/// <summary>Options controlling how the GDPR compliance report is generated.</summary>
public sealed record GdprReportOptions(
    string Preset = "standard",
    IReadOnlyList<string>? DomainPacks = null,
    string JudgeMode = "mode-a",
    IReadOnlyDictionary<string, string>? PromptVersions = null,
    string? JudgeModel = null);
