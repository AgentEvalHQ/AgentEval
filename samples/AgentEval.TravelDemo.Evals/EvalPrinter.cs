// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// ASCII-art console output for evaluation results.
/// Renders tool call timelines, per-executor breakdowns, and policy checks
/// to make it easy to compare single-agent vs workflow determinism at a glance.
/// </summary>
public static class EvalPrinter
{
    private const int BoxWidth = 82;   // total chars including ║ borders
    private const int InnerWidth = 78; // BoxWidth - 2 (for the ║ chars) - 2 (padding)

    // ── Public entry points ──────────────────────────────────────────────────

    /// <summary>
    /// Prints a rich summary for a single-agent evaluation (Eval01).
    /// </summary>
    /// <param name="result">The harness result.</param>
    /// <param name="expectedTools">The tools the policy check expects.</param>
    /// <param name="label">Panel title.</param>
    /// <param name="scoreMeasured">False when the judge produced no verdict: the score is then a placeholder and is not shown.</param>
    /// <param name="passingScore">The test case's pass mark, shown next to the quality gate.</param>
    /// <remarks>
    /// The header row is the QUALITY GATE — <see cref="TestResult.Passed"/>, which <c>MAFEvaluationHarness</c> sets
    /// from the judge's score alone (it does not enforce <c>ExpectedTools</c>). It is labelled as such: an unlabelled
    /// "✅ PASSED" over a run that called no tool read as the run's verdict. The tools, their order and the policy are
    /// checked after this panel, and the eval's RESULT line weighs every dimension.
    /// </remarks>
    public static void PrintAgentResult(
        TestResult result,
        IReadOnlyList<string> expectedTools,
        string label = "Agent Evaluation",
        bool scoreMeasured = true,
        int? passingScore = null)
    {
        var report = result.ToolUsage;
        var dur    = result.Performance?.TotalDuration;
        var calls  = report?.Calls ?? [];

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        MetaRowAgent(scoreMeasured ? result.Score : null, result.Passed, passingScore, dur, result.ToolCallCount);
        Divider();
        SectionRow("TOOL CALL TIMELINE  (ordered by first invocation)");
        Divider();
        PrintToolTimeline(calls, grouped: false);
        Divider();
        SectionRow("POLICY CHECK  (expected vs actual tool calls)");
        Divider();
        PrintPolicyCheck(calls, expectedTools);
        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints a rich summary for a workflow evaluation (Eval02).
    /// Includes per-executor grouping in the timeline.
    /// </summary>
    public static void PrintWorkflowResult(
        WorkflowTestResult result,
        IReadOnlyList<string> expectedTools,
        string label = "Workflow Evaluation")
    {
        var execResult = result.ExecutionResult;
        var report     = execResult?.ToolUsage;
        var calls      = report?.Calls ?? [];

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        MetaRowWorkflow(result.Passed, execResult?.Steps.Count ?? 0,
                        execResult?.TotalDuration, report?.Count ?? 0);

        if (execResult?.Steps is { Count: > 0 } steps)
        {
            Divider();
            SectionRow("WORKFLOW PIPELINE");
            var pipeline = string.Join(" → ", steps.Select(s => s.ExecutorId));
            ContentRow(pipeline);
        }

        Divider();
        SectionRow("TOOL CALL TIMELINE  (grouped by executor)");
        Divider();
        PrintToolTimeline(calls, grouped: true, execResult?.Steps);
        Divider();
        SectionRow("POLICY CHECK  (expected vs actual tool calls)");
        Divider();
        PrintPolicyCheck(calls, expectedTools);
        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints a per-criterion LLM-as-judge evaluation panel — usable after both
    /// <see cref="PrintAgentResult"/> and <see cref="PrintWorkflowResult"/>.
    /// </summary>
    public static void PrintLlmJudge(
        int overallScore,
        IReadOnlyList<CriterionResult>? criteria,
        IReadOnlyList<string>? improvements = null,
        string label = "LLM-as-Judge Quality Evaluation")
    {
        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();

        // Score row
        var scoreBar   = BuildScoreBar(overallScore);
        var scoreLabel = overallScore >= 80 ? "EXCELLENT"
                       : overallScore >= 65 ? "ACCEPTABLE"
                       : overallScore >= 50 ? "MARGINAL"
                       : "POOR";
        if (overallScore >= 65) Console.ForegroundColor = ConsoleColor.Green;
        else if (overallScore >= 50) Console.ForegroundColor = ConsoleColor.Yellow;
        else Console.ForegroundColor = ConsoleColor.Red;
        ContentRow($"  Score: {overallScore,3}/100  {scoreBar}  {scoreLabel}");
        Console.ResetColor();

        if (criteria is { Count: > 0 })
        {
            Divider();
            SectionRow("CRITERIA  (✅ met · ❌ not met · explanation below each failure)");
            Divider();

            int metCount = criteria.Count(c => c.Met);
            int i = 0;
            foreach (var cr in criteria)
            {
                i++;
                var icon  = cr.Met ? "✅" : "❌";
                // Truncate criterion text to first 68 chars for the summary line
                var text  = cr.Criterion.Length > 68
                    ? cr.Criterion[..65] + "…"
                    : cr.Criterion;
                Console.ForegroundColor = cr.Met ? ConsoleColor.Green : ConsoleColor.Red;
                ContentRow($"  {icon}  {i,2}. {text}");

                // Show the LLM's explanation on the next line(s) for failing criteria
                if (!cr.Met && !string.IsNullOrWhiteSpace(cr.Explanation))
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    // Wrap explanation into 72-char chunks
                    foreach (var chunk in WrapText($"       → {cr.Explanation}", InnerWidth))
                        ContentRow(chunk);
                }
                else if (cr.Met && !string.IsNullOrWhiteSpace(cr.Explanation))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    foreach (var chunk in WrapText($"       ✓ {cr.Explanation}", InnerWidth))
                        ContentRow(chunk);
                }
                Console.ResetColor();
            }

            Divider();
            Console.ForegroundColor = metCount == criteria.Count ? ConsoleColor.Green
                                    : metCount >= criteria.Count / 2 ? ConsoleColor.Yellow
                                    : ConsoleColor.Red;
            ContentRow($"  {metCount}/{criteria.Count} criteria met");
            Console.ResetColor();
        }

        if (improvements is { Count: > 0 })
        {
            Divider();
            SectionRow("AGENT IMPROVEMENT FEEDBACK  (act on these to raise the score)");
            Divider();
            int impIdx = 0;
            foreach (var imp in improvements)
            {
                impIdx++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                // Wrap each suggestion at InnerWidth
                bool first = true;
                foreach (var chunk in WrapText($"  {impIdx}. {imp}", InnerWidth))
                {
                    ContentRow(first ? chunk : "     " + chunk.TrimStart());
                    first = false;
                }
                Console.ResetColor();
            }
        }

        BottomBorder();
        Console.WriteLine();
    }

