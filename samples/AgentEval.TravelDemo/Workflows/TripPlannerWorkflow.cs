// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AgentEval.TravelDemo.Agents;

namespace AgentEval.TravelDemo.Workflows;

/// <summary>
/// Assembles the TripPlanner sequential workflow:
/// <c>TripPlanner → FlightReservation → HotelReservation → Presenter</c>
/// </summary>
public static class TripPlannerWorkflow
{
    /// <summary>
    /// Builds the workflow and returns it together with the ordered executor IDs.
    /// </summary>
    /// <param name="build">Optional builder used by evaluation hosts to attach owned instrumentation.</param>
    public static (Workflow Workflow, string[] ExecutorIds) Create(Func<WorkflowBuilder, Workflow>? build = null)
    {
        // One chat client on the configured provider, shared by all four agents.
        var chatClient = Config.CreateChatClient();

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
        var workflowBuilder = new WorkflowBuilder(tripPlannerExecutor)
            .AddEdge(tripPlannerExecutor, flightReservationExecutor)
            .AddEdge(flightReservationExecutor, hotelReservationExecutor)
            .AddEdge(hotelReservationExecutor, presenterExecutor)
            .WithOutputFrom(presenterExecutor)
            .WithName("TripPlanner")
            .WithDescription("Trip planning pipeline: plan → flights → hotels → present");
        var workflow = build is null ? workflowBuilder.Build() : build(workflowBuilder);

        return (workflow, ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"]);
    }
}
