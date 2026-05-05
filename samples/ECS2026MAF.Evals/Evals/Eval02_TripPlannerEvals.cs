// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Azure.AI.OpenAI;
using ECS2026MAF.Workflows;
using Microsoft.Extensions.AI;

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

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Building TripPlanner workflow + AgentEval harness...\n");

        // ── Evaluator client (for LLM-as-judge after the workflow run) ────────
        var azureClient     = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var evaluatorClient = azureClient.GetChatClient(Config.Model).AsIChatClient();
        var evaluator       = new ChatClientEvaluator(evaluatorClient);

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
            Name              = "TripPlanner — Tokyo & Cologne",
            Input             = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                                "I need city information, flights between them, " +
                                "and hotel bookings for each city.",
            Description       = "End-to-end workflow validation with tool-calling agents",
            ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
            StrictExecutorOrder = true,
            MaxDuration       = TimeSpan.FromMinutes(5),
            ExpectedTools     = ["GetInfoAbout", "SearchFlights", "SearchHotel", "BookFlight", "BookHotel"],
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

        // ── Print rich ASCII summary ───────────────────────────────────────────
        EvalPrinter.PrintWorkflowResult(
            testResult,
            testCase.ExpectedTools ?? [],
            label: "Eval 02 — TripPlanner Workflow");

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
                    .HaveCalledTool("SearchHotel", because: "HotelReservation must search before booking")
                        .BeforeTool("BookHotel", because: "must search before booking")
                        .WithoutError()
                    .And()
                    .HaveCalledTool("BookHotel",     because: "HotelReservation must book hotels")
                        .WithoutError()
                    .And()
                    .HaveNoToolErrors()
                    .HaveAtLeastTotalToolCalls(5, because: "minimum one call per tool")
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

        // ── LLM-as-judge quality evaluation ───────────────────────────────────
        Console.WriteLine("\n  Evaluating final output quality with LLM-as-judge...\n");

        try
        {
            var evalResult = await evaluator.EvaluateAsync(
                input:    testCase.Input,
                output:   execResult.FinalOutput,
                criteria: TravelEvalCriteria.Eval02);

            EvalPrinter.PrintLlmJudge(
                evalResult.OverallScore,
                evalResult.CriteriaResults,
                evalResult.Improvements,
                label: "Eval 02 — LLM Quality Gate (10 criteria)");

            // ── Quality gate verdict ─────────────────────────────────────────────
            const int passingScore = 75;
            bool qualityPassed = evalResult.OverallScore >= passingScore;
            Console.ForegroundColor = qualityPassed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(qualityPassed
                ? $"\n  ✅ LLM Quality Gate PASSED  ({evalResult.OverallScore}/100 ≥ {passingScore})"
                : $"\n  ❌ LLM Quality Gate FAILED  ({evalResult.OverallScore}/100 < {passingScore})");
            Console.ResetColor();

            // ── Deterministic criteria score + snapshot (for Eval03 comparison) ──
            int criteriaTotal    = evalResult.CriteriaResults?.Count ?? 0;
            int criteriaMetCount = evalResult.CriteriaResults?.Count(c => c.Met) ?? 0;
            int criteriaScore    = criteriaTotal > 0 ? criteriaMetCount * 100 / criteriaTotal : 0;
            int bookFlightCount  = execResult.ToolUsage?.Calls.Count(c => c.Name == "BookFlight") ?? 0;

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  Criteria score (discrete): {criteriaScore}/100  " +
                              $"({criteriaMetCount}/{criteriaTotal} criteria met)");
            Console.ResetColor();

            EvalResultStore.Save("eval02_workflow", new EvalSnapshot
            {
                Architecture     = "Workflow (4 agents)",
                Label            = "Eval 02 — TripPlanner Workflow",
                LlmScore         = evalResult.OverallScore,
                CriteriaScore    = criteriaScore,
                CriteriaMetCount = criteriaMetCount,
                CriteriaTotal    = criteriaTotal,
                ToolCallCount    = execResult.ToolUsage?.Count ?? 0,
                BookFlightCount  = bookFlightCount,
                Passed           = qualityPassed,
                DurationMs       = (long)execResult.TotalDuration.TotalMilliseconds,
                CriteriaDetails  = evalResult.CriteriaResults?
                    .Select(c => new CriterionSnapshot(c.Criterion[..Math.Min(c.Criterion.Length, 80)], c.Met, c.Explanation))
                    .ToList() ?? []
            });

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot saved — run Eval 3 to compare with single agent.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  LLM evaluation failed: {ex.Message}");
            Console.ResetColor();
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
