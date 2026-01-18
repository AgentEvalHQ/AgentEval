// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Core;
using AgentEval.NuGetConsumer.Tools;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Factory for creating testable agents in mock or real mode.
/// </summary>
public static class AgentFactory
{
    /// <summary>
    /// Creates a travel booking agent with tools.
    /// </summary>
    /// <param name="useMock">True for mock mode (no LLM calls), false for real Azure OpenAI.</param>
    public static ITestableAgent CreateTravelAgent(bool useMock)
    {
        var chatClient = useMock 
            ? CreateMockChatClient() 
            : CreateRealChatClient();

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TravelBookingAgent",
            Instructions = """
                You are a helpful travel booking assistant.
                Use the available tools to search for flights, book them, and send confirmations.
                Always search for flights before booking.
                After booking, always send a confirmation email.
                Be concise and helpful.
                """,
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(TravelTools.SearchFlights),
                    AIFunctionFactory.Create(TravelTools.BookFlight),
                    AIFunctionFactory.Create(TravelTools.SendConfirmation),
                    AIFunctionFactory.Create(TravelTools.GetUserConfirmation),
                    AIFunctionFactory.Create(TravelTools.CancelBooking)
                ]
            }
        });

        return new MAFAgentAdapter(agent);
    }

    /// <summary>
    /// Creates a calculator agent with a single tool.
    /// </summary>
    /// <param name="useMock">True for mock mode, false for real Azure OpenAI.</param>
    public static ITestableAgent CreateCalculatorAgent(bool useMock)
    {
        var chatClient = useMock 
            ? CreateMockChatClient() 
            : CreateRealChatClient();

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "CalculatorAgent",
            Instructions = "You are a math assistant. Always use the Calculator tool for calculations.",
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(CalculatorTool.Calculate)]
            }
        });

        return new MAFAgentAdapter(agent);
    }

    private static IChatClient CreateRealChatClient()
    {
        if (!Config.IsConfigured)
            throw new InvalidOperationException("Azure OpenAI is not configured. Set environment variables.");

        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        return azureClient.GetChatClient(Config.Model).AsIChatClient();
    }

    private static IChatClient CreateMockChatClient()
    {
        // Returns a mock response simulating a travel booking flow
        return new MockChatClient("""
            I found 5 flights to Paris for March 15th, 2026. The cheapest option is €259 
            with Iberia (IB7890). I've booked 2 passengers on flight AF1234 (Air France) 
            and sent a confirmation email to user@example.com. 
            Your booking reference is BK123456.
            """);
    }
}

/// <summary>
/// Simple mock chat client for demo without Azure credentials.
/// </summary>
internal class MockChatClient(string response) : IChatClient
{
    public ChatClientMetadata Metadata => new("MockChatClient");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var message = new ChatMessage(ChatRole.Assistant, response);
        return Task.FromResult(new ChatResponse(message));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not implemented in mock");
    }

    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}
