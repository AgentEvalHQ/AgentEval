// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ECS2026MAF.Tools;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Agents;

/// <summary>
/// Creates the <c>TravelBookingAgent</c> used in Demo 01.
/// The agent has access to all travel-related tools and books trips end-to-end.
/// </summary>
public static class TravelAgentFactory
{
    /// <summary>Creates a <see cref="AIAgent"/> connected to Azure OpenAI.</summary>
    public static AIAgent Create()
    {
        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var chatClient  = azureClient.GetChatClient(Config.Model).AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TravelBookingAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a helpful travel booking assistant.
                    Always use the SearchFlights tool before booking a flight.
                    After booking, send a confirmation email with SendConfirmation.
                    Be concise and professional.
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
}
