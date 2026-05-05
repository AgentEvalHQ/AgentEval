// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Core;
using AgentEval.Models;

namespace ECS2026MAF.Evals;

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
    public static void PrintAgentResult(
        TestResult result,
        IReadOnlyList<string> expectedTools,
        string label = "Agent Evaluation")
    {
        var report = result.ToolUsage;
        var dur    = result.Performance?.TotalDuration;
        var calls  = report?.Calls ?? [];

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        MetaRowAgent(result.Score, result.Passed, dur, result.ToolCallCount);
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
            // Grouped by executor: show each executor as a sub-section
            foreach (var step in steps)
            {
                var stepCalls = calls
                    .Where(c => c.ExecutorId == step.ExecutorId)
                    .OrderBy(c => c.Order)
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
                        .GroupBy(c => c.Name)
                        .OrderBy(g => g.Min(c => c.Order))
                        .ToList();
                    int labelW = grouped2.Max(g => g.Key.Length) + 4;
                    foreach (var nameGroup in grouped2)
                    {
                        var firstOrder = nameGroup.Min(c => c.Order);
                        PrintTimelineRow($"   #{firstOrder}", nameGroup.Key, nameGroup.ToList(), labelW);
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

    private static void MetaRowAgent(int score, bool passed, TimeSpan? duration, int toolCallCount)
    {
        var passIcon = passed ? "✅ PASSED" : "❌ FAILED";
        var durStr   = duration.HasValue ? $"{duration.Value.TotalSeconds:F1}s" : "n/a";
        var meta     = $"  Score: {score}/100   │  {passIcon}   │  Duration: {durStr}   │  Tool calls: {toolCallCount}";
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║");
        if (passed) Console.ForegroundColor = ConsoleColor.Green;
        else        Console.ForegroundColor = ConsoleColor.Red;
        var padded = meta.PadRight(BoxWidth - 2);
        Console.Write(padded[..Math.Min(padded.Length, BoxWidth - 2)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("║");
        Console.ResetColor();
    }

    private static void MetaRowWorkflow(bool passed, int stepCount, TimeSpan? duration, int toolCallCount)
    {
        var passIcon = passed ? "✅ PASSED" : "❌ FAILED";
        var durStr   = duration.HasValue ? $"{duration.Value.TotalSeconds:F1}s" : "n/a";
        var meta     = $"  {passIcon}   │  Steps: {stepCount}   │  Duration: {durStr}   │  Tool calls: {toolCallCount}";
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║");
        if (passed) Console.ForegroundColor = ConsoleColor.Green;
        else        Console.ForegroundColor = ConsoleColor.Red;
        var padded = meta.PadRight(BoxWidth - 2);
        Console.Write(padded[..Math.Min(padded.Length, BoxWidth - 2)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("║");
        Console.ResetColor();
    }

    // ── Stochastic summary ───────────────────────────────────────────────────

    /// <summary>
    /// Prints a compact stochastic evaluation summary showing pass rate,
    /// min/mean/max score, and a per-run result strip.
    /// </summary>
    public static void PrintStochasticSummary(
        string architecture,
        int runs,
        IReadOnlyList<int> scores,
        double passRate,
        bool overallPassed,
        string label = "Stochastic Evaluation")
    {
        int passedCount = (int)Math.Round(passRate * runs);
        double mean     = scores.Count > 0 ? scores.Average() : 0;
        int minS        = scores.Count > 0 ? scores.Min() : 0;
        int maxS        = scores.Count > 0 ? scores.Max() : 0;

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();

        // Architecture + overall verdict
        Console.ForegroundColor = overallPassed ? ConsoleColor.Green : ConsoleColor.Red;
        ContentRow($"  Architecture: {architecture}   │  {(overallPassed ? "✅ PASSED" : "❌ FAILED")}");
        Console.ResetColor();

        Divider();
        SectionRow($"  {runs} RUNS  ·  top-3 discriminating criteria  ·  threshold ≥ 60%");
        Divider();

        // Per-run strip
        var strip = string.Join("  ", scores.Select((s, i) =>
        {
            var icon = s >= 60 ? "✅" : "❌";
            return $"Run {i + 1}: {s,3} {icon}";
        }));
        foreach (var chunk in WrapText(strip, InnerWidth))
            ContentRow($"  {chunk}");

        Divider();
        SectionRow("STATISTICS");
        Divider();

        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  Pass rate  : {passedCount}/{runs}  ({passRate * 100:F1}%)   threshold: 60%");
        ContentRow($"  Mean score : {mean:F1}/100");
        ContentRow($"  Min / Max  : {minS} / {maxS}");
        ContentRow($"  Variance   : {(scores.Count > 1 ? scores.Select(s => Math.Pow(s - mean, 2)).Average() : 0):F1}");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    // ── Side-by-side hypothesis comparison ──────────────────────────────────

    /// <summary>
    /// Prints a side-by-side comparison of a single-agent eval snapshot vs a
    /// workflow eval snapshot to visually prove (or disprove) the hypothesis.
    /// Call this from Eval03 after loading both snapshots from disk.
    /// </summary>
    public static void PrintComparison(
        EvalSnapshot? agent,
        EvalSnapshot? workflow,
        string label = "Hypothesis Comparison: Single Agent vs Workflow")
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

        Divider();

        // Verdict
        if (agent is not null && workflow is not null)
        {
            bool workflowWins = workflow.LlmScore > agent.LlmScore
                             || workflow.CriteriaMetCount > agent.CriteriaMetCount;
            Console.ForegroundColor = workflowWins ? ConsoleColor.Green : ConsoleColor.Yellow;
            var verdict = workflowWins
                ? "  ✅ HYPOTHESIS CONFIRMED — workflow scored higher than single agent"
                : "  ⚠️  HYPOTHESIS NOT CONFIRMED — scores are equal or agent won this run";
            ContentRow(verdict);
        }

        // Source info
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (agent    is not null) ContentRow($"  Agent    snapshot  : {agent.RunAt:u}  —  {agent.Label}");
        if (workflow is not null) ContentRow($"  Workflow snapshot  : {workflow.RunAt:u}  —  {workflow.Label}");
        ContentRow("  Tip: Re-run Eval 1 / Eval 2 to refresh snapshots, then re-run Eval 3");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    // ── Stochastic spread comparison ─────────────────────────────────────────

    /// <summary>
    /// Prints a side-by-side comparison of stochastic snapshots from Eval04 (agent)
    /// and Eval05 (workflow), focusing on score spread as a proxy for determinism.
    /// </summary>
    public static void PrintStochasticComparison(
        StochasticSnapshot? agent,
        StochasticSnapshot? workflow,
        string label = "Stochastic Comparison: Agent vs Workflow Variance")
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

        // Spread: LOWER is better (tighter = more deterministic)
        CompareRow("Score Spread (max − min)",
            agent    is null ? "—" : agent.Spread.ToString(),
            workflow is null ? "—" : workflow.Spread.ToString(),
            agent?.Spread, workflow?.Spread, higherIsBetter: false);

        CompareRow("Runs Passed",
            agent    is null ? "—" : $"{agent.PassedCount}/{agent.Runs}",
            workflow is null ? "—" : $"{workflow.PassedCount}/{workflow.Runs}",
            agent?.PassedCount, workflow?.PassedCount, higherIsBetter: true);

        Divider();

        if (agent is not null && workflow is not null)
        {
            bool tighterSpread  = workflow.Spread   < agent.Spread;
            bool higherPassRate = workflow.PassRate  > agent.PassRate;
            bool confirmed      = tighterSpread || higherPassRate;

            Console.ForegroundColor = confirmed ? ConsoleColor.Green : ConsoleColor.Yellow;
            ContentRow(confirmed
                ? "  ✅ HYPOTHESIS CONFIRMED — workflow shows greater consistency"
                : "  ⚠️  HYPOTHESIS NOT CONFIRMED — no clear consistency advantage");

            if (tighterSpread)
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                ContentRow($"     Spread: Agent {agent.Spread} pts  vs  Workflow {workflow.Spread} pts" +
                           $"  (Δ = {agent.Spread - workflow.Spread} pts tighter)");
            }
        }

        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (agent    is not null) ContentRow($"  Agent    : {agent.RunAt:u}  ·  {agent.Label}");
        if (workflow is not null) ContentRow($"  Workflow : {workflow.RunAt:u}  ·  {workflow.Label}");
        ContentRow("  Tip: Re-run Eval 4 / Eval 5 to refresh stochastic snapshots");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

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
            if (agentNum > workflowNum)
            {
                winner = higherIsBetter ? "Agent ▲" : "Agent ▼";
                winnerColor = higherIsBetter ? ConsoleColor.Yellow : ConsoleColor.Green;
            }
            else if (workflowNum > agentNum)
            {
                winner = higherIsBetter ? "Workflow ▲" : "Workflow ▼";
                winnerColor = ConsoleColor.Green;
            }
            else
            {
                winner = "Tie";
                winnerColor = ConsoleColor.DarkGray;
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
