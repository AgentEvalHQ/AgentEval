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
/// <param name="Aggregation">Per-article aggregation strategy (default: weighted_sum).</param>
/// <param name="Description">Optional free-text description.</param>
public sealed record ArticleMetadata(
    string Article,
    string Pillar,
    string ControlId,
    string Title,
    string Severity,
    double PassThreshold,
    string Aggregation = "weighted_sum",
    string? Description = null);
