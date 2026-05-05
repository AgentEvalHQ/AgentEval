// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ECS2026MAF.Agents;

namespace ECS2026MAF.Workflows;

/// <summary>
/// Assembles the TripPlanner sequential workflow:
/// <c>TripPlanner → FlightReservation → HotelReservation → Presenter</c>
/// </summary>
public static class TripPlannerWorkflow
{
    /// <summary>
    /// Builds the workflow and returns it together with the ordered executor IDs.
    /// </summary>
    public static (Workflow Workflow, string[] ExecutorIds) Create()
    {
        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var chatClient  = azureClient.GetChatClient(Config.Model).AsIChatClient();

        // ── Create agents ────────────────────────────────────────────────────
        var tripPlanner       = TripPlannerAgentFactory.Create(chatClient);
        var flightReservation = FlightReservationAgentFactory.Create(chatClient);
        var hotelReservation  = HotelReservationAgentFactory.Create(chatClient);
        var presenter         = PresenterAgentFactory.Create(chatClient);

        // ── Bind as workflow executors ───────────────────────────────────────
        var tripPlannerExecutor       = tripPlanner.BindAsExecutor(emitEvents: true);
        var flightReservationExecutor = flightReservation.BindAsExecutor(emitEvents: true);
        var hotelReservationExecutor  = hotelReservation.BindAsExecutor(emitEvents: true);
        var presenterExecutor         = presenter.BindAsExecutor(emitEvents: true);

        // ── Wire sequential pipeline ─────────────────────────────────────────
        var workflow = new WorkflowBuilder(tripPlannerExecutor)
            .AddEdge(tripPlannerExecutor, flightReservationExecutor)
            .AddEdge(flightReservationExecutor, hotelReservationExecutor)
            .AddEdge(hotelReservationExecutor, presenterExecutor)
            .WithOutputFrom(presenterExecutor)
            .WithName("TripPlanner")
            .WithDescription("Trip planning pipeline: plan → flights → hotels → present")
            .Build();

        return (workflow, ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"]);
    }
}
