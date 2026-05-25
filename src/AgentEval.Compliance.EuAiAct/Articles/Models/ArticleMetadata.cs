// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.EuAiAct.Articles.Models;

/// <summary>Metadata block from an EU AI Act article YAML file.</summary>
/// <param name="Article">Article identifier (e.g. "Article-14"). Required.</param>
/// <param name="Pillar">Pillar this article rolls up into (e.g. "Pillar3-HumanOversight"). Required.</param>
/// <param name="ControlId">Stable control identifier (e.g. "eu_ai.art14.human_oversight"). Required.</param>
/// <param name="Title">Human-readable title. Required.</param>
/// <param name="Severity">"low" / "medium" / "high" / "critical". Required.</param>
/// <param name="PassThreshold">Score above which the article passes.</param>
/// <param name="WarnThreshold">
/// Score above which the article warns instead of failing. <b>Plan-13 T4.1e LR2-003</b>:
/// this field is parsed + validated but is <b>not currently consumed</b> by the composite
/// builder — the WARN band is derived elsewhere. Documented here so the field surfaces
/// in the public record's XML doc rather than as silent dead metadata. Pending: either
/// route this into the composite builder's threshold resolver or drop the field from
/// the YAML schema in v0.11.0 (tracked under plan-13 T4.1e).
/// </param>
/// <param name="PillarWeight">
/// Within-pillar weight for this article (0..1). <b>Plan-13 T4.1e LR2-003</b>: same
/// situation as <see cref="WarnThreshold"/> — parsed + validated but not consumed
/// today (pillar weights ship as composite-builder constants instead). Pending the
/// same disposition.
/// </param>
/// <param name="Aggregation">Per-article aggregation strategy (default: weighted_sum).</param>
/// <param name="Description">Optional free-text description.</param>
public sealed record ArticleMetadata(
    string Article,
    string Pillar,
    string ControlId,
    string Title,
    string Severity,
    double PassThreshold,
    double WarnThreshold,
    double PillarWeight,
    string Aggregation = "weighted_sum",
    string? Description = null);
