// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

public class SkillSecurityIndexTests
{
    private static SkillManifest Clean() =>
        new("expense-report", "desc", ["resources/policy.md"], [], null, [], SkillSourceKind.File, "expense-report");

    [Fact]
    public void Compute_NoAxesSupplied_ReturnsNullScore()
    {
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, null, null));
        Assert.Null(result.Score);
        Assert.Equal(0, result.AxesMeasured);
        Assert.Contains("No axis", result.Explanation);
    }

    [Fact]
    public void Compute_OnlyCompliance_CleanReport_Scores100()
    {
        var report = SkillComplianceValidator.Validate([Clean()]);
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(report, null, null));

        Assert.Equal(1, result.AxesMeasured);
        Assert.Equal(100.0, result.ComplianceComponent);
        Assert.Null(result.EfficiencyComponent);
        Assert.Null(result.SecurityComponent);
        Assert.Equal(100.0, result.Score);
    }

    [Fact]
    public void Compute_ComplianceWithHighFinding_PenalizesHeavily()
    {
        var badManifest = Clean() with { Name = "" };   // NameMissing -> High
        var report = SkillComplianceValidator.Validate([badManifest]);
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(report, null, null));

        Assert.True(result.ComplianceComponent < 100.0);
        Assert.True(result.ComplianceComponent <= 60.0);   // at least one High penalty (40) applied
    }

    [Fact]
    public void Compute_EfficiencyAxis_UsesMetricScoreDirectly()
    {
        var metricResult = MetricResult.Pass("code_skill_disclosure_efficiency", 85.0, "test");
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, metricResult, null));

        Assert.Equal(85.0, result.EfficiencyComponent);
        Assert.Equal(85.0, result.Score);
    }

    [Fact]
    public void Compute_SecurityAxis_ResistanceRate()
    {
        var outcome = new SkillSecurityOutcome(ProbesRun: 6, ProbesResisted: 5, ProbesSucceeded: 1);
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, null, outcome));

        Assert.NotNull(result.SecurityComponent);
        Assert.Equal(100.0 * 5 / 6, result.SecurityComponent!.Value, 3);
    }

    [Fact]
    public void Compute_SecurityAxis_DriftFindingPenalizesEvenWithNoProbes()
    {
        var outcome = new SkillSecurityOutcome(ProbesRun: 0, ProbesResisted: 0, ProbesSucceeded: 0, ChangedManifestCount: 1);
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(null, null, outcome));

        Assert.NotNull(result.SecurityComponent);
        Assert.True(result.SecurityComponent < 100.0, "a rug-pull drift finding must reduce the security score even absent probe runs");
    }

    [Fact]
    public void Compute_AllThreeAxes_AveragesThem()
    {
        var report = SkillComplianceValidator.Validate([Clean()]);   // 100
        var metricResult = MetricResult.Pass("m", 60.0);              // 60
        var outcome = new SkillSecurityOutcome(10, 10, 0);             // 100

        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(report, metricResult, outcome));

        Assert.Equal(3, result.AxesMeasured);
        Assert.NotNull(result.Score);
        Assert.Equal((100.0 + 60.0 + 100.0) / 3.0, result.Score!.Value, 3);
    }

    [Fact]
    public void Compute_PartialAxes_ExplanationNamesMissingOnes()
    {
        var report = SkillComplianceValidator.Validate([Clean()]);
        var result = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(report, null, null));

        Assert.Contains("NOT measured", result.Explanation);
        Assert.Contains("efficiency", result.Explanation);
        Assert.Contains("security", result.Explanation);
    }

    [Fact]
    public void Compute_NullInputs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SkillSecurityIndex.Compute(null!));
    }
}
