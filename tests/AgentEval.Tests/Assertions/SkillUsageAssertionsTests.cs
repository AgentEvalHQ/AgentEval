// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Models;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for <see cref="SkillUsageAssertions"/> — the MAF Agent Skills fluent assertion sugar
/// (Phase 1 of the Agent Skills evaluation design).
/// </summary>
public class SkillUsageAssertionsTests
{
    // ------------------------------------------------------------------------------------------
    // HaveLoadedSkill
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveLoadedSkill_NoNameFilter_MatchingTool_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));

        var exception = Record.Exception(() => report.Should().HaveLoadedSkill());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveLoadedSkill_WithMatchingSkillName_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));

        var exception = Record.Exception(() => report.Should().HaveLoadedSkill("expense-report"));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveLoadedSkill_WithMatchingSkillName_IsCaseInsensitive()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "Expense-Report"));

        var exception = Record.Exception(() => report.Should().HaveLoadedSkill("expense-report"));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveLoadedSkill_SelectsTheMatchingCallAmongMultiple()
    {
        // load_skill called twice, for two different skills — HaveLoadedSkill("skill-b") must bind
        // to the SECOND call so a chained .AfterTool(...)/order assertion is accurate.
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "skill-a"));
        report.AddCall(LoadCall(2, "skill-b"));

        var assertion = report.Should().HaveLoadedSkill("skill-b");

        // Times(1) operates on ALL calls named load_skill (2 total) — sanity check we didn't just
        // wrap the first call by accident by asserting an argument-specific check instead.
        var exception = Record.Exception(() => assertion.WithArgument(SkillToolNames.SkillNameArg, "skill-b"));
        Assert.Null(exception);
    }

    [Fact]
    public void HaveLoadedSkill_ToolNeverCalled_ThrowsWithHelpfulMessage()
    {
        var report = new ToolUsageReport();

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveLoadedSkill("expense-report"));

        Assert.Contains("load_skill", exception.Message);
        Assert.Contains("expense-report", exception.Message);
        Assert.Contains("never called", exception.Message);
    }

    [Fact]
    public void HaveLoadedSkill_CalledButForDifferentSkill_ThrowsWithHelpfulMessage()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "travel-policy"));

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveLoadedSkill("expense-report"));

        Assert.Contains("expense-report", exception.Message);
        Assert.Contains("no matching call", exception.Message);
    }

    [Fact]
    public void HaveLoadedSkill_MissingTool_InScope_RecordsSoftFailure_NoInvalidOperation()
    {
        // Mirrors the base ToolUsageAssertions BUG-15 regression test: chained assertions after a
        // failed HaveLoadedSkill must not throw InvalidOperationException inside an active scope.
        var report = new ToolUsageReport();

        using var scope = new AgentEvalScope();
        report.Should().HaveLoadedSkill("expense-report").WithoutError();

        Assert.True(scope.HasFailures);
        scope.Clear();
    }

    // ------------------------------------------------------------------------------------------
    // HaveReadSkillResource
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveReadSkillResource_WithSkillAndResourceName_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var exception = Record.Exception(() =>
            report.Should().HaveReadSkillResource("expense-report", "resources/policy.md"));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveReadSkillResource_ChainedAfterTool_EnforcesOrdering()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var exception = Record.Exception(() =>
            report.Should()
                .HaveReadSkillResource("expense-report", "resources/policy.md")
                .AfterTool(SkillToolNames.LoadSkill));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveReadSkillResource_ChainedAfterTool_FailsWhenReadPrecedesLoad()
    {
        var report = new ToolUsageReport();
        report.AddCall(ReadCall(1, "expense-report", "resources/policy.md"));
        report.AddCall(LoadCall(2, "expense-report"));

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should()
                .HaveReadSkillResource("expense-report", "resources/policy.md")
                .AfterTool(SkillToolNames.LoadSkill));

        Assert.Contains("after", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HaveReadSkillResource_WrongResourceName_Throws()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveReadSkillResource("expense-report", "resources/faq.md"));

        Assert.Contains("resourceName='resources/faq.md'", exception.Message);
    }

    // ------------------------------------------------------------------------------------------
    // HaveRunSkillScript
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveRunSkillScript_WithSkillAndScriptName_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(RunCall(2, "expense-report", "summarize"));

        var exception = Record.Exception(() =>
            report.Should().HaveRunSkillScript("expense-report", "summarize"));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveRunSkillScript_NotCalled_Throws()
    {
        var report = new ToolUsageReport();

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveRunSkillScript("expense-report", "summarize"));

        Assert.Contains("run_skill_script", exception.Message);
    }

    // ------------------------------------------------------------------------------------------
    // NotHaveRunSkillScript
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void NotHaveRunSkillScript_ScriptAbsent_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var exception = Record.Exception(() =>
            report.Should().NotHaveRunSkillScript(because: "read-only summarization must not execute code"));

        Assert.Null(exception);
    }

    [Fact]
    public void NotHaveRunSkillScript_ScriptPresent_ThrowsFailClosed()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(RunCall(2, "expense-report", "summarize"));

        var exception = Assert.Throws<BehavioralPolicyViolationException>(() =>
            report.Should().NotHaveRunSkillScript(because: "read-only summarization must not execute code"));

        Assert.Contains("run_skill_script", exception.Message);
    }

    // ------------------------------------------------------------------------------------------
    // HaveDisclosedProgressively
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveDisclosedProgressively_TightFunnel_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));
        report.AddCall(RunCall(3, "expense-report", "summarize"));

        var exception = Record.Exception(() => report.Should().HaveDisclosedProgressively());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveDisclosedProgressively_ReadBeforeLoad_Throws()
    {
        var report = new ToolUsageReport();
        report.AddCall(ReadCall(1, "expense-report", "resources/policy.md"));
        report.AddCall(LoadCall(2, "expense-report"));

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveDisclosedProgressively());

        Assert.Contains("Progressive disclosure violated", exception.Message);
        Assert.Contains("read_skill_resource", exception.Message);
    }

    [Fact]
    public void HaveDisclosedProgressively_RunForDifferentSkillThanLoaded_Throws()
    {
        // load_skill("skill-a") then run_skill_script("skill-b", ...) — same-skill correlation must
        // catch this even though SOME load_skill call did occur earlier.
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "skill-a"));
        report.AddCall(RunCall(2, "skill-b", "run-me"));

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveDisclosedProgressively());

        Assert.Contains("skill-b", exception.Message);
    }

    [Fact]
    public void HaveDisclosedProgressively_InScope_ReportsEveryOrphanedCall()
    {
        var report = new ToolUsageReport();
        report.AddCall(ReadCall(1, "skill-a", "res-1"));
        report.AddCall(RunCall(2, "skill-a", "script-1"));

        using var scope = new AgentEvalScope();
        report.Should().HaveDisclosedProgressively();

        Assert.Equal(2, scope.FailureCount);
        scope.Clear();
    }

    [Fact]
    public void HaveDisclosedProgressively_NoSkillCallsAtAll_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "UnrelatedTool", CallId = "c1", Order = 1 });

        var exception = Record.Exception(() => report.Should().HaveDisclosedProgressively());

        Assert.Null(exception);
    }

    // ------------------------------------------------------------------------------------------
    // Param-name-agnostic (value-based) matching — locks in the open-item-(a) mitigation.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveLoadedSkill_ArgumentKeyDiffersFromGuessedName_StillMatchesByValue()
    {
        // Simulates a hypothetical future MAF rename of the "skillName" parameter: as long as the
        // call carries exactly one string-valued argument, value-based fallback matching still finds it.
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = SkillToolNames.LoadSkill,
            CallId = "c1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["name"] = "expense-report" }, // NOT "skillName"
        });

        var exception = Record.Exception(() => report.Should().HaveLoadedSkill("expense-report"));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveDisclosedProgressively_ArgumentKeyDiffersFromGuessedName_StillCorrelatesBySkillValue()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = SkillToolNames.LoadSkill,
            CallId = "c1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["name"] = "expense-report" },
        });
        report.AddCall(new ToolCallRecord
        {
            Name = SkillToolNames.ReadSkillResource,
            CallId = "c2",
            Order = 2,
            Arguments = new Dictionary<string, object?> { ["skill"] = "expense-report", ["resource"] = "policy.md" },
        });

        // Two string-valued args on the read call ⇒ the ambiguous fallback can't disambiguate a
        // skill name, so it degrades to "was ANY load_skill call seen" — which is satisfied here.
        var exception = Record.Exception(() => report.Should().HaveDisclosedProgressively());

        Assert.Null(exception);
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

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