    private static IEnumerable<string> WrapText(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        // Simple greedy word-wrap
        var words  = text.Split(' ');
        var line   = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (line.Length + word.Length + 1 > maxWidth && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static string BuildScoreBar(int score)
    {
        const int barWidth = 20;
        int filled = (int)Math.Round(score / 100.0 * barWidth);
        filled = Math.Clamp(filled, 0, barWidth);
        return "[" + new string('█', filled) + new string('░', barWidth - filled) + "]";
    }

    // ── Timeline renderer ────────────────────────────────────────────────────

    private static void PrintToolTimeline(
        IReadOnlyList<ToolCallRecord> calls,
        bool grouped,
        IReadOnlyList<ExecutorStep>? steps = null)
    {
        if (calls.Count == 0)
        {
            ContentRow("  (no tool calls recorded)");
            return;
        }

        if (!grouped || steps == null)
        {
            // Flat timeline: group by tool name, ordered by first call order
            var byName = calls
                .GroupBy(c => c.Name)
                .OrderBy(g => g.Min(c => c.Order))
                .ToList();

            int labelWidth = byName.Max(g => g.Key.Length) + 2;
            foreach (var group in byName)
            {
                var firstOrder = group.Min(c => c.Order);
                PrintTimelineRow($"#{firstOrder}", group.Key, group.ToList(), labelWidth);
            }
        }
        else
        {
            // Grouped by step: each step lists ITS OWN calls (ExecutorStep.ToolCalls). Filtering the run's calls by
            // executor id instead repeated all of an executor's calls under each of its steps. The '#n' tags keep the
            // run-wide order, which is the order WorkflowExecutionResult.ToolUsage numbers the same calls in.
            int globalOrder = 0;
            foreach (var step in steps)
            {
                var stepCalls = (step.ToolCalls ?? [])
                    .Select(c => (Call: c, Order: ++globalOrder))
                    .ToList();

                var executorLabel = $"▸ {step.ExecutorId}  ({stepCalls.Count} call{(stepCalls.Count == 1 ? "" : "s")})";
                Console.ForegroundColor = ConsoleColor.Cyan;
                ContentRow(executorLabel);
                Console.ResetColor();

                if (stepCalls.Count == 0)
                {
                    ContentRow("     (no tools called — generates output from context)");
                }
                else
                {
                    var grouped2 = stepCalls
                        .GroupBy(c => c.Call.Name)
                        .OrderBy(g => g.Min(c => c.Order))
                        .ToList();
                    int labelW = grouped2.Max(g => g.Key.Length) + 4;
                    foreach (var nameGroup in grouped2)
                    {
                        var firstOrder = nameGroup.Min(c => c.Order);
                        PrintTimelineRow($"   #{firstOrder}", nameGroup.Key, [.. nameGroup.Select(c => c.Call)], labelW);
                    }
                }
            }
        }
    }

    private static void PrintTimelineRow(
        string orderTag,
        string toolName,
        List<ToolCallRecord> toolCalls,
        int labelWidth)
    {
        // Build the bar: each call becomes  ██ ✓  or  ██ ✗
        var bar = string.Join("  ", toolCalls.Select(c => c.HasError ? "██ ✗" : "██ ✓"));
        var callLabel = toolCalls.Count == 1 ? "1 call" : $"{toolCalls.Count} calls";

        // Fixed-width layout
        var tag       = orderTag.PadLeft(4);
        var name      = toolName.PadRight(labelWidth);
        var barField  = bar.PadRight(30);
        var line      = $"{tag}  {name}{barField} {callLabel}";

        // Colour-code by error state
        if (toolCalls.Any(c => c.HasError))
            Console.ForegroundColor = ConsoleColor.Red;
        else
            Console.ForegroundColor = ConsoleColor.White;

        ContentRow(line);
        Console.ResetColor();
    }

    // ── Policy check renderer ────────────────────────────────────────────────

    private static void PrintPolicyCheck(
        IReadOnlyList<ToolCallRecord> calls,
        IReadOnlyList<string> expectedTools)
    {
        var calledNames = calls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int nameWidth   = expectedTools.Count > 0
            ? expectedTools.Max(t => t.Length) + 2
            : 20;
        nameWidth = Math.Max(nameWidth,
            calledNames.Count > 0 ? calledNames.Max(n => n.Length) + 2 : nameWidth);

        // 1. Expected tools — check whether they were called
        foreach (var expected in expectedTools)
        {
            var count = calls.Count(c => string.Equals(c.Name, expected, StringComparison.OrdinalIgnoreCase));
            if (count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                ContentRow($"  ✅  {expected.PadRight(nameWidth)} expected   — called {count} time{(count == 1 ? "" : "s")}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                ContentRow($"  ❌  {expected.PadRight(nameWidth)} EXPECTED   — NOT called  ← policy violation");
            }
            Console.ResetColor();
        }

        // 2. Unexpected tools that WERE called
        var unexpected = calledNames
            .Where(n => !expectedTools.Any(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => calls.First(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)).Order)
            .ToList();

        foreach (var name in unexpected)
        {
            var count = calls.Count(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow($"  ⚠️   {name.PadRight(nameWidth)} not expected — called {count} time{(count == 1 ? "" : "s")}");
            Console.ResetColor();
        }

        if (calls.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow("  ⚠️   No tool calls were recorded — cannot validate policy");
            Console.ResetColor();
        }
    }

    // ── Box-drawing helpers ──────────────────────────────────────────────────

    private static void TopBorder()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╔" + new string('═', BoxWidth - 2) + "╗");
        Console.ResetColor();
    }

    private static void BottomBorder()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╚" + new string('═', BoxWidth - 2) + "╝");
        Console.ResetColor();
    }

    private static void Divider()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╠" + new string('═', BoxWidth - 2) + "╣");
        Console.ResetColor();
    }

    private static void TitleRow(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("║ ");
        Console.ForegroundColor = ConsoleColor.White;
        var padded = title.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }

    private static void SectionRow(string heading)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        var padded = heading.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }

