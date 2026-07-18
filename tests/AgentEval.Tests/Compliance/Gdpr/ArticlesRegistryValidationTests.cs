// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Models;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// Regression coverage for GAP-01: ArticleYamlValidator was only exercised by tests, so the
/// runtime registry loaded and built composites without validating. A malformed spec
/// (weights not summing to 1.0, bad pillar, missing criterion) must now fail fast at load.
/// </summary>
public class ArticlesRegistryValidationTests
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

    private static ArticleSpec Spec(string controlId, double scenarioWeight, double passThreshold = 0.70) =>
        new(
            new ArticleMetadata(
                Article: "Article-X",
                Pillar: "Pillar1-Foundations",
                ControlId: controlId,
                Title: "X",
                Severity: "low",
                PassThreshold: passThreshold,
                WarnThreshold: 0.50,
                PillarWeight: 0.10,
                Aggregation: "weighted_sum"),
            [Scenario(scenarioWeight)]);

    [Fact]
    public void ValidateOrThrow_ValidSpecs_DoesNotThrow()
    {
        // Single scenario weighted 1.0 → valid.
        ArticlesRegistry.ValidateOrThrow(new[] { Spec("gdpr.ok", scenarioWeight: 1.0) });
    }

    [Fact]
    public void ValidateOrThrow_InvalidSpec_ThrowsNamingTheControlId()
    {
        // Scenario weights sum to 0.5, not 1.0 → invalid.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArticlesRegistry.ValidateOrThrow(new[] { Spec("gdpr.bad_weights", scenarioWeight: 0.5) }));

        Assert.Contains("gdpr.bad_weights", ex.Message);
        Assert.Contains("weights", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_DuplicateControlId_ThrowsDescriptively()
    {
        // BUG-33: two individually-valid specs sharing a control_id must throw a descriptive error
        // naming the duplicate, not a non-diagnostic ArgumentException from ToDictionary.
        var specs = new[] { Spec("gdpr.dup", scenarioWeight: 1.0), Spec("gdpr.dup", scenarioWeight: 1.0) };

        var ex = Assert.Throws<InvalidOperationException>(() => ArticlesRegistry.ValidateOrThrow(specs));

        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("gdpr.dup", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_ZeroPassThreshold_ThrowsNamingPassThreshold()
    {
        // Regression (issue #16): an omitted metadata.pass_threshold field silently deserializes to the C#
        // default 0.0, and 0.0 trivially passes every score >= 0 — auto-passing the whole article with zero
        // actual signal, exactly the failure mode this benchmark exists to catch. Must be rejected, not just
        // negative values.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArticlesRegistry.ValidateOrThrow(new[] { Spec("gdpr.zero_threshold", scenarioWeight: 1.0, passThreshold: 0.0) }));

        Assert.Contains("gdpr.zero_threshold", ex.Message);
        Assert.Contains("pass_threshold", ex.Message);
    }

    // Note: the production registry constructor now calls ValidateOrThrow against every embedded
    // YAML; that real-data path is already exercised by the GDPR end-to-end / registry tests,
    // which would fail if any shipped spec were invalid.
}
