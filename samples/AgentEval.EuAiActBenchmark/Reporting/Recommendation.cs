// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.EuAiActBenchmark.Reporting;

/// <summary>
/// A single structured recommendation produced by <see cref="RecommendationExtractor"/>.
/// </summary>
/// <remarks>
/// Introduced in v1.1 (task 1.5) to replace the legacy <c>string[]</c> shape. The schema
/// uses <c>anyOf</c> at the <c>items</c> level so existing <c>*-evidence.json</c> files
/// written against the v0.8.1-beta strings-only shape still validate.
/// </remarks>
/// <param name="ControlId">
/// The EU AI Act article control key (e.g. <c>"eu_ai.art14.human_oversight"</c>) for which this
/// recommendation was generated. Corresponds to the evaluator key on the failed article node.
/// </param>
/// <param name="Severity">
/// Inherited severity of the failed control: one of <c>"low"</c>, <c>"medium"</c>,
/// <c>"high"</c>, or <c>"critical"</c>.
/// </param>
/// <param name="Text">
/// Human-readable remediation text describing the recommended corrective action.
/// </param>
/// <param name="Metadata">
/// Optional free-form string-to-string metadata. Reserved for v1.2+ extensions
/// (e.g. evidence references, severity-rollup details, internal correlation ids)
/// without requiring a breaking schema change. The schema allows arbitrary string
/// key-value entries under the <c>metadata</c> property.
/// </param>
public sealed record Recommendation(
    string ControlId,
    string Severity,
    string Text,
    IReadOnlyDictionary<string, string>? Metadata = null);
