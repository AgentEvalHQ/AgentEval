// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Core;
using AgentEval.Comparison;
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
    /// <param name="modelDeployment">Optional model deployment name (defaults to Config.Model).</param>
    public static IStreamableAgent CreateTravelAgent(bool useMock, string? modelDeployment = null)
    {
        var chatClient = useMock 
            ? CreateMockChatClient() 
            : CreateRealChatClient(modelDeployment);

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TravelBookingAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a helpful travel booking assistant.
                    You MUST use the available tools to search for flights, book them, and send confirmations.
                    When asked about flights, ALWAYS use the SearchFlights tool first.
                    Always search for flights before booking.
                    After booking, always send a confirmation email.
                    Be concise and helpful.
                    """,
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
    /// Creates a travel booking agent as a raw MAF <see cref="AIAgent"/> (no AgentEval wrapper).
    /// </summary>
    /// <param name="useMock">True for mock mode (no LLM calls), false for real Azure OpenAI.</param>
    /// <param name="modelDeployment">Optional model deployment name (defaults to Config.Model).</param>
    public static AIAgent CreateTravelAIAgent(bool useMock, string? modelDeployment = null)
    {
        var chatClient = useMock
            ? CreateMockChatClient()
            : CreateRealChatClient(modelDeployment);

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TravelBookingAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a helpful travel booking assistant.
                    You MUST use the available tools to search for flights, book them, and send confirmations.
                    When asked about flights, ALWAYS use the SearchFlights tool first.
                    Always search for flights before booking.
                    After booking, always send a confirmation email.
                    Be concise and helpful.
                    """,
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
    }

    /// <summary>
    /// Creates a calculator agent with a single tool.
    /// </summary>
    /// <param name="useMock">True for mock mode, false for real Azure OpenAI.</param>
    /// <param name="modelDeployment">Optional model deployment name (defaults to Config.Model).</param>
    public static IStreamableAgent CreateCalculatorAgent(bool useMock, string? modelDeployment = null)
    {
        var chatClient = useMock 
            ? CreateMockChatClient() 
            : CreateRealChatClient(modelDeployment);

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "CalculatorAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a math assistant. Always use the Calculator tool for calculations.",
                Tools = [AIFunctionFactory.Create(CalculatorTool.Calculate)]
            }
        });

        return new MAFAgentAdapter(agent);
    }

    /// <summary>
    /// Creates agent factories for model comparison testing.
    /// </summary>
    public static List<IAgentFactory> CreateCalculatorAgentFactories()
    {
        if (!Config.IsConfigured)
            throw new InvalidOperationException("Azure OpenAI is not configured.");

        return new List<IAgentFactory>
        {
            new DelegateAgentFactory(
                Config.Model,
                GetModelDisplayName(Config.Model),
                () => CreateCalculatorAgent(useMock: false, Config.Model)),
            new DelegateAgentFactory(
                Config.SecondaryModel,
                GetModelDisplayName(Config.SecondaryModel),
                () => CreateCalculatorAgent(useMock: false, Config.SecondaryModel))
        };
    }

    /// <summary>
    /// Creates a chat client for LLM-as-a-judge evaluation.
    /// </summary>
    public static IChatClient CreateEvaluatorChatClient()
    {
        if (!Config.IsConfigured)
            throw new InvalidOperationException("Azure OpenAI is not configured. Set environment variables.");

        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        // Use the primary model for evaluation (could also use a separate evaluator model)
        return azureClient.GetChatClient(Config.Model).AsIChatClient();
    }

    private static string GetModelDisplayName(string deployment) => deployment switch
    {
        "gpt-4o" => "GPT-4o",
        "gpt-4o-mini" => "GPT-4o Mini",
        "gpt-5-chat" => "GPT-5 Chat",
        "gpt-35-turbo" => "GPT-3.5 Turbo",
        _ => deployment
    };

    private static IChatClient CreateRealChatClient(string? modelDeployment = null)
    {
        if (!Config.IsConfigured)
            throw new InvalidOperationException("Azure OpenAI is not configured. Set environment variables.");

        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        return azureClient.GetChatClient(modelDeployment ?? Config.Model).AsIChatClient();
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
