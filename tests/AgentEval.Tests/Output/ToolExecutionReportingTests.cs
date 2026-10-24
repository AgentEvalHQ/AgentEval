// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Output;

public class ToolExecutionReportingTests
{
    [Theory]
    [InlineData("emitted", 0)]
    [InlineData("denied", 0)]
    [InlineData("success", 1)]
    [InlineData("error", 0)]
    public void WriteSummary_ExtractedToolOutcome_CountsOnlyExecutedSuccess(string state, int successes)
    {
        using var output = new StringWriter();
        new EvaluationOutputWriter(new VerbositySettings { Level = VerbosityLevel.Summary }, output)
            .WriteTestResult(Result(state));

        Assert.Contains($"{successes} succeeded", output.ToString());
        if (state is "emitted" or "denied")
            Assert.Contains("1 not executed", output.ToString());
    }

    [Theory]
    [InlineData("emitted", "○")]
    [InlineData("denied", "○")]
    [InlineData("success", "✓")]
    [InlineData("error", "✗")]
    public void WriteDetailed_ExtractedToolOutcome_DistinguishesIntentFromSuccess(string state, string icon)
    {
        using var output = new StringWriter();
        new EvaluationOutputWriter(new VerbositySettings { Level = VerbosityLevel.Detailed }, output)
            .WriteTestResult(Result(state));

        var row = Assert.Single(output.ToString().Split('\n'), line => line.Contains("[1] lookup"));
        Assert.Contains($"{icon} [1] lookup", row);
        if (state is "emitted" or "denied") Assert.Contains("not executed", row);
        if (state == "error") Assert.Contains("lookup failed", output.ToString());
    }

    [Theory]
    [InlineData("emitted", false)]
    [InlineData("denied", false)]
    [InlineData("success", true)]
    [InlineData("error", false)]
    public void PrintToolSummary_ExtractedToolOutcome_RequiresSuccessfulExecution(string state, bool succeeded)
    {
        var summary = Stochastic(state).GetToolUsageSummary("lookup");
        using var output = new StringWriter();
        TableFormatter.PrintToolSummary(summary, new OutputOptions { Writer = output, UseColors = false });

        Assert.Equal(succeeded, summary.AllCallsSucceeded);
        Assert.Equal(succeeded, output.ToString().Contains("100% Success"));
        if (state is "emitted" or "denied") Assert.Contains("1 not executed", output.ToString());
    }

    [Theory]
    [InlineData("emitted", "0%")]
    [InlineData("denied", "0%")]
    [InlineData("success", "100%")]
    [InlineData("error", "0%")]
    public void PrintComparison_ExtractedToolOutcome_ReportsExecutedSuccessRate(string state, string expected)
    {
        using var output = new StringWriter();
        TableFormatter.PrintComparisonTable([("subject", Stochastic(state))], new OutputOptions
        {
            Writer = output, UseColors = false, ShowToolSuccess = true,
        });
        var lines = output.ToString().Split('\n');
        var header = Assert.Single(lines, line => line.Contains("ToolOK%")).Split('|').Select(cell => cell.Trim()).ToArray();
        var row = Assert.Single(lines, line => line.TrimStart().StartsWith("subject ", StringComparison.Ordinal)).Split('|');

        Assert.Equal(expected, row[Array.IndexOf(header, "ToolOK%")].Trim());
    }

    [Fact]
    public void PrintToolSummary_LegacySummaryWithoutExecutionEvidence_WithholdsSuccess()
    {
        var summary = new ToolUsageSummary("lookup", 1, 0, 1, null, 1, 0);
        using var output = new StringWriter();
        TableFormatter.PrintToolSummary(summary, new OutputOptions { Writer = output, UseColors = false });

        Assert.False(summary.AllCallsSucceeded);
        Assert.DoesNotContain("100% Success", output.ToString());
        Assert.Contains("not recorded", output.ToString());
    }

    [Fact]
    public void PrintToolSummary_RecordedNoCalls_RemainsNotApplicable()
    {
        var summary = Stochastic("empty").GetToolUsageSummary("lookup");
        using var output = new StringWriter();
        TableFormatter.PrintToolSummary(summary, new OutputOptions { Writer = output });

        Assert.True(summary.AllCallsSucceeded);
        Assert.Contains("N/A (No calls made)", output.ToString());
        Assert.DoesNotContain("100% Success", output.ToString());
    }

    private static StochasticResult Stochastic(string state) => new(
        new TestCase { Name = "execution", Input = "lookup" }, [Result(state)], null, new StochasticOptions(), true);

    private static TestResult Result(string state)
    {
        var call = new FunctionCallContent("c1", "lookup");
        var request = new ToolApprovalRequestContent("approval1", call);
        var messages = new List<object>();
        if (state != "empty")
            messages.Add(new ChatMessage(ChatRole.Assistant, [state == "denied" ? request : call]));
        if (state == "denied")
        {
            messages.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false)]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "Tool call invocation rejected.")]));
        }
        else if (state is "success" or "error")
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", state == "success" ? null : "diagnostic")
            {
                Exception = state == "error" ? new InvalidOperationException("lookup failed") : null,
            }]));
        return new TestResult
        {
            TestName = "execution", Passed = true, Score = 100,
            ToolUsage = ToolUsageExtractor.Extract(messages, includeApprovalGatedCalls: true),
        };
    }
}
