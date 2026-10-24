// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.TravelDemo.Agents;
using Microsoft.Extensions.AI;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Eval 01 — TravelAgent Full-Trip Assertions (Hypothesis Test)
///
/// Given the SAME request as Eval 02, we assert that the single all-in-one
/// TravelAgent calls every expected tool in the correct sequence:
///   GetInfoAbout → SearchFlights → BookFlight
///                → SearchHotel  → BookHotel → SendConfirmation
///
/// Hypothesis: a single agent is LESS deterministic than a structured workflow.
/// One run cannot show non-determinism (Eval 04 measures run-to-run variance and Eval 03 compares): a skipped,
/// re-ordered or forbidden step here is a SIGNAL — a step the workflow enforces structurally — and the run is a FAIL.
/// A run that called no tool at all failed for another reason and is not read as evidence either way.
///
/// The RESULT line weighs every dimension: a failed tool / order / policy / response check is FAIL, a failed
/// quality gate (the judge's score) on sound checks is WARN, and anything not measured is INCOMPLETE.
///
/// Agents and tools are imported from AgentEval.TravelDemo — no duplication.
/// </summary>
public static class Eval01_TravelAgentEvals
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        // Model banner: neutral by default; with --show-model it names the
        // provider and model this eval is about to charge (never the key), so
        // the operator can confirm the wiring before a 30-second LLM run.
        Config.PrintTarget();
        Console.WriteLine();

        Console.WriteLine("  Creating TravelAgent + AgentEval harness...\n");

        // ── Agent from AgentEval.TravelDemo (no duplication) ────────────────────────────
        var rawAgent = TravelAgentFactory.Create();
        var agent    = new MAFAgentAdapter(rawAgent);

        // ── Evaluator LLM client for LLM-as-a-judge (same provider + model as the agent) ──
        var evaluatorClient = Config.CreateChatClient();

        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        // ── Test case (same input as Eval 02 / Demo 02) ———————————————————
        var testCase = new TestCase
        {
            Name  = "TravelAgent — Full trip (Tokyo + Cologne)",
            Input = TravelDemoPrompt.Canonical,
            ExpectedTools          = ["GetInfoAbout", "SearchFlights", "SearchHotel",
                                      "BookFlight", "BookHotel", "SendConfirmation"],
            ExpectedOutputContains = "Tokyo",
            EvaluationCriteria = [..TravelEvalCriteria.Eval01],
            PassingScore = 70
        };

        Console.WriteLine($"  Running: \"{testCase.Name}\"\n");
        Console.WriteLine("  ⏳ This may take ~30 seconds (single agent, multiple tool calls)...\n");

        // Live trace — wire the harness's five streaming callbacks to a
        // colour-coded console printer so the operator can watch the agent
        // think + call tools in real time. Catches the same failure modes
        // the Demo01 tool-trace dump catches, but live instead of post-hoc.
        // The streamed text is kept too: when the judge call throws, the harness drops the answer it had.
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Live trace ──────────────────────────────────────────────────────");
        Console.ResetColor();

        var streamedText = new System.Text.StringBuilder();
        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            streamingOptions: StreamingTracePrinter.Create(onText: chunk => streamedText.Append(chunk)),
            options: new EvaluationOptions
            {
                TrackTools       = true,
                EvaluateResponse = true,
                ModelName        = Config.Model,
                Verbose          = true,
                TrackPerformance = true,
            });

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("\n  ─── End of live trace ───────────────────────────────────────────────");
        Console.ResetColor();

        // ── A failed run is NOT a result — but WHERE it failed decides what is left ──
        // The harness catches every exception (an HTTP 404/429 from the provider, a DNS failure) into Error, the
        // judge call's included. If the AGENT failed there is nothing to check: no verdict, no snapshot. If the agent
        // finished and the JUDGE failed, its tool calls and answer are still checked below; only the quality gate is
        // not measured. Saying "the agent run failed" for a judge failure blamed the wrong component.
        if (result.Error is { } runError)
        {
            var stage = RunClassifier.FailedStageOf(result);
            if (stage == FailedStage.AgentRun)
            {
                PrintFail($"The agent run failed — this run was NOT measured (no verdict, no snapshot): {Config.Redact(runError)}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n  ⚠️  RESULT: NOT MEASURED — the agent run failed before anything could be checked.");
                SampleOutcome.Skip("Eval 01: the agent run failed before anything could be checked.");
                Console.ResetColor();
                return;
            }
            var what = stage == FailedStage.JudgeCall ? "The judge call failed" : "The evaluation failed after the judge answered";
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  {what} — the quality gate was NOT measured; the agent finished, and its tools and " +
                              $"answer are still checked below: {Config.Redact(runError)}");
            Console.ResetColor();
        }

        // The judge can fail on its own (no verdict): then the score is a placeholder, not a grade.
        bool qualityMeasured = result.Error is null && !result.JudgeFailed;
        // The harness sets Passed from the judge's score alone — it does not enforce ExpectedTools — so it is the
        // QUALITY GATE, and only that.
        bool? qualityPassed = qualityMeasured ? result.Passed : null;

        // ── Print rich ASCII summary ─────────────────────────────────────────────
        EvalPrinter.PrintAgentResult(
            result,
            testCase.ExpectedTools ?? [],
            label: "Eval 01 — TravelAgent (single agent, hypothesis test)",
            scoreMeasured: qualityMeasured,
            passingScore: testCase.PassingScore);

        // ── LLM-as-judge criterion results ────────────────────────────────────
        if (qualityMeasured)
        {
            EvalPrinter.PrintLlmJudge(
                result.Score,
                result.CriteriaResults,
                result.Suggestions,
                label: "Eval 01 — LLM Quality Gate (9 criteria)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  LLM Quality Gate NOT MEASURED — the judge produced no verdict (the score 0 is a placeholder).\n");
            Console.ResetColor();
        }

        // ── The deterministic eval, through the library's floor-gated door ────
        //
        // What this replaces: two reads of `result.ToolUsage?.Calls.Count(…) ?? 0`. That `?? 0`
        // rendered "no recorder ran, so nobody knows" and "a recorder ran and saw nothing" as the
        // same number — and picked the one that reads as a clean negative.
        var evalInput = testCase.ToEvalInput(result);
        if (result.ToolUsage is not null)
        {
            var evalRunner = await new AgentEvalBuilder()
                .AddEval(new BookFlightWasCalledEval(), BookFlightWasCalledEval.DeclaredFloor)
                .BuildAsync(CancellationToken.None);

            var evalResults = await evalRunner.EvaluateEvalsAsync(evalInput, CancellationToken.None);
            var bookFlight = evalResults[0];

            Console.WriteLine();
            Console.WriteLine("  ── Deterministic eval (AgentEval door) ──────────────────────────────");
            Console.WriteLine($"  {bookFlight.Metric.Name}: {bookFlight.Score.Label}  "
                            + $"[census: {bookFlight.Score.CensusBucket()}]");
            foreach (var e in bookFlight.Details.Evidence ?? [])
            {
                Console.WriteLine($"    · {e.Source}/{e.Reference}: {Config.Redact(e.Message)}");
            }
            Console.WriteLine();
        }

        // ── Deterministic checks: tools, order, policy, response ───────────────
        // Each dimension is checked on its own so one failure does not hide another; the RESULT line weighs them all.
        var output = string.IsNullOrEmpty(result.ActualOutput) ? streamedText.ToString() : result.ActualOutput;
        var checks = RunChecks(result.ToolUsage, evalInput.ToolCalls, output);
        foreach (var check in checks)
        {
            switch (check.Passed)
            {
                case true:  PrintPass(check.Detail); break;
                case false: PrintFail($"{check.Name} FAILED: {(check.Failure is { } f ? Config.Redact(f) : check.Detail)}"); break;
                default:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ⚠️  {check.Name} UNDECIDABLE — {check.Detail}");
                    Console.ResetColor();
                    break;
            }
        }

        // ── What this one run says about the hypothesis ───────────────────────
        var hypothesis = HypothesisNote(result.ToolUsage, checks);
        if (hypothesis is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  {hypothesis}");
            Console.ResetColor();
        }

        // ── The run's verdict over every dimension ───────────────────────────
        bool? checksPassed = ChecksPassed(checks);
        var verdict = RunVerdicts.Combine(checksPassed, qualityPassed);
        PrintOverallVerdict(verdict, checks, qualityPassed);

        // ── Snapshot for Eval03 — only a run whose quality AND tools were measured ──
        if (!qualityMeasured || result.ToolUsage is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  ⚠️  Snapshot NOT saved — {(qualityMeasured ? "no tool-call data was recorded" : "the quality score was not measured")}; " +
                              "Eval 3 keeps the previous one.");
            Console.ResetColor();
            return;
        }

        // ── Deterministic criteria score + snapshot (for Eval03 comparison) ──────
        var snapshot = BuildSnapshot(result, checksPassed, verdict, EvalResultStore.CurrentProviderTag, EvalResultStore.CurrentModel);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  Criteria score (discrete): {snapshot.CriteriaScore}/100  " +
                          $"({snapshot.CriteriaMetCount}/{snapshot.CriteriaTotal} criteria met)");
        Console.ResetColor();

        EvalResultStore.Save("eval01_agent", snapshot);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  📁 Snapshot saved — run Eval 3 to compare with workflow.");
        if (checksPassed != true)
            Console.WriteLine("     It records the failed / unchecked tools: Eval 3 gives no hypothesis verdict on it.");
        Console.ResetColor();
    }

    // ── The checks (pure: the self-test drives them offline) ──────────────────

    /// <summary>One deterministic check of Eval 01.</summary>
    /// <param name="Name">The dimension, for the RESULT line.</param>
    /// <param name="Passed">True / false, or null when it could not look (no tool-call data).</param>
    /// <param name="Detail">The pass line, the failure text, or why it could not look.</param>
    /// <param name="IsToolOrPolicy">True for the tool, order and policy checks — the ones the workflow enforces structurally.</param>
    /// <param name="Failure">The assertion that failed, printed through <c>Config.Redact</c>.</param>
    internal sealed record CheckOutcome(string Name, bool? Passed, string Detail, bool IsToolOrPolicy, Exception? Failure = null);

    /// <summary>
    /// Every deterministic check of the run: tool order &amp; completeness, the no-cancellation policy, the response
    /// content, both flight legs, and a confirmation before each booking. The last two were printed as yellow
    /// "signals" next to a green PASSED; a missed leg or an unconfirmed booking is a failed tool / policy check.
    /// </summary>
    /// <param name="report">The harness's tool usage; null when no recorder saw the run.</param>
    /// <param name="projectedCalls">The same calls through the eval projection (null = nobody recorded).</param>
    /// <param name="output">The agent's answer.</param>
    internal static List<CheckOutcome> RunChecks(ToolUsageReport? report, IReadOnlyList<ToolCall>? projectedCalls, string? output)
    {
        const string NoData = "no tool-call data was recorded on this run";
        var checks = new List<CheckOutcome>();

        if (report is null)
        {
            // No recorder saw the run: the assertions could not look, which is not the agent skipping a step.
            checks.Add(new("Tool order & completeness", null, NoData, IsToolOrPolicy: true));
            checks.Add(new("Behavioural policy (no cancellations)", null, NoData, IsToolOrPolicy: true));
        }
        else
        {
            checks.Add(Assert("Tool order & completeness", true, "Tool order & completeness assertions PASSED!", () =>
                report.Should()
                    // Research must come first
                    .HaveCalledTool("GetInfoAbout",
                        because: "agent must research both cities before planning")
                        .BeforeTool("SearchFlights",
                            because: "destination research should precede flight search")
                    .And()
                    // Flights: search → (user confirm) → book
                    .HaveCalledTool("SearchFlights",
                        because: "user asked for flights between Tokyo and Cologne")
                        .BeforeTool("BookFlight",
                            because: "must search available options before booking")
                    .And()
                    .HaveCalledTool("BookFlight",
                        because: "user explicitly requested flight bookings")
                    .And()
                    // Hotels: search → (user confirm) → book
                    .HaveCalledTool("SearchHotel",
                        because: "user asked for hotel bookings in each city")
                        .BeforeTool("BookHotel",
                            because: "must search available options before booking")
                    .And()
                    .HaveCalledTool("BookHotel",
                        because: "user explicitly requested hotel bookings for each city")
                    .And()
                    // Email confirmation
                    .HaveCalledTool("SendConfirmation",
                        because: "user asked for trip summary sent to traveller@example.com")));

            // NeverCallTool throws BehavioralPolicyViolationException (not a ToolAssertionException): checked on its
            // own, so a skipped step above cannot hide a forbidden call here.
            checks.Add(Assert("Behavioural policy (no cancellations)", true, "Policy assertions PASSED — no cancellation tool was called.", () =>
                report.Should()
                    .NeverCallTool("CancelFlightReservation",
                        because: "user did not request any cancellation")
                    .NeverCallTool("CancelHotelBooking",
                        because: "user did not request any cancellation")));
        }

        checks.Add(Assert("Response content", false, "Response content assertions PASSED!", () =>
            (output ?? "").Should()
                .Contain("Tokyo",   because: "response must cover the first destination")
                .Contain("Cologne", because: "response must cover the second destination")
                .HaveLengthBetween(500, 50_000,
                    because: "a full 7-day trip plan with bookings should be substantial")));

        // The tool counts read off the PROJECTION, which distinguishes "nobody recorded" from "recorded nothing".
        if (projectedCalls is null)
        {
            checks.Add(new("Flight legs (BookFlight ≥ 2)", null, NoData, IsToolOrPolicy: true));
            checks.Add(new("Confirmation before booking (GetUserConfirmation ≥ 4)", null, NoData, IsToolOrPolicy: true));
        }
        else
        {
            int bookFlights = projectedCalls.Count(c => c.Name == "BookFlight");
            int confirms    = projectedCalls.Count(c => c.Name == "GetUserConfirmation");
            checks.Add(new("Flight legs (BookFlight ≥ 2)", bookFlights >= 2,
                bookFlights >= 2
                    ? $"BookFlight called {bookFlights} time(s) — at least 2 legs booked ✓"
                    : $"BookFlight called {bookFlights} time(s); a 2-city trip needs ≥ 2 legs booked",
                IsToolOrPolicy: true));
            checks.Add(new("Confirmation before booking (GetUserConfirmation ≥ 4)", confirms >= 4,
                confirms >= 4
                    ? $"GetUserConfirmation called {confirms} time(s) — all bookings confirmed ✓"
                    : $"GetUserConfirmation called {confirms} time(s); the agent's policy is to confirm every booking " +
                      "(≥ 4: 2 flights + 2 hotels)",
                IsToolOrPolicy: true));
        }
        return checks;
    }

    /// <summary>The checks together: false if any failed, null if none failed but one could not look, else true.</summary>
    internal static bool? ChecksPassed(IReadOnlyList<CheckOutcome> checks) =>
        checks.Any(c => c.Passed == false) ? false
        : checks.Any(c => c.Passed is null) ? null
        : true;

    private static CheckOutcome Assert(string name, bool isToolOrPolicy, string passDetail, Action assert)
    {
        try
        {
            assert();
            return new CheckOutcome(name, true, passDetail, isToolOrPolicy);
        }
        catch (AgentEvalAssertionException ex)
        {
            // Every assertion type (tool, policy, response) is a failed check, reported — never an escape from the eval.
            return new CheckOutcome(name, false, ex.Message, isToolOrPolicy, ex);
        }
    }

    /// <summary>
    /// What this one run says about the hypothesis, or null when it says nothing. Never a verdict that contradicts the
    /// RESULT: a run that called NO tool at all failed because the agent did not use its tools — that is not evidence
    /// of non-determinism and is reported as what it is. A skipped, re-ordered or forbidden step is a one-run SIGNAL.
    /// </summary>
    internal static string? HypothesisNote(ToolUsageReport? report, IReadOnlyList<CheckOutcome> checks)
    {
        if (report is { Count: 0 })
        {
            return "❌ No tool was called at all — a failed run, not evidence about determinism: the agent answered " +
                   "without using its tools (check that the model supports tool calling). No hypothesis signal.";
        }
        var toolFailures = checks.Where(c => c.IsToolOrPolicy && c.Passed == false).Select(c => c.Name).ToList();
        if (toolFailures.Count == 0) return null;
        return $"⚠️  HYPOTHESIS SIGNAL (one run) — the single agent missed what the workflow enforces structurally: " +
               $"{string.Join("; ", toolFailures)}.\n     One run is not a variance measurement: Eval 4 measures it, Eval 3 compares.";
    }

    /// <summary>The RESULT line: every failed dimension named; never a clean PASS hiding one.</summary>
    internal static void PrintOverallVerdict(RunVerdict verdict, IReadOnlyList<CheckOutcome> checks, bool? qualityPassed)
    {
        EvalProcessOutcome.Record(verdict, $"Eval 01 RESULT: {verdict}");
        var quality   = qualityPassed switch { true => "passed", false => "failed", null => "not measured" };
        var failed     = checks.Where(c => c.Passed == false).Select(c => c.Name).ToList();
        var notChecked = checks.Where(c => c.Passed is null).Select(c => c.Name).ToList();
        var (text, color) = verdict switch
        {
            RunVerdict.Fail => ($"❌ RESULT: FAIL — {string.Join("; ", failed)} failed (quality gate {quality}).", ConsoleColor.Red),
            RunVerdict.Pass => ("✅ RESULT: PASS — tools, order, policy, response and the quality gate all passed.", ConsoleColor.Green),
            RunVerdict.Warn => ("⚠️  RESULT: WARN — tools, order, policy and response passed; the quality gate failed.", ConsoleColor.Yellow),
            _ when notChecked.Count > 0
                            => ($"⚠️  RESULT: INCOMPLETE — not checked: {string.Join("; ", notChecked)} (quality gate {quality}).", ConsoleColor.Yellow),
            _               => ("⚠️  RESULT: INCOMPLETE — tools, order, policy and response passed; the quality gate was not measured.", ConsoleColor.Yellow),
        };
        Console.ForegroundColor = color;
        Console.WriteLine($"\n  {text}");
        Console.ResetColor();
    }

    /// <summary>
    /// The Eval 3 snapshot: the quality gate (<see cref="EvalSnapshot.Passed"/>) AND the checks and the RESULT, so a
    /// tool-failed run can never reach Eval 3 as a clean pass. Pure — provenance is passed in.
    /// </summary>
    internal static EvalSnapshot BuildSnapshot(TestResult result, bool? checksPassed, RunVerdict verdict, string? providerTag, string? model)
    {
        int criteriaTotal    = result.CriteriaResults?.Count ?? 0;
        int criteriaMetCount = result.CriteriaResults?.Count(c => c.Met) ?? 0;
        return new EvalSnapshot
        {
            Architecture     = "Single Agent",
            Label            = "Eval 01 — TravelAgent (Alex)",
            ProviderTag      = providerTag,   // provenance: recorded even when the console hides it
            Model            = model,
            LlmScore         = result.Score,
            CriteriaScore    = criteriaTotal > 0 ? criteriaMetCount * 100 / criteriaTotal : 0,
            CriteriaMetCount = criteriaMetCount,
            CriteriaTotal    = criteriaTotal,
            ToolCallCount    = result.ToolCallCount,
            BookFlightCount  = result.ToolUsage?.Calls.Count(c => c.Name == "BookFlight") ?? 0,
            Passed           = result.Passed,
            StructurePassed  = checksPassed,
            Result           = verdict,
            DurationMs       = (long)(result.Performance?.TotalDuration.TotalMilliseconds ?? 0),
            CriteriaDetails  = result.CriteriaResults?
                .Select(c => new CriterionSnapshot(c.Criterion[..Math.Min(c.Criterion.Length, 80)], c.Met, c.Explanation))
                .ToList() ?? []
        };
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 01 — TravelAgent (single agent, hypothesis test)                       ║
║   Same task as Eval 02 · All tools asserted · Order matters                  ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        SampleOutcome.Skip("Eval 01: no model configured.");
        Config.PrintMissingProvider("Eval 01");
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
