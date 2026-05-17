// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Compliance.EuAiAct.Reporting;

/// <summary>
/// EU AI Act-specific evidence wrapper that extends the plan-01 <see cref="ComplianceEvidence"/>
/// with pillar/article roll-ups, critical findings, recommendations, and a disclaimer.
/// </summary>
public sealed record EuAiActComplianceEvidence(
    ComplianceEvidence Base,
    string Preset,
    IReadOnlyList<string> DomainPacks,
    EvalResult CompositeTree,
    EuAiActSummary Summary,
    IReadOnlyList<EvalResult> CriticalFindings,
    IReadOnlyList<Recommendation> Recommendations,
    string Disclaimer,
    EuAiActAttestation EuAiActAttestation);

/// <summary>
/// Roll-up summary of an EU AI Act benchmark run, broken down by pillar and article.
/// </summary>
public sealed record EuAiActSummary(
    double OverallScore,
    string OverallStatus,
    IReadOnlyDictionary<string, EuAiActPillarSummary> PerPillar,
    IReadOnlyDictionary<string, EuAiActArticleSummary> PerArticle);

/// <summary>Summary for a single EU AI Act pillar.</summary>
public sealed record EuAiActPillarSummary(double Score, string Status, IReadOnlyList<string> CriticalFails);

/// <summary>Summary for a single EU AI Act article composite.</summary>
public sealed record EuAiActArticleSummary(double Score, string Status, int ScenarioCount, int ScenariosFailed, string Severity);

/// <summary>Attestation block for the EU AI Act evidence wrapper.</summary>
public sealed record EuAiActAttestation(string JudgeMode, IReadOnlyDictionary<string, string> PromptVersions);

/// <summary>Options controlling how the EU AI Act compliance report is generated.</summary>
public sealed record EuAiActReportOptions(
    string Preset = "standard",
    IReadOnlyList<string>? DomainPacks = null,
    string JudgeMode = "mode-a",
    IReadOnlyDictionary<string, string>? PromptVersions = null,
    string? JudgeModel = null);
