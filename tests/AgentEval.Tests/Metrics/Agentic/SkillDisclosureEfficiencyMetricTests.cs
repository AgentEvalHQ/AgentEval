// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for <see cref="SkillDisclosureEfficiencyMetric"/> — a deterministic, code-based (no LLM call)
/// agentic metric measuring the MAF Agent Skills progressive-disclosure funnel.
/// </summary>
public class SkillDisclosureEfficiencyMetricTests
{
    [Fact]
    public async Task EvaluateAsync_TightFunnel_ScoresHigh()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "expense-report"));
        toolUsage.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));
        toolUsage.AddCall(RunCall(3, "expense-report", "summarize"));

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.Equal("code_skill_disclosure_efficiency", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_OrphanedRead_Penalized()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        // read_skill_resource with no prior load_skill for the same skill.
        toolUsage.AddCall(ReadCall(1, "expense-report", "resources/policy.md"));

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.True(result.Score < 100);
        Assert.NotNull(result.Details);
        Assert.Equal(1, result.Details!["orphanedReads"]);
    }

    [Fact]
    public async Task EvaluateAsync_OrphanedRun_Penalized()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "skill-a"));
        toolUsage.AddCall(RunCall(2, "skill-b", "run-me")); // wrong skill — orphaned

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.True(result.Score < 100);
        Assert.Equal(1, result.Details!["orphanedRuns"]);
    }

    [Fact]
    public async Task EvaluateAsync_DuplicateLoad_Penalized()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "expense-report"));
        toolUsage.AddCall(LoadCall(2, "expense-report")); // redundant — same skill loaded twice
        toolUsage.AddCall(ReadCall(3, "expense-report", "resources/policy.md"));

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.True(result.Score < 100);
        Assert.Equal(1, result.Details!["redundantLoads"]);
    }

    [Fact]
    public async Task EvaluateAsync_LoadStorm_Penalized()
    {
        // Many skills loaded, zero subsequent reads/runs.
        var options = new SkillDisclosureEfficiencyOptions { LoadStormThreshold = 3 };
        var metric = new SkillDisclosureEfficiencyMetric(options);
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "skill-a"));
        toolUsage.AddCall(LoadCall(2, "skill-b"));
        toolUsage.AddCall(LoadCall(3, "skill-c"));

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.True(result.Score < 100);
        Assert.Equal(true, result.Details!["loadStormDetected"]);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpectedSkills_ComputesSelectionF1()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "expense-report"));
        toolUsage.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var context = Context(toolUsage);
        context.SetProperty("expected_skills", (IReadOnlyList<string>)new List<string> { "expense-report" });

        var result = await metric.EvaluateAsync(context);

        Assert.NotNull(result.Details);
        Assert.True(result.Details!.ContainsKey("selectionF1"));
        Assert.Equal(1.0, (double)result.Details["selectionF1"], precision: 5);
        Assert.Contains("selection F1", result.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpectedSkills_PartialMatch_LowersF1()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "wrong-skill"));

        var context = Context(toolUsage);
        context.SetProperty("expected_skills", (IReadOnlyList<string>)new List<string> { "expense-report" });

        var result = await metric.EvaluateAsync(context);

        Assert.Equal(0.0, (double)result.Details!["selectionF1"], precision: 5);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutExpectedSkills_ExplanationStatesStructuralOnly_NoSelectionScoreFabricated()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "expense-report"));
        toolUsage.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.Contains("structural only", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selectionF1", result.Details!.Keys);
    }

    [Fact]
    public async Task EvaluateAsync_NoSkillToolsCalled_Returns100_NoFabricatedAdvertiseCount()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord { Name = "UnrelatedTool", CallId = "c1", Order = 1 });

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
        Assert.DoesNotContain("advertise", result.Details!.Keys);
        Assert.Equal("L→R→S", result.Details["funnel"]);
    }

    [Fact]
    public async Task EvaluateAsync_NullToolUsage_Returns100()
    {
        var metric = new SkillDisclosureEfficiencyMetric();

        var result = await metric.EvaluateAsync(new EvaluationContext
        {
            Input = "hello",
            Output = "hi",
            ToolUsage = null,
        });

        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }

    [Fact]
    public void Properties_ReturnCorrectMetadata()
    {
        var metric = new SkillDisclosureEfficiencyMetric();

        Assert.Equal("code_skill_disclosure_efficiency", metric.Name);
        Assert.True(metric.RequiresToolUsage);
        Assert.True(metric.Categories.HasFlag(MetricCategory.Agentic));
        Assert.True(metric.Categories.HasFlag(MetricCategory.CodeBased));
        Assert.True(metric.Categories.HasFlag(MetricCategory.RequiresToolUsage));
    }

    [Fact]
    public async Task EvaluateAsync_DetailsContainThreeObservableStagesOnly()
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(LoadCall(1, "expense-report"));
        toolUsage.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));
        toolUsage.AddCall(RunCall(3, "expense-report", "summarize"));

        var result = await metric.EvaluateAsync(Context(toolUsage));

        Assert.Equal(1, result.Details!["nLoad"]);
        Assert.Equal(1, result.Details["nRead"]);
        Assert.Equal(1, result.Details["nRun"]);
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private static EvaluationContext Context(ToolUsageReport toolUsage) => new()
    {
        Input = "Summarize the expense policy for a $200 dinner.",
        Output = "Per policy, meals are capped at $150; $200 exceeds the limit.",
        ToolUsage = toolUsage,
    };

    private static ToolCallRecord LoadCall(int order, string skillName) => new()
    {
        Name = SkillToolNames.LoadSkill,
        CallId = $"call-{order}",
        Order = order,
        Arguments = new Dictionary<string, object?> { [SkillToolNames.SkillNameArg] = skillName },
    };

    private static ToolCallRecord ReadCall(int order, string skillName, string resourceName) => new()
    {
        Name = SkillToolNames.ReadSkillResource,
        CallId = $"call-{order}",
        Order = order,
        Arguments = new Dictionary<string, object?>
        {
            [SkillToolNames.SkillNameArg] = skillName,
            [SkillToolNames.ResourceNameArg] = resourceName,
        },
    };

    private static ToolCallRecord RunCall(int order, string skillName, string scriptName) => new()
    {
        Name = SkillToolNames.RunSkillScript,
        CallId = $"call-{order}",
        Order = order,
        Arguments = new Dictionary<string, object?>
        {
            [SkillToolNames.SkillNameArg] = skillName,
            [SkillToolNames.ScriptNameArg] = scriptName,
        },
    };
}
