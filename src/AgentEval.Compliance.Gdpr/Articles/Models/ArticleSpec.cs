// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.Gdpr.Articles.Models;

/// <summary>Represents a complete GDPR article YAML file (metadata + scenarios).</summary>
public sealed record ArticleSpec(
    ArticleMetadata Metadata,
    IReadOnlyList<ScenarioSpec> Scenarios);
