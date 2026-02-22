// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

public class ConversationalTestCaseTests
{
    #region Turn Tests

    [Fact]
    public void Turn_User_CreatesUserRole()
    {
        var turn = Turn.User("Hello");
        
        Assert.Equal("user", turn.Role);
        Assert.Equal("Hello", turn.Content);
        Assert.Null(turn.ToolCalls);
    }

    [Fact]
    public void Turn_Assistant_CreatesAssistantRole()
    {
        var turn = Turn.Assistant("Hi there!");
        
        Assert.Equal("assistant", turn.Role);
        Assert.Equal("Hi there!", turn.Content);
        Assert.Null(turn.ToolCalls);
    }

    [Fact]
    public void Turn_Assistant_WithToolCalls()
    {
        var toolCall = new ToolCallInfo("get_weather", new Dictionary<string, object?> { ["city"] = "Seattle" });
        var turn = Turn.Assistant("Let me check the weather.", toolCall);
        
        Assert.Equal("assistant", turn.Role);
        Assert.NotNull(turn.ToolCalls);
        Assert.Single(turn.ToolCalls);
        Assert.Equal("get_weather", turn.ToolCalls[0].Name);
    }

    [Fact]
    public void Turn_System_CreatesSystemRole()
    {
        var turn = Turn.System("You are a helpful assistant.");
        
        Assert.Equal("system", turn.Role);
        Assert.Equal("You are a helpful assistant.", turn.Content);
    }

    [Fact]
    public void Turn_Tool_CreatesToolRole()
    {
        var turn = Turn.Tool("{\"temp\": 72}", "call_123");
        
        Assert.Equal("tool", turn.Role);
        Assert.Equal("{\"temp\": 72}", turn.Content);
        Assert.Equal("call_123", turn.ToolCallId);
    }

    #endregion

    #region Builder Tests

    [Fact]
    public void Builder_Create_SetsName()
    {
        var testCase = ConversationalTestCase.Create("Weather Query Test").Build();
        
        Assert.Equal("Weather Query Test", testCase.Name);
        Assert.NotNull(testCase.Id);
    }

    [Fact]
    public void Builder_WithId_SetsCustomId()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .WithId("custom-id-123")
            .Build();
        
