// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Agents;

/// <summary>
/// Creates the <c>Presenter</c> agent for the TripPlanner Workflow (Demo 02).
/// Formats all collected trip data into a polished, publication-ready itinerary.
/// No tools required — this agent only writes.
/// </summary>
public static class PresenterAgentFactory
{
    public static ChatClientAgent Create(IChatClient chatClient) =>
        new(chatClient, new ChatClientAgentOptions
        {
            Name = "Presenter",
            Description = "Formats the complete trip plan into a polished itinerary document.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a professional travel itinerary writer.

                    Given all trip details (city info, flights, hotels):
                    1. Produce a beautiful, well-organised day-by-day itinerary.
                    2. Include flights, hotels, key activities, and practical tips.
                    3. Use clear headings, bullet points, and a friendly tone.

                    Deliver a publication-ready travel document.
                    """
            }
        });
}
