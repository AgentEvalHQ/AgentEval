// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Models;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for the Phase 4 "cheap sugar" catalog items (design doc §10.4): <c>WithScriptArgument</c>,
/// <c>ForSkill</c>, <c>HaveDisclosedEfficiently</c>, <c>HaveCorrectlyDeclinedSkill</c>.
/// </summary>
public class SkillUsageAssertionsSugarTests
{
    // ------------------------------------------------------------------------------------------
    // WithScriptArgument
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void WithScriptArgument_MatchingValue_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(RunCallWithArgs(1, "expense-report", "scripts/summarize.csx", new Dictionary<string, object?> { ["amount"] = 200 }));

        var exception = Record.Exception(() =>
            report.Should().HaveRunSkillScript("expense-report", "scripts/summarize.csx").WithScriptArgument("amount", 200));

        Assert.Null(exception);
    }

    [Fact]
    public void WithScriptArgument_MismatchedValue_Throws()
    {
        var report = new ToolUsageReport();
        report.AddCall(RunCallWithArgs(1, "expense-report", "scripts/summarize.csx", new Dictionary<string, object?> { ["amount"] = 200 }));

        var exception = Record.Exception(() =>
            report.Should().HaveRunSkillScript("expense-report", "scripts/summarize.csx").WithScriptArgument("amount", 999));

        Assert.NotNull(exception);
    }

    [Fact]
    public void WithScriptArgument_MissingKey_Throws()
    {
        var report = new ToolUsageReport();
        report.AddCall(RunCallWithArgs(1, "expense-report", "scripts/summarize.csx", new Dictionary<string, object?> { ["amount"] = 200 }));

        var exception = Record.Exception(() =>
            report.Should().HaveRunSkillScript("expense-report", "scripts/summarize.csx").WithScriptArgument("currency", "USD"));

        Assert.NotNull(exception);
    }

    [Fact]
    public void WithScriptArgument_NoNestedArguments_Throws()
    {
        var report = new ToolUsageReport();
        report.AddCall(RunCall(1, "expense-report", "scripts/summarize.csx"));   // no nested "arguments" key at all

        var exception = Record.Exception(() =>
            report.Should().HaveRunSkillScript("expense-report", "scripts/summarize.csx").WithScriptArgument("amount", 200));

        Assert.NotNull(exception);
    }

    [Fact]
    public void WithScriptArgument_ToolNeverCalled_ShortCircuitsWithoutThrowing()
    {
        // HaveRunSkillScript itself already records the failure (soft-fail scope convention, BUG-15);
        // WithScriptArgument must short-circuit on a null Call, not throw a SECOND exception.
        var report = new ToolUsageReport();
        var exception = Record.Exception(() =>
        {
            try
            {
                report.Should().HaveRunSkillScript("expense-report").WithScriptArgument("amount", 200);
            }
            catch (AgentEvalAssertionException)
            {
                // expected — HaveRunSkillScript's own failure
            }
        });

        Assert.Null(exception);
    }

    // ------------------------------------------------------------------------------------------
    // ForSkill
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void ForSkill_ScopesToOnlyThatSkillsCalls()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(LoadCall(2, "invoice-helper"));
        report.AddCall(ReadCall(3, "expense-report", "resources/policy.md"));
        report.AddCall(ReadCall(4, "invoice-helper", "resources/format.md"));

        var scoped = report.ForSkill("expense-report");

        Assert.Equal(2, scoped.Count);
        Assert.All(scoped.Calls, c => Assert.True(
            SkillArgumentExtractionTestHook.SkillNameMatches(c, "expense-report")));
    }

    [Fact]
    public void ForSkill_ScopedReport_SupportsChainedAssertions()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(LoadCall(2, "invoice-helper"));

        var exception = Record.Exception(() => report.ForSkill("invoice-helper").Should().HaveLoadedSkill("invoice-helper"));
        Assert.Null(exception);
    }

    [Fact]
    public void ForSkill_NoMatchingCalls_ReturnsEmptyReport()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));

        var scoped = report.ForSkill("nonexistent-skill");
        Assert.Equal(0, scoped.Count);
    }

    [Fact]
    public void ForSkill_ExcludesNonSkillToolCalls()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(new ToolCallRecord { Name = "unrelated_tool", CallId = "c2", Order = 2, Arguments = null });

        var scoped = report.ForSkill("expense-report");
        Assert.Equal(1, scoped.Count);
    }

    // ------------------------------------------------------------------------------------------
    // HaveDisclosedEfficiently
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveDisclosedEfficiently_CleanFunnel_PassesHighThreshold()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));
        report.AddCall(ReadCall(2, "expense-report", "resources/policy.md"));

        var exception = Record.Exception(() => report.Should().HaveDisclosedEfficiently(90));
        Assert.Null(exception);
    }

    [Fact]
    public void HaveDisclosedEfficiently_OrphanedRead_FailsHighThreshold()
    {
        var report = new ToolUsageReport();
        // A read with NO preceding load — a progressive-disclosure violation the metric penalizes.
        report.AddCall(ReadCall(1, "expense-report", "resources/policy.md"));

        var exception = Record.Exception(() => report.Should().HaveDisclosedEfficiently(90));
        Assert.NotNull(exception);
    }

    // ------------------------------------------------------------------------------------------
    // HaveCorrectlyDeclinedSkill
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void HaveCorrectlyDeclinedSkill_SkillNeverLoaded_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        var exception = Record.Exception(() => report.Should().HaveCorrectlyDeclinedSkill("expense-report"));
        Assert.Null(exception);
    }

    [Fact]
    public void HaveCorrectlyDeclinedSkill_SkillWasLoaded_Throws()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "expense-report"));

        var exception = Record.Exception(() => report.Should().HaveCorrectlyDeclinedSkill("expense-report"));
        Assert.NotNull(exception);
    }

    [Fact]
    public void HaveCorrectlyDeclinedSkill_OtherSkillLoaded_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(LoadCall(1, "invoice-helper"));

        var exception = Record.Exception(() => report.Should().HaveCorrectlyDeclinedSkill("expense-report"));
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

    private static ToolCallRecord RunCallWithArgs(int order, string skillName, string scriptName, IDictionary<string, object?> scriptArgs) => new()
    {
        Name = SkillToolNames.RunSkillScript,
        CallId = $"call-{order}",
        Order = order,
        Arguments = new Dictionary<string, object?>
        {
            [SkillToolNames.SkillNameArg] = skillName,
            [SkillToolNames.ScriptNameArg] = scriptName,
            [SkillToolNames.ScriptArgumentsArg] = scriptArgs,
        },
    };
}

/// <summary>Test-only helper mirroring the internal value-based skillName match, for ForSkill assertions.</summary>
internal static class SkillArgumentExtractionTestHook
{
    public static bool SkillNameMatches(ToolCallRecord call, string skillName) =>
        call.Arguments is not null
        && call.Arguments.TryGetValue(SkillToolNames.SkillNameArg, out var v)
        && string.Equals(v as string, skillName, StringComparison.OrdinalIgnoreCase);
}
