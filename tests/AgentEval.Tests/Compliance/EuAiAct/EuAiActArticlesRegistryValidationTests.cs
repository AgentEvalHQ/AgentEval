// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Articles.Models;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct;

/// <summary>
/// Regression coverage for GAP-01 (EU AI Act side): the registry must validate every loaded
/// spec at construction rather than silently building composites from malformed YAML.
/// </summary>
public class EuAiActArticlesRegistryValidationTests
{
    private static ScenarioSpec Scenario(double weight, string id = "s-1") =>
        new(
            Id: id,
            Pattern: "direct",
            Weight: weight,
            Input: "test input",
            EvaluationCriteria: ["criterion A"],
            Granularity: "atomic",
            Sensitive: false,
            ExpectedBehavior: null,
            Tags: []);

    private static ArticleSpec Spec(string controlId, double scenarioWeight) =>
        new(
            new ArticleMetadata(
                Article: "Article-X",
                Pillar: "Pillar1-ProhibitedPractices",
                ControlId: controlId,
                Title: "X",
                Severity: "low",
                PassThreshold: 0.70,
                WarnThreshold: 0.50,
                PillarWeight: 0.10,
                Aggregation: "weighted_sum"),
            [Scenario(scenarioWeight)]);

    [Fact]
    public void ValidateOrThrow_ValidSpecs_DoesNotThrow()
    {
        EuAiActArticlesRegistry.ValidateOrThrow(new[] { Spec("eu_ai.ok", scenarioWeight: 1.0) });
    }

    [Fact]
    public void ValidateOrThrow_InvalidSpec_ThrowsNamingTheControlId()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EuAiActArticlesRegistry.ValidateOrThrow(new[] { Spec("eu_ai.bad_weights", scenarioWeight: 0.5) }));

        Assert.Contains("eu_ai.bad_weights", ex.Message);
        Assert.Contains("weights", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_DuplicateControlId_ThrowsDescriptively()
    {
        // BUG-33: duplicate control_id across YAMLs → descriptive error, not ToDictionary's
        // non-diagnostic ArgumentException.
        var specs = new[] { Spec("eu_ai.dup", scenarioWeight: 1.0), Spec("eu_ai.dup", scenarioWeight: 1.0) };

        var ex = Assert.Throws<InvalidOperationException>(() => EuAiActArticlesRegistry.ValidateOrThrow(specs));

        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("eu_ai.dup", ex.Message);
    }
}
