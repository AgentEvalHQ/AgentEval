// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;
using AgentEval.MAF.Evaluators;

namespace AgentEval.Tests.MAF.Evaluators;

public class ConversationExtractorTests
{
    [Fact]
    public void ExtractLastUserMessage_WithMultipleMessages_ReturnsLastUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First question"),
            new(ChatRole.Assistant, "First answer"),
            new(ChatRole.User, "Second question"),
            new(ChatRole.Assistant, "Second answer"),
        };

        var result = ConversationExtractor.ExtractLastUserMessage(messages);

        Assert.Equal("Second question", result);
    }

    [Fact]
    public void ExtractLastUserMessage_WithSingleUserMessage_ReturnsThatMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Only question"),
        };

        var result = ConversationExtractor.ExtractLastUserMessage(messages);

        Assert.Equal("Only question", result);
    }

    [Fact]
    public void ExtractLastUserMessage_WithNoUserMessages_ReturnsFirstMessageText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.Assistant, "Hello!"),
        };

        var result = ConversationExtractor.ExtractLastUserMessage(messages);

        Assert.Equal("System prompt", result);
    }

    [Fact]
    public void ExtractLastUserMessage_WithEmptyList_ReturnsEmptyString()
    {
        var result = ConversationExtractor.ExtractLastUserMessage(new List<ChatMessage>());
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractLastUserMessage_WithNull_ReturnsEmptyString()
    {
        var result = ConversationExtractor.ExtractLastUserMessage(null!);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractAllUserMessages_ConcatenatesAllUserMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First"),
            new(ChatRole.Assistant, "Answer"),
            new(ChatRole.User, "Second"),
        };

        var result = ConversationExtractor.ExtractAllUserMessages(messages);

        Assert.Equal("First\nSecond", result);
    }

    [Fact]
    public void ExtractToolUsage_WithPairedCalls_ReturnsPairedRecords()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Search for flights"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "SearchFlights",
                new Dictionary<string, object?> { ["destination"] = "Paris" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "3 flights found")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Found 3 flights to Paris.")]);

        var report = ConversationExtractor.ExtractToolUsage(messages, response);

        Assert.NotNull(report);
        Assert.Equal(1, report!.Count);
        Assert.Equal("SearchFlights", report.Calls[0].Name);
        Assert.Equal("3 flights found", report.Calls[0].Result?.ToString());
        Assert.False(report.Calls[0].HasTiming); // No timing in light path
    }

    [Fact]
    public void ExtractToolUsage_WithMultipleTools_PreservesOrder()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Plan trip"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("c1", "SearchFlights"),
                new FunctionCallContent("c2", "SearchHotels"),
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("c1", "flights"),
                new FunctionResultContent("c2", "hotels"),
            ]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")]);

        var report = ConversationExtractor.ExtractToolUsage(messages, response);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Count);
        Assert.Equal("SearchFlights", report.Calls[0].Name);
        Assert.Equal("SearchHotels", report.Calls[1].Name);
        Assert.Equal(1, report.Calls[0].Order);
        Assert.Equal(2, report.Calls[1].Order);
    }

    [Fact]
    public void ExtractToolUsage_WithNoToolCalls_ReturnsNull()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hi!")]);

        var report = ConversationExtractor.ExtractToolUsage(messages, response);

        Assert.Null(report);
    }

    [Fact]
    public void ExtractToolUsage_WithUnmatchedCall_IncludesAsPending()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("orphan", "OrphanTool")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")]);

        var report = ConversationExtractor.ExtractToolUsage(messages, response);

        Assert.NotNull(report);
        Assert.Equal(1, report!.Count);
        Assert.Equal("OrphanTool", report.Calls[0].Name);
        Assert.Null(report.Calls[0].Result);
    }

    [Fact]
    public void ExtractToolUsage_WithToolResult_CapturesResult()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "MyTool")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "result data")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")]);

        var report = ConversationExtractor.ExtractToolUsage(messages, response);

        Assert.NotNull(report);
        Assert.Equal(1, report!.Count);
        Assert.Equal("MyTool", report.Calls[0].Name);
        Assert.Equal("result data", report.Calls[0].Result?.ToString());
    }
}
