// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.Models;
using ECS2026MAF.Workflows;

namespace ECS2026MAF.Evals;

/// <summary>
/// Eval 02 — TripPlanner Workflow Assertions
///
/// Uses AgentEval to verify that the TripPlanner Workflow:
/// - Executes all 4 agents in the correct order
/// - Calls the expected tools across the pipeline
/// - Completes within a reasonable time budget
/// - Produces non-empty output at every stage
///
/// The workflow factory is imported from ECS2026MAF — no duplication.
/// </summary>
public static class Eval02_TripPlannerEvals
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!ECS2026MAF.Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Building TripPlanner workflow + AgentEval harness...\n");

        // ── Workflow from ECS2026MAF (no duplication) ─────────────────────────
        var (workflow, executorIds) = TripPlannerWorkflow.Create();

        var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow,
            name: "TripPlanner",
            executorIds: executorIds,
            workflowType: "PromptChaining");

        // ── Test case ─────────────────────────────────────────────────────────
        var testCase = new WorkflowTestCase
        {
            Name              = "TripPlanner — Tokyo & Beijing",
            Input             = "Plan a 7-day trip visiting both Tokyo and Beijing. " +
                                "I need city information, flights between them, " +
                                "and hotel bookings for each city.",
            Description       = "End-to-end workflow validation with tool-calling agents",
            ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
            StrictExecutorOrder = true,
            MaxDuration       = TimeSpan.FromMinutes(5),
            ExpectedTools     = ["GetInfoAbout", "SearchFlights", "BookFlight", "BookHotel"],
            Tags              = ["trip-planner", "ecs2026", "workflow"]
        };

        Console.WriteLine($"  Running: \"{testCase.Name}\"\n");
        Console.WriteLine("  ⏳ This may take up to 2 minutes (4 LLM calls with tools)...\n");

        var harness     = new WorkflowEvaluationHarness(verbose: false);
        var testOptions = new WorkflowTestOptions
        {
            Timeout = TimeSpan.FromMinutes(5),
            Verbose = false
        };

        WorkflowTestResult testResult;
        try
        {
            testResult = await harness.RunWorkflowTestAsync(workflowAdapter, testCase, testOptions);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ Workflow execution failed: {ex.Message}");
            Console.ResetColor();
            return;
        }

        // ── Print summary ─────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"  Overall : {(testResult.Passed ? "✅ PASSED" : "❌ FAILED")}");

        if (testResult.ExecutionResult is { } result)
        {
            Console.WriteLine($"  Steps   : {result.Steps.Count}");
            Console.WriteLine($"  Duration: {result.TotalDuration.TotalSeconds:F1}s");
            Console.WriteLine($"  Tools   : {result.ToolUsage?.Count ?? 0} calls — " +
                              $"{string.Join(", ", result.ToolUsage?.UniqueToolNames ?? [])}");
        }

        Console.WriteLine();

        if (testResult.ExecutionResult is not { } execResult)
        {
            PrintFail("No execution result available.");
            return;
        }

        // ── Fluent assertions ─────────────────────────────────────────────────
        try
        {
            // Workflow structure
            execResult.Should()
                .HaveStepCount(4, because: "pipeline has exactly 4 agents")
                .HaveExecutedInOrder("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
                .HaveCompletedWithin(TimeSpan.FromMinutes(5))
                .HaveNoErrors()
                .HaveNonEmptyOutput()
                .Validate();

            PrintPass("Workflow structure assertions PASSED!");

            // Per-executor output
            execResult.Should()
                .ForExecutor("TripPlanner").HaveNonEmptyOutput().And()
                .ForExecutor("FlightReservation").HaveNonEmptyOutput().And()
                .ForExecutor("HotelReservation").HaveNonEmptyOutput().And()
                .ForExecutor("Presenter").HaveNonEmptyOutput().And()
                .Validate();

            PrintPass("Per-executor assertions PASSED!");

            // Tool-level assertions (if tool events were captured)
            if (execResult.ToolUsage != null)
            {
                execResult.Should()
                    .HaveCalledTool("GetInfoAbout",  because: "TripPlanner researches each city")
                        .WithoutError()
                    .And()
                    .HaveCalledTool("SearchFlights")
                        .BeforeTool("BookFlight", because: "must search before booking")
                        .WithoutError()
                    .And()
                    .HaveCalledTool("BookFlight").WithoutError()
                    .And()
                    .HaveCalledTool("BookHotel",     because: "HotelReservation must book hotels")
                        .WithoutError()
                    .And()
                    .HaveNoToolErrors()
                    .HaveAtLeastTotalToolCalls(4, because: "minimum one call per tool")
                    .Validate();

                PrintPass("Tool-level assertions PASSED!");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ⚠️  Tool-level assertions skipped (no tool events captured).");
                Console.ResetColor();
            }
        }
        catch (WorkflowAssertionException ex)
        {
            PrintFail($"Workflow assertion failed: {ex.Message}");
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 02 — TripPlanner Workflow Assertions                                  ║
║   4-agent pipeline · Tool order validated · Execution time verified         ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Eval 02 — Azure OpenAI credentials required.
");
        Console.ResetColor();
    }

    private static void PrintPass(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✅ {message}");
        Console.ResetColor();
    }

    private static void PrintFail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ❌ {message}");
        Console.ResetColor();
    }
}
