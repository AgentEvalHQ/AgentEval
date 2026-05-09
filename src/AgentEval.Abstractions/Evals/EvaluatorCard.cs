// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Schema-driven metadata describing an <see cref="IEval"/> implementation, consumed by
/// Mission Control's <c>&lt;EvaluatorRegistry/&gt;</c> page and the recursive run-detail
/// tooltip system. One JSON card per evaluator; the union of all cards drives the GraphQL
/// <c>Query.evaluators(...)</c> resolver in plan-08 Phase 1.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the shipped <c>eval-result.schema.json</c> carries the *result* shape
/// (metric, score, details, provenance) but not the *evaluator's* metadata — cost tier,
/// description, expected inputs, recommended visualisation, external compatibility. The
/// portal needs all of those to render a useful registry view and per-metric tooltip.
/// </para>
/// <para>
/// Cards are hand-authored JSON files at <c>src/AgentEval.Evals.Agentic/EvaluatorCards/&lt;key&gt;.json</c>
/// (embedded resources). Validation against <c>evaluator-card.schema.json</c> v1.0 happens
/// at load time via <c>SchemaValidator</c>. Plan-08 MC1.5.0 ships the schema and record;
/// MC1.5.1 authors the 46 cards (one per shipped evaluator).
/// </para>
/// <para>
/// See plan-07 §11 for the design rationale and plan-08 Phase 1 acceptance criteria.
/// </para>
/// </remarks>
/// <param name="Key">Stable evaluator key, matching <see cref="IEval.Key"/>.</param>
/// <param name="Name">Human-readable name for the evaluator (UI labels).</param>
/// <param name="Category">Category folder name (e.g. <c>"system"</c>, <c>"process"</c>, <c>"adversarial"</c>).</param>
/// <param name="Version">SemVer-shaped evaluator version, matching <see cref="IEval.Version"/>.</param>
/// <param name="Description">Human-readable explanation of what this evaluator measures.</param>
/// <param name="CostTier">Cost tier per <see cref="EvaluatorCostMap"/>.</param>
/// <param name="HigherIsBetter">True when a higher score is better (the typical case); false for "lower is better" metrics.</param>
/// <param name="DefaultThreshold">Suggested pass threshold when constructing the evaluator without an explicit one.</param>
/// <param name="ExpectedInputs">Required + optional inputs the evaluator consumes (typically <c>EvalInput.Metadata</c> keys).</param>
/// <param name="RecommendedVisualization">Hint to the portal for how to render this metric (radar / timeline / histogram / heatmap / sparkline / sankey / stackedBar / none).</param>
/// <param name="CompatibleWith">Equivalent evaluators in external systems (e.g. Foundry compatibility URIs).</param>
/// <param name="Links">Links to documentation and source code.</param>
public sealed record EvaluatorCard(
    string Key,
    string Name,
    string Category,
    string Version,
    string Description,
    EvaluatorCostTier CostTier,
    bool HigherIsBetter,
    double? DefaultThreshold,
    IReadOnlyList<EvaluatorExpectedInput> ExpectedInputs,
    string? RecommendedVisualization,
    IReadOnlyList<EvaluatorExternalCompat> CompatibleWith,
    EvaluatorCardLinks? Links);

/// <summary>
/// One required-or-optional input that the evaluator reads from <see cref="EvalInput"/>.
/// </summary>
/// <param name="Kind">Where the input comes from: <c>"metadata"</c> (the typical case — keyed lookup in <c>EvalInput.Metadata</c>), <c>"query"</c>, <c>"response"</c>, <c>"context"</c>, <c>"toolCalls"</c>, or <c>"systemMessage"</c>.</param>
/// <param name="Key">When <paramref name="Kind"/> is <c>"metadata"</c>, the metadata-dictionary key. Empty string when not applicable.</param>
/// <param name="Required">Whether the eval skips (returns <see cref="EvalResult.Skipped(IEval, string)"/>) when this input is missing.</param>
/// <param name="Description">Human-readable description of what this input represents.</param>
public sealed record EvaluatorExpectedInput(
    string Kind,
    string Key,
    bool Required,
    string Description);

/// <summary>
/// External-system compatibility entry — points to the equivalent metric in another framework.
/// Typically used for Foundry parity claims; future entries may target Eleuther / OpenLLM-Leaderboard / etc.
/// </summary>
/// <param name="System">The external system identifier (e.g. <c>"foundry"</c>).</param>
/// <param name="Uri">A canonical URI identifying the equivalent metric in that system (informational only — not dereferenced at runtime).</param>
public sealed record EvaluatorExternalCompat(string System, string Uri);

/// <summary>
/// Optional links shown on the evaluator's detail page in Mission Control.
/// </summary>
/// <param name="Documentation">Path or URL to user-facing documentation for this evaluator.</param>
/// <param name="Source">Path to the implementation source file (repo-relative — e.g. <c>"src/AgentEval.Evals.Agentic/Process/ToolSelectionEval.cs"</c>).</param>
public sealed record EvaluatorCardLinks(string? Documentation, string? Source);
