// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Evals.Agentic.Reporting;

/// <summary>
/// Evaluation result wrapper for an agentic benchmark run.
/// This is NOT a compliance evidence document — it is an evaluation artifact
/// that captures agent behavior on benchmark scenarios.
/// </summary>
/// <param name="Preset">The benchmark preset used (e.g. "agentic-execution", "tool-call-accuracy").</param>
/// <param name="Subject">The agent or workflow identity that was evaluated.</param>
/// <param name="SourceRunId">The run ID of the benchmark run that produced the composite tree.</param>
/// <param name="SourceManifestHash">Content hash of the source run manifest, for audit trail integrity.</param>
/// <param name="GeneratedAt">UTC timestamp when this result document was generated.</param>
/// <param name="CompositeTree">The recursive <see cref="EvalResult"/> tree produced by the benchmark runner.</param>
/// <param name="Summary">Category and per-evaluator roll-up summary.</param>
/// <param name="CriticalFindings">Leaf results with severity=critical that did not pass, ordered worst-first.</param>
/// <param name="Recommendations">Short remediation strings for failed evaluators, ordered by evaluator key.</param>
/// <param name="Disclaimer">Required disclaimer that this result is not a compliance attestation.</param>
/// <param name="Attestation">Tooling and judge configuration metadata for reproducibility.</param>
public sealed record AgenticBenchmarkResult(
    string Preset,
    SubjectIdentity Subject,
    string SourceRunId,
    string SourceManifestHash,
    DateTimeOffset GeneratedAt,
    EvalResult CompositeTree,
    AgenticSummary Summary,
    IReadOnlyList<EvalResult> CriticalFindings,
    IReadOnlyList<string> Recommendations,
    string Disclaimer,
    AgenticAttestation Attestation);

/// <summary>
/// Roll-up summary for an agentic benchmark run, broken down by evaluation category
/// and individual evaluator.
/// </summary>
/// <param name="OverallScore">Composite score in the [0, 1] range.</param>
/// <param name="OverallStatus">Overall verdict: <c>PASS</c>, <c>WARN</c>, or <c>FAIL</c>.</param>
/// <param name="PerCategory">
/// Score and status keyed by category string (e.g. "system-outcome", "agentic-process",
/// "rag", "safety-security", "operational", "judge-quality").
/// </param>
/// <param name="PerEvaluator">Score and status keyed by evaluator metric key.</param>
public sealed record AgenticSummary(
    double OverallScore,
    string OverallStatus,
    IReadOnlyDictionary<string, AgenticCategorySummary> PerCategory,
    IReadOnlyDictionary<string, AgenticEvaluatorSummary> PerEvaluator);

/// <summary>Summary for a single agentic benchmark category.</summary>
/// <param name="Score">Category-level composite score in [0, 1].</param>
/// <param name="Status">Category verdict: <c>PASS</c>, <c>WARN</c>, or <c>FAIL</c>.</param>
/// <param name="CriticalFails">Evaluator keys within this category that failed at critical severity.</param>
public sealed record AgenticCategorySummary(
    double Score,
    string Status,
    IReadOnlyList<string> CriticalFails);

/// <summary>Summary for a single evaluator within the agentic benchmark.</summary>
/// <param name="Score">Evaluator-level score in [0, 1].</param>
/// <param name="Status">Evaluator verdict: <c>PASS</c>, <c>WARN</c>, or <c>FAIL</c>.</param>
/// <param name="Severity">The evaluator's declared severity level (e.g. "critical", "high", "medium").</param>
/// <param name="Confidence">Optional confidence value from LLM judge, or null when not applicable.</param>
public sealed record AgenticEvaluatorSummary(
    double Score,
    string Status,
    string Severity,
    double? Confidence);

/// <summary>
/// Attestation block capturing tooling version, judge mode, and prompt versions
/// for reproducibility and audit purposes.
/// </summary>
/// <param name="AgentEvalVersion">The AgentEval assembly version that generated this result.</param>
/// <param name="JudgeMode">
/// The judge mode used: <c>single</c>, <c>panel</c>, or <c>adjudicated</c>.
/// </param>
/// <param name="PromptVersions">
/// Map of prompt template key to version string, e.g.
/// <c>{ "agentic-judge-system": "v1", "task-completion-criterion": "v1" }</c>.
/// </param>
public sealed record AgenticAttestation(
    string AgentEvalVersion,
    string JudgeMode,
    IReadOnlyDictionary<string, string> PromptVersions);

/// <summary>
/// Options controlling how the agentic benchmark report is generated.
/// </summary>
/// <param name="Preset">
/// The benchmark preset identifier. Defaults to <c>"agentic-execution"</c>.
/// Well-known values: <c>"agentic-execution"</c>, <c>"tool-call-accuracy"</c>,
/// <c>"safety"</c>, <c>"rag-quality"</c>, <c>"judge-quality"</c>, <c>"telemetry"</c>, <c>"stochastic-stability"</c>.
/// </param>
/// <param name="JudgeMode">
/// The judge mode used during evaluation. Defaults to <c>"single"</c>.
/// Allowed values: <c>"single"</c>, <c>"panel"</c>, <c>"adjudicated"</c>.
/// </param>
/// <param name="PromptVersions">
/// Optional map of prompt template key to version string. When null, version
/// defaults are applied by the reporter.
/// </param>
public sealed record AgenticReportOptions(
    string Preset = "agentic-execution",
    string JudgeMode = "single",
    IReadOnlyDictionary<string, string>? PromptVersions = null);
