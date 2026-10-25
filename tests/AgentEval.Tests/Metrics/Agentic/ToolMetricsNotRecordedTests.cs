// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Skills;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// T58: the code-based tool metrics read a null <see cref="EvaluationContext.ToolUsage"/> — nothing recorded the run's
/// tool calls (tool tracking off, the MAF light path with no messages, a dataset row with no trace) — as "no tools
/// called" and scored it: PASS 100 for arguments, success, efficiency and skill disclosure, a measured 0 for selection.
/// Not recorded is not measured; a recorded empty report stays a real result. Tool success also counted a call that
/// was emitted but never ran as a success, and tool efficiency credited an untimed run with a perfect duration.
/// Every test here except the guards fails on the old code.
/// </summary>
public class ToolMetricsNotRecordedTests
{
    private static EvaluationContext Context(ToolUsageReport? toolUsage) =>
        new() { Input = "Find flights to Paris", Output = "Here are three flights.", ToolUsage = toolUsage };

    private static IEnumerable<IMetric> ToolMetrics() =>
    [
        new ToolSelectionMetric(["search_flights"]),
        new ToolSelectionMetric([]),
        new ToolArgumentsMetric(new Dictionary<string, IEnumerable<string>> { ["search_flights"] = ["destination"] }),
        new ToolSuccessMetric(),
        new ToolEfficiencyMetric(),
        new SkillDisclosureEfficiencyMetric(),
    ];

    private static ToolCallRecord Call(string name, int order, bool executed = true, Exception? error = null) => new()
    {
        Name = name,
        CallId = $"call-{order}",
        Order = order,
        WasExecuted = executed,
        Exception = error,
    };

    private static ToolUsageReport Report(params ToolCallRecord[] calls)
    {
        var report = new ToolUsageReport();
        foreach (var call in calls) report.AddCall(call);
        return report;
    }

