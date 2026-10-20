// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.TravelDemo.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Eval 02 — TripPlanner Workflow Assertions
///
/// Uses AgentEval to verify that the TripPlanner Workflow:
/// - Executes all 4 agents in the correct order
/// - Calls the expected tools across the pipeline
/// - Completes within a reasonable time budget
/// - Produces non-empty output at every stage
///
/// The workflow factory is imported from AgentEval.TravelDemo — no duplication.
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

        // Model banner (provider + model only with --show-model; never the
        // key). Identical block to Eval01 so the operator can compare runs
        // side-by-side.
        Config.PrintTarget();
        Console.WriteLine();

        Console.WriteLine("  Building TripPlanner workflow + AgentEval harness...\n");

        // ── Evaluator client (LLM-as-judge after the workflow run; same provider + model) ──
        var evaluatorClient = Config.CreateChatClient();
        var evaluator       = new ChatClientEvaluator(evaluatorClient);

        // ── Workflow from AgentEval.TravelDemo (no duplication) ─────────────────────────
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
            Input             = TravelDemoPrompt.Canonical,
            Description       = "End-to-end workflow validation with tool-calling agents",
            ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
            StrictExecutorOrder = true,
            MaxDuration       = TimeSpan.FromMinutes(5),
            ExpectedTools     = ["GetInfoAbout", "SearchFlights", "SearchHotel", "BookFlight", "BookHotel"],
            Tags              = ["trip-planner", "ecs2026", "workflow"]
        };

        Console.WriteLine($"  Running: \"{testCase.Name}\"\n");
        Console.WriteLine("  ⏳ This may take up to 2 minutes (4 LLM calls with tools)...\n");

        // Workflow path — WorkflowEvaluationHarness has no StreamingOptions
        // surface today (unlike MAFEvaluationHarness used in Eval01), so the
        // closest equivalent is verbose mode on both the ctor and the test
        // options, plus CaptureTelemetry so per-executor timing + tool calls
        // show up in the result. Per-stage progress will print to the
        // console as the workflow advances.
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Live trace (verbose workflow harness) ──────────────────────────");
        Console.ResetColor();

        // The verbose console logger, but every line through Config.Redact: the harness logs a failed run's raw
        // exception text ("❌ Workflow execution failed: {message}"), which can name the host or the model.
        var harness     = new WorkflowEvaluationHarness(evaluator: null, new RedactingAgentEvalLogger(new ConsoleAgentEvalLogger()));
        var testOptions = new WorkflowTestOptions
        {
            Timeout          = TimeSpan.FromMinutes(5),
            Verbose          = true,
            CaptureTelemetry = true
        };

        WorkflowTestResult testResult;
        try
        {
            testResult = await harness.RunWorkflowTestAsync(workflowAdapter, testCase, testOptions);
        }
        catch (Exception ex)
        {
            PrintFail($"Workflow execution failed — this run was NOT measured (no verdict, no snapshot): {Config.Redact(ex)}");
            PrintNotMeasured("the workflow run failed");
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── End of live trace ───────────────────────────────────────────────");
        Console.ResetColor();

        // ── A failed run is NOT a result ──────────────────────────────────────
        // The harness catches a thrown failure (timeout, workflow exception) into Error, and the workflow adapter does
        // not throw on a failed agent call at all — it records it in Errors and the run "completes". Either way the
        // agents did not get to do their job: no structure verdict, no judge, no snapshot.
        if (testResult.Error is { } runError)
        {
            PrintFail($"The workflow run failed — this run was NOT measured (no verdict, no snapshot): {Config.Redact(runError)}");
            PrintNotMeasured("the workflow run failed");
            return;
        }
        if (testResult.ExecutionResult is not { } execResult)
        {
            PrintFail("No execution result available — this run was NOT measured.");
            PrintNotMeasured("the harness returned no execution result");
            return;
        }
        if (execResult.Errors is { Count: > 0 } executorErrors)
        {
            PrintFail($"{executorErrors.Count} agent call(s) failed — this run was NOT measured (no verdict, no snapshot):");
            foreach (var e in executorErrors)
                PrintFail($"   [{e.ExecutorId}] {EvalPrinter.OneLine(Config.Redact(e.Message))}");
            PrintNotMeasured("an agent call failed");
            return;
        }

        // ── Print rich ASCII summary ───────────────────────────────────────────
        EvalPrinter.PrintWorkflowResult(
            testResult,
            testCase.ExpectedTools ?? [],
            label: "Eval 02 — TripPlanner Workflow");

        // ── Structure: the harness's checks, then the fluent assertions ──────────
        // Every part is checked on its own so one failure does not hide another, and no line says "structure PASSED"
        // while a part of it failed: the harness's checks (executor order, expected tools, duration) ARE structure.
        var structureFailures = new List<string>();
        foreach (var failed in testResult.AssertionResults?.Where(a => !a.Passed) ?? [])
        {
            structureFailures.Add($"harness check '{failed.AssertionName}'");
            PrintFail($"Harness check '{failed.AssertionName}' failed: {EvalPrinter.OneLine(Config.Redact(failed.FailureMessage))}");
        }
        if (!testResult.Passed && structureFailures.Count == 0)
        {
            structureFailures.Add("the harness's checks");
            PrintFail("The harness failed the run without naming a check.");
        }

        try
        {
            // Pipeline shape — part of the structure, not all of it.
            execResult.Should()
                .HaveStepCount(4, because: "pipeline has exactly 4 agents")
                .HaveExecutedInOrder("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
                .HaveCompletedWithin(TimeSpan.FromMinutes(5))
                .HaveNoErrors()
                .HaveNonEmptyOutput()
                .Validate();

            PrintPass("Pipeline shape assertions passed (4 steps in order, within 5 min, no errors, non-empty output).");
        }
        catch (AgentEvalAssertionException ex)
        {
            // Redacted like every printed message: HaveNoErrors() copies an executor's raw error text into its message.
            structureFailures.Add("pipeline shape");
            PrintFail($"Pipeline shape assertion failed: {Config.Redact(ex)}");
        }

        try
        {
            // Per-executor output
            execResult.Should()
                .ForExecutor("TripPlanner").HaveNonEmptyOutput().And()
                .ForExecutor("FlightReservation").HaveNonEmptyOutput().And()
                .ForExecutor("HotelReservation").HaveNonEmptyOutput().And()
                .ForExecutor("Presenter").HaveNonEmptyOutput().And()
                .Validate();

            PrintPass("Per-executor output assertions passed.");
        }
        catch (AgentEvalAssertionException ex)
        {
            structureFailures.Add("per-executor output");
            PrintFail($"Per-executor assertion failed: {Config.Redact(ex)}");
        }

        try
        {
            // Tool-level assertions. WorkflowExecutionResult.ToolUsage is null when NO step recorded a tool call — with
            // five tools expected that is a failure, not a skipped check (the harness's Expected Tools check agrees).
            if (execResult.ToolUsage == null)
            {
                structureFailures.Add("tool-level assertions");
                PrintFail($"Tool-level assertions FAILED — no step recorded a tool call ({testCase.ExpectedTools?.Count ?? 0} tools expected).");
            }
            else
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

                PrintPass("Tool-level assertions passed.");
            }
        }
        catch (AgentEvalAssertionException ex)
        {
            structureFailures.Add("tool-level assertions");
            PrintFail($"Tool-level assertion failed: {Config.Redact(ex)}");
        }

        // One line for the whole structure, consistent with every line above and with the RESULT below.
        bool structurePassed = structureFailures.Count == 0;
        if (structurePassed)
            PrintPass("Workflow structure PASSED — harness checks, pipeline shape, per-executor output and tools.");
        else
            PrintFail($"Workflow structure FAILED — {string.Join("; ", structureFailures)}.");

        // ── LLM-as-judge quality evaluation ───────────────────────────────────
        Console.WriteLine("\n  Evaluating final output quality with LLM-as-judge...\n");

        bool? qualityGatePassed = null;   // null = not measured
        try
        {
            var evalResult = await evaluator.EvaluateAsync(
                input:    testCase.Input,
                output:   execResult.FinalOutput,
                criteria: TravelEvalCriteria.Eval02);

            if (evalResult.EvaluationFailed)
            {
                // The judge answered nothing usable: its score is a sentinel, not a grade.
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ⚠️  LLM Quality Gate NOT MEASURED — the judge produced no verdict (no snapshot).");
                Console.ResetColor();
                PrintOverallVerdict(structurePassed, qualityGatePassed);
                return;
            }

            EvalPrinter.PrintLlmJudge(
                evalResult.OverallScore,
                evalResult.CriteriaResults,
                evalResult.Improvements,
                label: "Eval 02 — LLM Quality Gate (10 criteria)");

            // ── Quality gate verdict ─────────────────────────────────────────────
            const int passingScore = 75;
            bool qualityPassed = evalResult.OverallScore >= passingScore;
            qualityGatePassed = qualityPassed;
            Console.ForegroundColor = qualityPassed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(qualityPassed
                ? $"\n  ✅ LLM Quality Gate PASSED  ({evalResult.OverallScore}/100 ≥ {passingScore})"
                : $"\n  ❌ LLM Quality Gate FAILED  ({evalResult.OverallScore}/100 < {passingScore})");
            Console.ResetColor();

            // ── Deterministic criteria score + snapshot (for Eval03 comparison) ──
            var snapshot = BuildSnapshot(execResult, evalResult, structurePassed, qualityPassed,
                                         EvalResultStore.CurrentProviderTag, EvalResultStore.CurrentModel);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  Criteria score (discrete): {snapshot.CriteriaScore}/100  " +
                              $"({snapshot.CriteriaMetCount}/{snapshot.CriteriaTotal} criteria met)");
            Console.ResetColor();

            EvalResultStore.Save("eval02_workflow", snapshot);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot saved — run Eval 3 to compare with single agent.");
            if (!structurePassed)
                Console.WriteLine("     It records the failed structure: Eval 3 gives no hypothesis verdict on it.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  LLM evaluation failed — quality NOT MEASURED (no snapshot): {Config.Redact(ex)}");
            Console.ResetColor();
        }

        PrintOverallVerdict(structurePassed, qualityGatePassed);
    }

    /// <summary>
    /// The Eval 3 snapshot of a judged run. It records the structure verdict and the run's RESULT next to the quality
    /// gate: <see cref="EvalSnapshot.Passed"/> alone (the judge's score) let a structure-failed run reach Eval 3 as a
    /// clean pass and win the comparison. Pure — provenance is passed in — so the self-test can build one offline.
    /// </summary>
    internal static EvalSnapshot BuildSnapshot(WorkflowExecutionResult execResult, EvaluationResult evalResult, bool structurePassed,
                                               bool qualityPassed, string? providerTag, string? model)
    {
        int criteriaTotal    = evalResult.CriteriaResults?.Count ?? 0;
        int criteriaMetCount = evalResult.CriteriaResults?.Count(c => c.Met) ?? 0;
        return new EvalSnapshot
        {
            Architecture     = "Workflow (4 agents)",
            Label            = "Eval 02 — TripPlanner Workflow",
            ProviderTag      = providerTag,   // provenance: recorded even when the console hides it
            Model            = model,
            LlmScore         = evalResult.OverallScore,
            CriteriaScore    = criteriaTotal > 0 ? criteriaMetCount * 100 / criteriaTotal : 0,
            CriteriaMetCount = criteriaMetCount,
            CriteriaTotal    = criteriaTotal,
            ToolCallCount    = execResult.ToolUsage?.Count ?? 0,
            BookFlightCount  = execResult.ToolUsage?.Calls.Count(c => c.Name == "BookFlight") ?? 0,
            Passed           = qualityPassed,
            StructurePassed  = structurePassed,
            Result           = RunVerdicts.Combine(structurePassed, qualityPassed),
            DurationMs       = (long)execResult.TotalDuration.TotalMilliseconds,
            CriteriaDetails  = evalResult.CriteriaResults?
                .Select(c => new CriterionSnapshot(c.Criterion[..Math.Min(c.Criterion.Length, 80)], c.Met, c.Explanation))
                .ToList() ?? []
        };
    }

    /// <summary>
    /// The run's verdict over BOTH dimensions (<see cref="RunVerdicts.Combine"/>): a failed structure is a FAIL whatever
    /// the judge said, a failed quality gate on a sound structure is a WARN, and an unmeasured quality gate is never a PASS.
    /// </summary>
    private static void PrintOverallVerdict(bool structurePassed, bool? qualityPassed)
    {
        var quality = qualityPassed switch { true => "passed", false => "failed", null => "not measured" };
        var (text, color) = RunVerdicts.Combine(structurePassed, qualityPassed) switch
        {
            RunVerdict.Fail => ($"❌ RESULT: FAIL — the workflow's structure / tool checks failed (quality gate {quality}).", ConsoleColor.Red),
            RunVerdict.Pass => ("✅ RESULT: PASS — structure, tools and the quality gate all passed.", ConsoleColor.Green),
            RunVerdict.Warn => ("⚠️  RESULT: WARN — structure and tools passed; the quality gate failed.", ConsoleColor.Yellow),
            _               => ("⚠️  RESULT: INCOMPLETE — structure and tools passed; the quality gate was not measured.", ConsoleColor.Yellow),
        };
        Console.ForegroundColor = color;
        Console.WriteLine($"\n  {text}");
        Console.ResetColor();
    }

    private static void PrintNotMeasured(string why)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  ⚠️  RESULT: NOT MEASURED — {why} before anything could be checked.");
        Console.ResetColor();
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

    private static void PrintMissingCredentials() => Config.PrintMissingProvider("Eval 02");

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
