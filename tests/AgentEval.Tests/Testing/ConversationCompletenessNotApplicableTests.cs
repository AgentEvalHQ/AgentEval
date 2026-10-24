// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// A completeness dimension the test case does not ask for is not applicable: it is not scored as perfect, and it
/// cannot lift a conversation that left most turns unanswered to a pass.
/// </summary>
public class ConversationCompletenessNotApplicableTests
{
    [Fact]
    public void NoToolsAndNoMaxDuration_AreLeftOut_SoUnansweredTurnsFail()
    {
        // 5 user turns, 2 answered. With tools and duration scored as perfect this read 0.76, a pass.
        var result = Conversation(
            ConversationalTestCase.Create("t")
                .AddUserTurn("1").AddUserTurn("2").AddUserTurn("3").AddUserTurn("4").AddUserTurn("5")
                .Build(),
            assistantTurns: 2);

        var metric = new ConversationCompletenessMetric().Evaluate(result);

        // (0.4 * 0.4 + 0.15 * 1.0) / (0.4 + 0.15)
        Assert.Equal(0.31 / 0.55, metric.Score, precision: 6);
        Assert.False(metric.Passed);
        Assert.False(metric.SubScores.ContainsKey("ToolUsage"));
        Assert.False(metric.SubScores.ContainsKey("DurationCompliance"));
        Assert.Equal(0.4, metric.SubScores["ResponseRate"]);
    }

    [Fact]
    public void ExpectedToolsCalled_AndEveryTurnAnswered_StillPasses()
    {
        // Guard: a complete conversation passes before and after.
        var result = Conversation(
            ConversationalTestCase.Create("t").AddUserTurn("find it").ExpectTools("search").Build(),
            assistantTurns: 1,
            toolsCalled: ["search"]);

        var metric = new ConversationCompletenessMetric().Evaluate(result);

        Assert.True(metric.Passed);
        Assert.Equal(1.0, metric.Score, precision: 6);
        Assert.Equal(1.0, metric.SubScores["ToolUsage"]);
    }

    [Fact]
    public void MaxDurationSetAndMet_IsScored()
    {
        var result = Conversation(
            ConversationalTestCase.Create("t").AddUserTurn("hi").WithMaxDuration(TimeSpan.FromSeconds(1)).Build(),
            assistantTurns: 1);

        var metric = new ConversationCompletenessMetric().Evaluate(result);

        Assert.Equal(1.0, metric.SubScores["DurationCompliance"]);
    }

    [Fact]
    public void AggregateToolUsage_IsOverTheConversationsThatExpectedTools()
    {
        var noTools = Conversation(ConversationalTestCase.Create("a").AddUserTurn("hi").Build(), assistantTurns: 1);
        var halfTools = Conversation(
            ConversationalTestCase.Create("b").AddUserTurn("go").ExpectTools("search", "book").Build(),
            assistantTurns: 1,
            toolsCalled: ["search"]);

        var stats = new ConversationCompletenessMetric().GetAggregateStats([noTools, halfTools]);

        // Today 0.75: the conversation that expected no tools counted as a perfect 1.0.
        Assert.Equal(0.5, stats.AverageToolUsage);
        Assert.Equal(1, stats.ToolUsageMeasuredCount);
        Assert.Equal(2, stats.ResponseRateMeasuredCount);
    }

    [Fact]
    public void AggregateToolUsage_WhenNoConversationExpectedTools_IsNotMeasured()
    {
        var noTools = Conversation(ConversationalTestCase.Create("a").AddUserTurn("hi").Build(), assistantTurns: 1);

        var stats = new ConversationCompletenessMetric().GetAggregateStats([noTools]);

        Assert.Equal(0, stats.ToolUsageMeasuredCount);
        Assert.Equal(0, stats.AverageToolUsage);
    }

    private static ConversationResult Conversation(
        ConversationalTestCase testCase,
        int assistantTurns,
        List<string>? toolsCalled = null)
    {
        var turns = new List<Turn>();
        foreach (var userTurn in testCase.Turns)
        {
            turns.Add(Turn.User(userTurn.Content));
            if (turns.Count(t => t.Role == "assistant") < assistantTurns)
            {
                turns.Add(Turn.Assistant("ok"));
            }
        }

        return new ConversationResult
        {
            TestCase = testCase,
            Success = true,
            ActualTurns = turns,
            ToolsCalled = toolsCalled ?? [],
            Duration = TimeSpan.FromMilliseconds(100),
        };
    }
}