    private static void ContentRow(string content)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        // Keep existing foreground for the content
        var saved = Console.ForegroundColor;
        var padded = content.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }

    private static void MetaRowAgent(int? score, bool passed, int? passingScore, TimeSpan? duration, int toolCallCount)
    {
        // The quality gate ONLY (judge score vs pass mark). A null score is "not measured" (the judge gave no
        // verdict) — never shown as 0 or as FAILED.
        var mark = passingScore is { } p ? $" {(passed ? "≥" : "<")} {p}" : "";
        var (gate, color) = score is null ? ("⚠️ NOT MEASURED  (the judge gave no verdict)", ConsoleColor.Yellow)
                          : passed        ? ($"✅ PASSED  (judge score {score}/100{mark})", ConsoleColor.Green)
                          :                 ($"❌ FAILED  (judge score {score}/100{mark})", ConsoleColor.Red);
        MetaRow($"  Quality gate: {gate}", color);
        var durStr = duration.HasValue ? $"{duration.Value.TotalSeconds:F1}s" : "n/a";
        MetaRow($"  Duration: {durStr}   │  Tool calls: {toolCallCount}   │  tools, order, policy: checked below", ConsoleColor.Gray);
    }

    private static void MetaRowWorkflow(bool passed, int stepCount, TimeSpan? duration, int toolCallCount)
    {
        // The harness's own checks (executor order, expected tools, duration, errors) — not the quality gate.
        var passIcon = passed ? "✅ PASSED" : "❌ FAILED";
        var durStr   = duration.HasValue ? $"{duration.Value.TotalSeconds:F1}s" : "n/a";
        MetaRow($"  Harness checks: {passIcon}   │  Steps: {stepCount}   │  Duration: {durStr}   │  Tool calls: {toolCallCount}",
                passed ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static void MetaRow(string meta, ConsoleColor color)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║");
        Console.ForegroundColor = color;
        var padded = meta.PadRight(BoxWidth - 2);
        Console.Write(padded[..Math.Min(padded.Length, BoxWidth - 2)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("║");
        Console.ResetColor();
    }

    // ── Stochastic summary ───────────────────────────────────────────────────

    /// <summary>
    /// Prints a compact stochastic evaluation summary showing pass rate,
    /// min/mean/max score, and a per-run result strip. Runs that were not measured
    /// (model call or judge failed) are shown as such and kept out of every statistic.
    /// </summary>
    /// <param name="architecture">What ran.</param>
    /// <param name="tally">The runs, with the eval's own pass-rate threshold.</param>
    /// <param name="passingScore">The per-run passing score, for the header.</param>
    /// <param name="label">Panel title.</param>
    public static void PrintStochasticSummary(
        string architecture,
        StochasticTally tally,
        int passingScore,
        string label = "Stochastic Evaluation")
    {
        var scores  = tally.Scores;
        double mean = scores.Count > 0 ? scores.Average() : 0;

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();

        // Architecture + overall verdict
        var (verdictText, verdictColor) = tally.Verdict switch
        {
            StochasticVerdict.Pass       => ("✅ PASSED", ConsoleColor.Green),
            StochasticVerdict.Fail       => ("❌ FAILED", ConsoleColor.Red),
            StochasticVerdict.Incomplete => ($"⚠️ INCOMPLETE — {tally.Errored} of {tally.Total} runs not measured", ConsoleColor.Yellow),
            _                            => ("⚠️ NOT MEASURED — no run produced a verdict", ConsoleColor.Yellow),
        };
        // Two rows: the verdict on its own, so a long one (INCOMPLETE — k of N …) is never cut by the box.
        ContentRow($"  Architecture: {architecture}");
        Console.ForegroundColor = verdictColor;
        ContentRow($"  Verdict     : {verdictText}");
        Console.ResetColor();

        Divider();
        SectionRow($"  {tally.Total} RUNS  ·  top-3 criteria  ·  pass ≥ {passingScore}/100  ·  threshold ≥ {tally.Threshold * 100:F0}%");
        Divider();

        // Per-run strip: score + verdict; "—" where there is no score.
        var strip = string.Join("  ", tally.Runs.Select(r =>
        {
            var icon  = !r.Measured ? "⚠️" : r.Passed ? "✅" : "❌";
            var score = r.Score is { } s ? $"{s,3}" : "  —";
            return $"Run {r.Run}: {score} {icon}";
        }));
        foreach (var chunk in WrapText(strip, InnerWidth))
            ContentRow($"  {chunk}");

        Divider();
        SectionRow("STATISTICS  (pass rate: measured runs · scores: judged runs)");
        Divider();

        Console.ForegroundColor = ConsoleColor.White;
        // Nothing measured is "n/a", never a 0% pass rate or a 0 variance. The score lines say how many runs they
        // cover when that is not every run: a spread over 4 of 5 runs is not a spread over 5.
        var over = scores.Count > 0 && scores.Count < tally.Total ? $"   (over {scores.Count} of {tally.Total} runs)" : "";
        ContentRow(tally.Measured > 0
            ? $"  Pass rate  : {tally.PassedCount}/{tally.Measured}  ({tally.PassRate * 100:F1}%)   threshold: {tally.Threshold * 100:F0}%"
            : $"  Pass rate  : n/a (no run measured)   threshold: {tally.Threshold * 100:F0}%");
        ContentRow($"  Mean score : {(scores.Count > 0 ? $"{mean:F1}/100{over}" : "n/a")}");
        ContentRow($"  Min / Max  : {(scores.Count > 0 ? $"{scores.Min()} / {scores.Max()}{over}" : "n/a")}");
        ContentRow($"  Variance   : {(scores.Count > 0 ? $"{scores.Select(s => Math.Pow(s - mean, 2)).Average():F1}{over}" : "n/a")}");
        if (tally.Unscored > 0)
        {
            // A measured run with no judge score is a FAILED run — in the pass rate above — and is not in the spread.
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow($"  Unscored   : {tally.Unscored} of {tally.Total} runs had no judge score — FAILED in the pass rate,");
            ContentRow("               not in the spread: no consistency verdict on these runs");
        }
        if (tally.Errored > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow($"  Not measured: {tally.Errored} of {tally.Total} runs (model call or judge failed) — excluded above");
        }
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>Collapses a (redacted) multi-line message onto one console line.</summary>
    public static string OneLine(string? text) =>
        string.Join(" ", (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // ── Side-by-side hypothesis comparison ──────────────────────────────────

    /// <summary>
    /// Prints a side-by-side comparison of a single-agent eval snapshot vs a
    /// workflow eval snapshot to visually prove (or disprove) the hypothesis.
    /// Call this from Eval03 after loading both snapshots from disk.
    /// </summary>
    /// <param name="agent">The single-agent snapshot.</param>
    /// <param name="workflow">The workflow snapshot.</param>
    /// <param name="label">Panel title.</param>
    /// <param name="noVerdictReason">
    /// Set when the two snapshots do not differ in architecture alone (different or unrecorded models): the table is
    /// still shown, the hypothesis verdict is replaced by "NO VERDICT — {reason}".
    /// </param>
    public static void PrintComparison(
        EvalSnapshot? agent,
        EvalSnapshot? workflow,
        string label = "Hypothesis Comparison: Single Agent vs Workflow",
        string? noVerdictReason = null)
    {
        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        SectionRow("\"Is a structured workflow more deterministic than a single all-in-one agent?\"");
        Divider();

        if (agent is null && workflow is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow("  ⚠️  No snapshots found. Run Eval 1 and Eval 2 first.");
            Console.ResetColor();
            BottomBorder();
            Console.WriteLine();
            return;
        }

        // Column headers
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"Metric",-26}  {"Single Agent",16}  {"Workflow",16}  {"Winner",10}");
        Divider();

        CompareRow("LLM Score (0-100)",
            agent?.LlmScore.ToString() ?? "—",
            workflow?.LlmScore.ToString() ?? "—",
            agent?.LlmScore, workflow?.LlmScore, higherIsBetter: true);

        CompareRow("Criteria Score",
            agent    is null ? "—" : $"{agent.CriteriaScore}/100",
            workflow is null ? "—" : $"{workflow.CriteriaScore}/100",
            agent?.CriteriaScore, workflow?.CriteriaScore, higherIsBetter: true);

        CompareRow("Criteria Met",
            agent    is null ? "—" : $"{agent.CriteriaMetCount}/{agent.CriteriaTotal}",
            workflow is null ? "—" : $"{workflow.CriteriaMetCount}/{workflow.CriteriaTotal}",
            agent?.CriteriaMetCount, workflow?.CriteriaMetCount, higherIsBetter: true);

        CompareRow("Criteria Missed",
            agent?.MissedCount.ToString() ?? "—",
            workflow?.MissedCount.ToString() ?? "—",
            agent?.MissedCount, workflow?.MissedCount, higherIsBetter: false);

        CompareRow("Tool Calls",
            agent?.ToolCallCount.ToString() ?? "—",
            workflow?.ToolCallCount.ToString() ?? "—",
            null, null, higherIsBetter: true);

        CompareRow("BookFlight Count",
            agent?.BookFlightCount.ToString() ?? "—",
            workflow?.BookFlightCount.ToString() ?? "—",
            agent?.BookFlightCount, workflow?.BookFlightCount, higherIsBetter: true);

        CompareRow("Passed Quality Gate",
            agent    is null ? "—" : agent.Passed    ? "✅ Yes" : "❌ No",
            workflow is null ? "—" : workflow.Passed ? "✅ Yes" : "❌ No",
            null, null, higherIsBetter: true);

        // The quality gate is the judge's score only; these two rows carry what it does not: the deterministic checks
        // and the run's verdict over both. A snapshot saved before they were recorded says so.
        CompareRow("Structure / Tools / Policy", ChecksText(agent), ChecksText(workflow), null, null, higherIsBetter: true);
        CompareRow("Run Result", ResultText(agent), ResultText(workflow), null, null, higherIsBetter: true);

        Divider();

        // Verdict — only when architecture is the one variable between the two snapshots.
        if (agent is not null && workflow is not null && noVerdictReason is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow($"  ⚠️  NO VERDICT — {noVerdictReason}");
        }
        else if (agent is not null && workflow is not null)
        {
            bool workflowWins = workflow.LlmScore > agent.LlmScore
                             || workflow.CriteriaMetCount > agent.CriteriaMetCount;
            Console.ForegroundColor = workflowWins ? ConsoleColor.Green : ConsoleColor.Yellow;
            var verdict = workflowWins
                ? "  ✅ HYPOTHESIS CONFIRMED — workflow scored higher than single agent"
                : "  ⚠️  HYPOTHESIS NOT CONFIRMED — scores are equal or agent won this run";
            ContentRow(verdict);
        }

        // Source info (the model only with --show-model: the snapshots record it either way)
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (agent    is not null) ContentRow($"  Agent    snapshot  : {agent.RunAt:u}  —  {agent.Label}");
        if (agent    is not null && Config.ShowModel) ContentRow($"                      model {agent.ModelIdentity ?? "not recorded"}");
        if (workflow is not null) ContentRow($"  Workflow snapshot  : {workflow.RunAt:u}  —  {workflow.Label}");
        if (workflow is not null && Config.ShowModel) ContentRow($"                      model {workflow.ModelIdentity ?? "not recorded"}");
        ContentRow("  Tip: Re-run Eval 1 / Eval 2 to refresh snapshots, then re-run Eval 3");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    private static string ChecksText(EvalSnapshot? snapshot) => snapshot is null ? "—" : snapshot.StructurePassed switch
    {
        true  => "✅ Passed",
        false => "❌ Failed",
        null  => "not recorded",
    };

    private static string ResultText(EvalSnapshot? snapshot) =>
        snapshot is null ? "—" : snapshot.Result?.ToString().ToUpperInvariant() ?? "not recorded";

    // ── Stochastic spread comparison ─────────────────────────────────────────

    /// <summary>
    /// Prints a side-by-side comparison of stochastic snapshots from Eval04 (agent)
    /// and Eval05 (workflow), focusing on score spread as a proxy for determinism.
    /// </summary>
    /// <param name="agent">The Eval 04 snapshot.</param>
    /// <param name="workflow">The Eval 05 snapshot.</param>
    /// <param name="label">Panel title.</param>
    /// <param name="noVerdictReason">Set when the snapshots are not comparable (different or unrecorded models, unmeasured runs): no verdict.</param>
    public static void PrintStochasticComparison(
        StochasticSnapshot? agent,
        StochasticSnapshot? workflow,
        string label = "Stochastic Comparison: Agent vs Workflow Variance",
        string? noVerdictReason = null)
    {
        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        SectionRow("HYPOTHESIS: Workflow is more consistent (lower score spread)");
        Divider();

        if (agent is null && workflow is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow("  ⚠️  No stochastic snapshots. Run Eval 4 and/or Eval 5 first.");
            Console.ResetColor();
            BottomBorder();
            Console.WriteLine();
            return;
        }

        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"Metric",-28}  {"Single Agent",14}  {"Workflow",14}  {"Winner",10}");
        Divider();

        CompareRow("Pass Rate",
            agent    is null ? "—" : $"{agent.PassRate * 100:F1}%",
            workflow is null ? "—" : $"{workflow.PassRate * 100:F1}%",
            agent    is null ? null : (int)(agent.PassRate * 100),
            workflow is null ? null : (int)(workflow.PassRate * 100),
            higherIsBetter: true);

        CompareRow("Mean Score",
            agent    is null ? "—" : $"{agent.MeanScore:F1}",
            workflow is null ? "—" : $"{workflow.MeanScore:F1}",
            agent    is null ? null : (int)agent.MeanScore,
            workflow is null ? null : (int)workflow.MeanScore,
            higherIsBetter: true);

        CompareRow("Min Score",
            agent?.MinScore.ToString() ?? "—",
            workflow?.MinScore.ToString() ?? "—",
            agent?.MinScore, workflow?.MinScore, higherIsBetter: true);

        CompareRow("Max Score",
            agent?.MaxScore.ToString() ?? "—",
            workflow?.MaxScore.ToString() ?? "—",
            agent?.MaxScore, workflow?.MaxScore, higherIsBetter: true);

        // Spread: LOWER is better (tighter = more deterministic). Over k of n runs when not every run was scored.
        CompareRow("Score Spread (max − min)",
            SpreadText(agent), SpreadText(workflow),
            agent?.Spread, workflow?.Spread, higherIsBetter: false);

        CompareRow("Runs Passed (of measured)",
            agent    is null ? "—" : $"{agent.PassedCount}/{agent.MeasuredRuns}",
            workflow is null ? "—" : $"{workflow.PassedCount}/{workflow.MeasuredRuns}",
            agent?.PassedCount, workflow?.PassedCount, higherIsBetter: true);

        CompareRow("Met Its Pass Threshold",
            agent    is null ? "—" : agent.Passed    ? "✅ Yes" : "❌ No",
            workflow is null ? "—" : workflow.Passed ? "✅ Yes" : "❌ No",
            null, null, higherIsBetter: true);

        CompareRow("Runs Not Measured",
            agent?.ErroredRuns.ToString() ?? "—",
            workflow?.ErroredRuns.ToString() ?? "—",
            null, null, higherIsBetter: false);

        CompareRow("Runs Unscored (failed)",
            agent    is null ? "—" : agent.UnscoredRuns?.ToString()    ?? "not recorded",
            workflow is null ? "—" : workflow.UnscoredRuns?.ToString() ?? "not recorded",
            null, null, higherIsBetter: false);

        Divider();

        if (agent is not null && workflow is not null && noVerdictReason is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow($"  ⚠️  NO VERDICT — {noVerdictReason}");
        }
        else if (agent is not null && workflow is not null)
        {
            bool tighterSpread  = workflow.Spread   < agent.Spread;
            bool higherPassRate = workflow.PassRate  > agent.PassRate;
            // A tight spread of FAILING runs is not consistency: the workflow must also meet its own pass threshold
            // (Passed — threshold met, no run errored). Before, five structure-failed runs at 80 "confirmed" it.
            bool advantage      = tighterSpread || higherPassRate;
            bool confirmed      = advantage && workflow.Passed;

            Console.ForegroundColor = confirmed ? ConsoleColor.Green : ConsoleColor.Yellow;
            ContentRow(confirmed ? "  ✅ HYPOTHESIS CONFIRMED — workflow shows greater consistency"
                     : advantage ? "  ⚠️  HYPOTHESIS NOT CONFIRMED — the workflow missed its own pass threshold"
                     :             "  ⚠️  HYPOTHESIS NOT CONFIRMED — no clear consistency advantage");
            if (advantage && !workflow.Passed)
                ContentRow($"     ({workflow.PassedCount}/{workflow.MeasuredRuns} runs passed): a tight spread of failing runs is not consistency");

            if (tighterSpread)
            {
                Console.ForegroundColor = confirmed ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray;
                ContentRow($"     Spread: Agent {agent.Spread} pts  vs  Workflow {workflow.Spread} pts" +
                           $"  (Δ = {agent.Spread - workflow.Spread} pts tighter)");
            }
        }

        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (agent    is not null) ContentRow($"  Agent    : {agent.RunAt:u}  ·  {agent.Label}");
        if (agent    is not null && Config.ShowModel) ContentRow($"             model {agent.ModelIdentity ?? "not recorded"}");
        if (workflow is not null) ContentRow($"  Workflow : {workflow.RunAt:u}  ·  {workflow.Label}");
        if (workflow is not null && Config.ShowModel) ContentRow($"             model {workflow.ModelIdentity ?? "not recorded"}");
        ContentRow("  Tip: Re-run Eval 4 / Eval 5 to refresh stochastic snapshots");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    private static string SpreadText(StochasticSnapshot? snapshot) =>
        snapshot is null ? "—"
        : snapshot.ScoredRuns < snapshot.Runs ? $"{snapshot.Spread} ({snapshot.ScoredRuns}/{snapshot.Runs} runs)"
        : snapshot.Spread.ToString();

    private static void CompareRow(
        string metric,
        string agentVal,
        string workflowVal,
        int? agentNum,
        int? workflowNum,
        bool higherIsBetter)
    {
        string winner = "—";
        ConsoleColor winnerColor = ConsoleColor.DarkGray;

        if (agentNum.HasValue && workflowNum.HasValue)
        {
            if (agentNum == workflowNum)
            {
                winner = "Tie";
                winnerColor = ConsoleColor.DarkGray;
            }
            else
            {
                // The winner is the BETTER side: the lower number where lower is better (spread, criteria missed).
                // ▲ = won by being higher, ▼ = won by being lower. The old rule named the higher side either way,
                // so a lower-is-better row called the loser the winner.
                bool agentWins = higherIsBetter ? agentNum > workflowNum : agentNum < workflowNum;
                var arrow = higherIsBetter ? "▲" : "▼";
                winner = agentWins ? $"Agent {arrow}" : $"Workflow {arrow}";
                winnerColor = agentWins ? ConsoleColor.Yellow : ConsoleColor.Green;
            }
        }

        Console.ForegroundColor = ConsoleColor.White;
        var line = $"  {metric,-26}  {agentVal,16}  {workflowVal,16}";
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(line.PadRight(InnerWidth - 12)[..Math.Min(line.Length, InnerWidth - 12)]);
        Console.ForegroundColor = winnerColor;
        Console.Write($"  {winner,-10}");
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }
}