        Assert.Equal("custom-id-123", testCase.Id);
    }

    [Fact]
    public void Builder_WithDescription_SetsDescription()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .WithDescription("Tests weather queries")
            .Build();
        
        Assert.Equal("Tests weather queries", testCase.Description);
    }

    [Fact]
    public void Builder_InCategory_SetsCategory()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .InCategory("weather")
            .Build();
        
        Assert.Equal("weather", testCase.Category);
    }

    [Fact]
    public void Builder_WithSystemPrompt_AddsSystemTurnFirst()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Hello")
            .WithSystemPrompt("You are helpful")
            .Build();
        
        Assert.Equal(2, testCase.Turns.Count);
        Assert.Equal("system", testCase.Turns[0].Role);
        Assert.Equal("user", testCase.Turns[1].Role);
    }

    [Fact]
    public void Builder_AddUserTurn_AddsUserTurn()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("What's the weather?")
            .AddUserTurn("And tomorrow?")
            .Build();
        
        Assert.Equal(2, testCase.Turns.Count);
        Assert.All(testCase.Turns, t => Assert.Equal("user", t.Role));
    }

    [Fact]
    public void Builder_ExpectTools_SetsExpectedTools()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .ExpectTools("get_weather", "format_response")
            .Build();
        
        Assert.NotNull(testCase.ExpectedTools);
        Assert.Equal(2, testCase.ExpectedTools.Count);
        Assert.Contains("get_weather", testCase.ExpectedTools);
    }

    [Fact]
    public void Builder_ExpectOutcome_SetsOutcome()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .ExpectOutcome("Response contains temperature")
            .Build();
        
        Assert.Equal("Response contains temperature", testCase.ExpectedOutcome);
    }

    [Fact]
    public void Builder_WithMaxDuration_SetsDuration()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .WithMaxDuration(TimeSpan.FromSeconds(30))
            .Build();
        
        Assert.Equal(TimeSpan.FromSeconds(30), testCase.MaxDuration);
    }

    [Fact]
    public void Builder_WithMetadata_AddsMetadata()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .WithMetadata("priority", "high")
            .WithMetadata("version", 2)
            .Build();
        
        Assert.Equal("high", testCase.Metadata["priority"]);
        Assert.Equal(2, testCase.Metadata["version"]);
    }

    [Fact]
    public void Builder_CompleteConversation_BuildsCorrectly()
    {
        var testCase = ConversationalTestCase.Create("Multi-turn Weather")
            .WithId("weather-001")
            .WithDescription("Tests a multi-turn weather conversation")
            .InCategory("weather")
            .WithSystemPrompt("You are a weather assistant.")
            .AddUserTurn("What's the weather in Seattle?")
            .AddAssistantTurn("Let me check that for you.", 
                new ToolCallInfo("get_weather", new Dictionary<string, object?> { ["city"] = "Seattle" }, "call_1"))
            .AddToolResponse("{\"temp\": 65, \"condition\": \"cloudy\"}", "call_1")
            .AddUserTurn("What about tomorrow?")
            .ExpectTools("get_weather")
            .WithMaxDuration(TimeSpan.FromSeconds(60))
            .Build();

        Assert.Equal("weather-001", testCase.Id);
        Assert.Equal("Multi-turn Weather", testCase.Name);
        Assert.Equal("weather", testCase.Category);
        Assert.Equal(5, testCase.Turns.Count);
        Assert.Single(testCase.ExpectedTools!);
        Assert.Equal(TimeSpan.FromSeconds(60), testCase.MaxDuration);
    }

    #endregion

    #region ConversationResult Tests

    [Fact]
    public void ConversationResult_DefaultValues()
    {
        var testCase = ConversationalTestCase.Create("Test").Build();
        var result = new ConversationResult { TestCase = testCase };
        
        Assert.False(result.Success);
        Assert.Empty(result.ActualTurns);
        Assert.Empty(result.ToolsCalled);
        Assert.Null(result.Error);
    }

    [Fact]
    public void AssertionResult_RecordEquality()
    {
        var a1 = new AssertionResult("Test", true, "message");
        var a2 = new AssertionResult("Test", true, "message");
        
        Assert.Equal(a1, a2);
    }

    #endregion

    #region ConversationCompletenessMetric Tests

    [Fact]
    public void CompletenessMetric_PerfectConversation_ScoresHigh()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Hello")
            .ExpectTools("greet")
            .Build();

        var result = new ConversationResult
        {
            TestCase = testCase,
            Success = true,
            ActualTurns = new List<Turn>
            {
                Turn.User("Hello"),
                Turn.Assistant("Hi there!")
            },
            ToolsCalled = new List<string> { "greet" },
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var metric = new ConversationCompletenessMetric();
        var metricResult = metric.Evaluate(result);

        Assert.True(metricResult.Score >= 0.9);
        Assert.True(metricResult.Passed);
        Assert.Equal(1.0, metricResult.SubScores["ResponseRate"]);
        Assert.Equal(1.0, metricResult.SubScores["ToolUsage"]);
    }

    [Fact]
    public void CompletenessMetric_MissingResponse_LowerScore()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Hello")
            .AddUserTurn("Goodbye")
            .Build();

        var result = new ConversationResult
        {
            TestCase = testCase,
            ActualTurns = new List<Turn>
            {
                Turn.User("Hello"),
                Turn.Assistant("Hi!"),
                Turn.User("Goodbye")
                // Missing second assistant response
            },
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var metric = new ConversationCompletenessMetric();
        var metricResult = metric.Evaluate(result);

        Assert.Equal(0.5, metricResult.SubScores["ResponseRate"]);
    }

    [Fact]
    public void CompletenessMetric_MissingTools_LowerScore()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Get weather")
            .ExpectTools("get_weather", "format_response")
            .Build();

        var result = new ConversationResult
        {
            TestCase = testCase,
            ActualTurns = new List<Turn>
            {
                Turn.User("Get weather"),
                Turn.Assistant("Here's the weather")
            },
            ToolsCalled = new List<string> { "get_weather" }, // Missing format_response
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var metric = new ConversationCompletenessMetric();
        var metricResult = metric.Evaluate(result);

        Assert.Equal(0.5, metricResult.SubScores["ToolUsage"]);
    }

    [Fact]
    public void CompletenessMetric_ExceededDuration_LowerScore()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Hello")
            .WithMaxDuration(TimeSpan.FromMilliseconds(100))
            .Build();

        var result = new ConversationResult
        {
            TestCase = testCase,
            ActualTurns = new List<Turn>
            {
                Turn.User("Hello"),
                Turn.Assistant("Hi!")
            },
            Duration = TimeSpan.FromMilliseconds(150) // 1.5x over limit
        };

        var metric = new ConversationCompletenessMetric();
        var metricResult = metric.Evaluate(result);

        Assert.True(metricResult.SubScores["DurationCompliance"] < 1.0);
    }

    [Fact]
    public void CompletenessMetric_WithError_NotPassed()
    {
        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Hello")
            .Build();

        var result = new ConversationResult
        {
            TestCase = testCase,
            ActualTurns = new List<Turn> { Turn.User("Hello") },
            Error = "Connection timeout",
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var metric = new ConversationCompletenessMetric();
        var metricResult = metric.Evaluate(result);

        Assert.False(metricResult.Passed);
        Assert.Equal(0.0, metricResult.SubScores["ErrorFree"]);
    }

    [Fact]
    public void CompletenessMetric_GetAggregateStats_CalculatesCorrectly()
    {
        var testCase1 = ConversationalTestCase.Create("Test1").AddUserTurn("Hi").Build();
        var testCase2 = ConversationalTestCase.Create("Test2").AddUserTurn("Hello").Build();

        var results = new List<ConversationResult>
        {
            new()
            {
                TestCase = testCase1,
                Success = true,
                ActualTurns = new List<Turn> { Turn.User("Hi"), Turn.Assistant("Hello") },
                Duration = TimeSpan.FromMilliseconds(100)
            },
            new()
            {
                TestCase = testCase2,
                Success = false,
                ActualTurns = new List<Turn> { Turn.User("Hello") },
                Error = "Failed",
                Duration = TimeSpan.FromMilliseconds(200)
            }
        };

        var metric = new ConversationCompletenessMetric();
        var stats = metric.GetAggregateStats(results);

        Assert.Equal(2, stats.TotalConversations);
        Assert.Equal(1, stats.SuccessfulConversations);
        Assert.Equal(0.5, stats.SuccessRate);
        Assert.Equal(1, stats.ErrorCount);
        Assert.Equal(TimeSpan.FromMilliseconds(150), stats.AverageDuration);
    }

    [Fact]
    public void CompletenessMetric_EmptyResults_HandlesGracefully()
    {
        var metric = new ConversationCompletenessMetric();
        var stats = metric.GetAggregateStats(Enumerable.Empty<ConversationResult>());

        Assert.Equal(0, stats.TotalConversations);
        Assert.Equal(0.0, stats.SuccessRate);
    }

    #endregion

    #region ToolCallInfo Tests

    [Fact]
    public void ToolCallInfo_BasicConstruction()
    {
        var toolCall = new ToolCallInfo("search");
        
        Assert.Equal("search", toolCall.Name);
        Assert.Null(toolCall.Arguments);
        Assert.Null(toolCall.Id);
    }

    [Fact]
    public void ToolCallInfo_WithArguments()
    {
        var args = new Dictionary<string, object?> { ["query"] = "weather", ["limit"] = 10 };
        var toolCall = new ToolCallInfo("search", args, "call_abc");
        
        Assert.Equal("search", toolCall.Name);
        Assert.Equal("weather", toolCall.Arguments!["query"]);
        Assert.Equal(10, toolCall.Arguments["limit"]);
        Assert.Equal("call_abc", toolCall.Id);
    }

    #endregion
}
