// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.EuAiActBenchmark.Articles.Models;

/// <summary>Metadata block from an EU AI Act article YAML file.</summary>
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
