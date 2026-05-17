// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.EuAiAct.Articles.Models;

/// <summary>A single evaluation scenario from an EU AI Act article YAML file.</summary>
public sealed record ScenarioSpec(
    string Id,
    string Pattern,
    double Weight,
    string Input,
    IReadOnlyList<string> EvaluationCriteria,
    string Granularity,
    bool Sensitive,
    string? ExpectedBehavior,
    IReadOnlyList<string> Tags);