    // ── Not recorded ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryToolMetric_NoToolDataRecorded_IsNotMeasured_NeitherAPassNorAZero()
    {
        foreach (var metric in ToolMetrics())
        {
            var result = await metric.EvaluateAsync(Context(toolUsage: null));

            Assert.False(result.Measured, metric.Name);
            Assert.False(result.Passed, metric.Name);
            Assert.Contains("not measured", result.Explanation!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARecordedRunWithNoToolCalls_IsARealResult()
    {
        // Guard (passes before and after): an empty report is an observation, not an absence.
        var expectedButNotCalled = await new ToolSelectionMetric(["search_flights"]).EvaluateAsync(Context(new ToolUsageReport()));
        var success = await new ToolSuccessMetric().EvaluateAsync(Context(new ToolUsageReport()));
        var efficiency = await new ToolEfficiencyMetric().EvaluateAsync(Context(new ToolUsageReport()));

        Assert.True(expectedButNotCalled.Measured);
        Assert.False(expectedButNotCalled.Passed);
        Assert.True(success.Measured);
        Assert.True(success.Passed);
        Assert.True(efficiency.Measured);
        Assert.True(efficiency.Passed);
    }

    [Fact]
    public async Task Cli_EvalMetrics_ARunWithNoToolData_ScoresCodeToolSuccessAsNotMeasured()
    {
        // `agenteval eval --metrics code_tool_success` against a target that recorded no tool calls.
        var context = EvalCommand.ToMetricsContext(
            new DatasetTestCase { Id = "t1", Input = "hi" },
            new TestResult { TestName = "t1", ActualOutput = "hello", ToolUsage = null });

        var result = await MetricCatalog.Resolve("code_tool_success", null).EvaluateAsync(context);

        Assert.False(result.Measured);
        Assert.False(result.Passed);
    }

    // ── Tool success reads whether the call ran ──────────────────────────────

    [Fact]
    public async Task ToolSuccess_ACallThatWasEmittedButNeverRan_IsNotASuccess()
    {
        var result = await new ToolSuccessMetric().EvaluateAsync(Context(Report(
            Call("search_flights", 1, executed: true),
            Call("book_flight", 2, executed: false))));

        Assert.True(result.Measured);
        Assert.Equal(50, result.Score);
        Assert.False(result.Passed);
        Assert.Equal(1, result.Details!["successfulCalls"]);
        Assert.Contains("book_flight", (IEnumerable<string>)result.Details["notExecutedCalls"]);
        Assert.Contains("never ran", result.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolSuccess_ARejectedApproval_IsNotASuccess()
    {
        // The framework answers a rejected approval with a generated result and no exception, so HasError is false:
        // the old metric scored this call a success. It never ran (WasExecuted false).
        var request = new ToolApprovalRequestContent("req-1", new FunctionCallContent("c1", "PlaceOrder"));
        var report = ToolUsageExtractor.Extract(new List<object>
        {
            new ChatMessage(ChatRole.Assistant, [request]),
            new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false, reason: "not today")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "Tool call invocation rejected.")]),
        }, includeApprovalGatedCalls: true);

        var result = await new ToolSuccessMetric().EvaluateAsync(Context(report));

        Assert.True(result.Measured);
        Assert.Equal(0, result.Score);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ToolSuccess_ExecutedCallsWithoutError_AreSuccesses()
    {
        // Guard (passes before and after).
        var result = await new ToolSuccessMetric().EvaluateAsync(Context(Report(
            Call("search_flights", 1), Call("book_flight", 2))));

        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }

    // ── Tool efficiency does not credit a duration nobody timed ─────────────

    [Fact]
    public async Task ToolEfficiency_UntimedCalls_ScoreOnTheCallCountAlone()
    {
        // 15 distinct untimed calls against a budget of 10: the call count scores 50. The old metric added a perfect 100
        // for the untimed 0s total and averaged them, 75 = PASS.
        var calls = Enumerable.Range(1, 15).Select(i => Call($"tool_{i}", i)).ToArray();

        var result = await new ToolEfficiencyMetric(maxExpectedCalls: 10).EvaluateAsync(Context(Report(calls)));

        Assert.True(result.Measured);
        Assert.Equal(50, result.Score);
        Assert.False(result.Passed);
        Assert.Equal(false, result.Details!["durationMeasured"]);
        Assert.Contains("duration not measured", result.Explanation!, StringComparison.Ordinal);
    }

    // ── Skill disclosure ─────────────────────────────────────────────────────

    [Fact]
    public async Task SkillDisclosure_AnExpectedSkillThatWasNeverLoaded_IsAMeasuredMiss_NotAPass()
    {
        var context = Context(Report(Call("search_flights", 1)));
        context.SetProperty("expected_skills", (IReadOnlyList<string>)new List<string> { "billing" });

        var result = await new SkillDisclosureEfficiencyMetric().EvaluateAsync(context);

        Assert.True(result.Measured);
        Assert.False(result.Passed);
        Assert.Equal(0, result.Score);
        Assert.Equal(0.0, (double)result.Details!["selectionRecall"], precision: 5);
    }

    [Fact]
    public async Task SkillDisclosure_NoSkillCalled_AndNoExpectation_IsNotMeasured()
    {
        var result = await new SkillDisclosureEfficiencyMetric().EvaluateAsync(Context(Report(Call("search_flights", 1))));

        Assert.False(result.Measured);
        Assert.False(result.Passed);
    }

    [Fact]
    public void HaveDisclosedEfficiently_WhenNothingWasMeasured_IsInconclusive_InACollectingScope()
    {
        var report = Report(Call("search_flights", 1));

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveDisclosedEfficiently(80);
        scope.Dispose();

        var outcome = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Inconclusive, outcome.Outcome);
    }

    [Fact]
    public void HaveDisclosedEfficiently_WhenNothingWasMeasured_FailsClosed_WhenThrown()
    {
        var report = Report(Call("search_flights", 1));

        var exception = Record.Exception(() => report.Should().HaveDisclosedEfficiently(80));

        Assert.NotNull(exception);
        Assert.Contains("not measured", exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HaveDisclosedEfficiently_ACleanFunnel_StillPasses()
    {
        // Guard (passes before and after).
        var report = Report(
            new ToolCallRecord
            {
                Name = SkillToolNames.LoadSkill, CallId = "c1", Order = 1, WasExecuted = true,
                Arguments = new Dictionary<string, object?> { [SkillToolNames.SkillNameArg] = "billing" },
            },
            new ToolCallRecord
            {
                Name = SkillToolNames.ReadSkillResource, CallId = "c2", Order = 2, WasExecuted = true,
                Arguments = new Dictionary<string, object?> { [SkillToolNames.SkillNameArg] = "billing", [SkillToolNames.ResourceNameArg] = "policy.md" },
            });

        Assert.Null(Record.Exception(() => report.Should().HaveDisclosedEfficiently(90)));
    }
}
