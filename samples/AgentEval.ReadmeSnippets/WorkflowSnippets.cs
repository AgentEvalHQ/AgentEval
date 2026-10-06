// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: multi-agent workflow evaluation.</summary>
public static class WorkflowSnippets
{
    public static async Task Workflow(IWorkflowEvaluableAgent workflowAdapter)
    {
        // begin-snippet: workflow
        var testCase = new WorkflowTestCase
        {
            Name              = "TripPlanner — Tokyo & Beijing",
            Input             = "Plan a 7-day trip to Tokyo and Beijing — flights and hotels",
            ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
            StrictExecutorOrder = true,
            ExpectedTools     = ["SearchFlights", "BookFlight", "BookHotel"],
            MaxDuration       = TimeSpan.FromMinutes(2),
        };

        var harness = new WorkflowEvaluationHarness();
        var result  = await harness.RunWorkflowTestAsync(workflowAdapter, testCase);

        result.ExecutionResult!.Should()
            .HaveSucceeded(because: "the trip must be planned end-to-end")
            .HaveExecutedInOrder("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
            .HaveAnyExecutorCalledTool("SearchFlights")
            .HaveAnyExecutorCalledTool("BookHotel")
            .HaveTraversedEdge("TripPlanner", "FlightReservation")
            .HaveCompletedWithin(TimeSpan.FromMinutes(2))
            .HaveNoToolErrors();
        // end-snippet
    }
}
