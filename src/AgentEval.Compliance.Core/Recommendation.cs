// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.Core;

/// <summary>
/// A single structured remediation recommendation produced by a compliance pack's RecommendationExtractor.
/// Shared by the GDPR and EU AI Act packs (ARC-01) — the record shape was identical in both.
/// </summary>
/// <remarks>
/// Introduced in v1.1 (task 1.5) to replace the legacy <c>string[]</c> shape. The evidence schema uses
/// <c>anyOf</c> at the <c>items</c> level so existing <c>*-evidence.json</c> files written against the
/// v0.8.1-beta strings-only shape still validate.
/// </remarks>
/// <param name="ControlId">
/// The regulation control key (e.g. <c>"gdpr.art17.erasure"</c> / <c>"euaiact.art14.human_oversight"</c>)
/// for which this recommendation was generated. Corresponds to the evaluator key on the failed node.
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
