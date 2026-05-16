// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.GdprBenchmark.Reporting;

/// <summary>
/// A single structured recommendation produced by <see cref="RecommendationExtractor"/>.
/// </summary>
/// <remarks>
/// Introduced in v1.1 (task 1.5) to replace the legacy <c>string[]</c> shape. The schema
/// supports <c>oneOf</c> so existing <c>*-evidence.json</c> files written against the v0.8.1-beta
/// strings-only shape still validate.
/// </remarks>
/// <param name="ControlId">
/// The GDPR article control key (e.g. <c>"gdpr.art17.erasure"</c>) for which this
/// recommendation was generated. Corresponds to the evaluator key on the failed article node.
/// </param>
/// <param name="Severity">
/// Inherited severity of the failed control: one of <c>"low"</c>, <c>"medium"</c>,
/// <c>"high"</c>, or <c>"critical"</c>.
/// </param>
/// <param name="Text">
/// Human-readable remediation text describing the recommended corrective action.
/// </param>
public sealed record Recommendation(string ControlId, string Severity, string Text);
