// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Tracing;

namespace AgentEval.Samples;

/// <summary>
/// Renders an honest MAF-vs-Glass Box comparison from a SINGLE real run captured two ways: the framework's
/// own account (<see cref="MafLens"/>, derived from MAF's <c>AgentResponse</c>) and the Glass Box chat/tool
/// boundary trace (from <c>TraceRecordingChatClient</c> + <c>EvaluatingAIFunction</c>).
/// </summary>
/// <remarks>
/// Honesty rules baked in: every row is DERIVED from the captured traces, never hand-typed. Token totals and
/// tool-call counts are shown as <see cref="DiffKind.Agree"/> when they match (MEAI's function-invocation loop
/// sums usage and preserves tool calls — so the framework does NOT lie about totals). The value rows are
/// <see cref="DiffKind.MafHides"/>: per-turn detail the framework rolls up and discards. A red
/// <see cref="DiffKind.MafDiverges"/> row is emitted ONLY when a per-turn finish reason genuinely contradicts
/// the framework's single reported one (e.g. a content_filter/length turn surfaced as "stop") — never fabricated.
/// </remarks>
public static class GlassBoxComparison
{
    /// <summary>How a row's MAF and Glass Box cells relate.</summary>
    public enum DiffKind
    {
        /// <summary>Both lenses agree — the framework's account is accurate here.</summary>
        Agree,

        /// <summary>The framework is silent / aggregated; Glass Box has the detail.</summary>
        MafHides,

        /// <summary>The framework's account genuinely contradicts what happened at the wire.</summary>
        MafDiverges,
    }

    /// <summary>One row of the comparison table. <paramref name="Provenance"/> is observed | induced | opportunistic.</summary>
    public sealed record Row(string Description, string Maf, string GlassBox, DiffKind Diff, string Provenance = "observed", string? Note = null);

    /// <summary>The framework's native account of a run, extracted from MAF's <c>AgentResponse</c>.</summary>
    public sealed record MafLens(int? TotalInTokens, int? TotalOutTokens, int ToolCallCount, string? FinishReason);

    /// <summary>Builds the comparison rows from the chat-boundary trace and the framework's account. Pure / deterministic.</summary>
    public static IReadOnlyList<Row> BuildRows(AgentTrace chatTrace, MafLens maf)
    {
        ArgumentNullException.ThrowIfNull(chatTrace);
        ArgumentNullException.ThrowIfNull(maf);

        var responses = chatTrace.Entries
            .Where(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Response })
            .ToList();
        var requests = chatTrace.Entries
            .Where(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Request })
            .ToList();
        var toolExecs = chatTrace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ToolExecution)
            .ToList();

        var roundTrips = responses.Count;
        var chatIn = responses.Sum(e => e.TokenUsage?.PromptTokens ?? 0);
        var chatOut = responses.Sum(e => e.TokenUsage?.CompletionTokens ?? 0);
        var chatToolCalls = responses.Sum(e => e.ToolCalls?.Count ?? 0);

        var perTurnTokens = string.Join("  ", responses.Select((e, i) =>
            $"t{i}:{e.TokenUsage?.PromptTokens ?? 0}/{e.TokenUsage?.CompletionTokens ?? 0}"));

        var systemPrompt = requests.Select(r => r.SystemPrompt).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        var toolDefNames = requests
            .SelectMany(r => r.ToolDefinitions ?? new List<TraceToolDefinition>())
            .Select(d => d.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var perTurnFinishes = responses.Select(e => e.FinishReason ?? "—").ToList();
        var distinctFinishes = perTurnFinishes.Where(f => f != "—").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var providerIntervention = responses.FirstOrDefault(e =>
            string.Equals(e.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.FinishReason, "length", StringComparison.OrdinalIgnoreCase));

        var rows = new List<Row>
        {
            new("LLM round-trips (model calls)",
                "— not surfaced",
                $"{roundTrips}",
                DiffKind.MafHides,
                Note: "the framework returns one final answer; Glass Box counts every model call in the loop"),

            new("Total tokens (in / out)",
                maf.TotalInTokens is null ? "— none reported" : $"{maf.TotalInTokens} / {maf.TotalOutTokens}",
                $"{chatIn} / {chatOut}",
                maf.TotalInTokens == chatIn && maf.TotalOutTokens == chatOut ? DiffKind.Agree : DiffKind.MafDiverges,
                Note: maf.TotalInTokens == chatIn ? "framework SUMS usage faithfully — no lie about totals" : "framework total disagrees with the summed per-turn usage"),

            new("Per-turn token split",
                "— aggregate only",
                string.IsNullOrEmpty(perTurnTokens) ? "(none)" : perTurnTokens,
                DiffKind.MafHides,
                Note: "you cannot attribute cost to a specific turn from the framework's view"),

            new("System prompt actually sent",
                "— not returned",
                systemPrompt is null ? "(none)" : $"{systemPrompt.Length} chars: \"{Clip(systemPrompt, 40)}\"",
                DiffKind.MafHides),

            new("Tool schemas offered per turn",
                "— not returned",
                toolDefNames.Count == 0 ? "(none)" : $"{toolDefNames.Count}: {Clip(string.Join(", ", toolDefNames), 48)}",
                DiffKind.MafHides),

            new("Tool calls (count)",
                $"{maf.ToolCallCount}",
                $"{chatToolCalls}",
                maf.ToolCallCount == chatToolCalls ? DiffKind.Agree : DiffKind.MafDiverges,
                Note: maf.ToolCallCount == chatToolCalls ? "framework preserves tool calls — no hidden retry on this run" : "framework dropped/added tool calls vs the wire"),

            new("Tool execution timing / success",
                "— result text only",
                toolExecs.Count == 0 ? "(none captured)" : $"{toolExecs.Count} exec(s); e.g. {ExecSummary(toolExecs[0])}",
                DiffKind.MafHides,
                Note: "the framework shows the result string; Glass Box shows duration + success/failure per tool"),

            new("Finish reason(s)",
                maf.FinishReason ?? "— none",
                perTurnFinishes.Count == 0 ? "(none)" : string.Join(", ", perTurnFinishes),
                providerIntervention is not null && !string.Equals(maf.FinishReason, providerIntervention.FinishReason, StringComparison.OrdinalIgnoreCase)
                    ? DiffKind.MafDiverges
                    : DiffKind.MafHides,
                Provenance: providerIntervention is not null ? "opportunistic" : "observed",
                Note: providerIntervention is not null
                    ? $"a turn finished with '{providerIntervention.FinishReason}' (provider intervention) that the framework's single finish reason hides"
                    : "the framework collapses per-turn finish reasons to one final value"),
        };

        return rows;
    }

    /// <summary>Prints the comparison table to the console with colour-coded DIFF cells, plus a cost-attribution callout.</summary>
    public static void RenderConsole(string title, IReadOnlyList<Row> rows, TraceFidelityReport fidelity, AgentTrace chatTrace)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  ── {title} — MAF vs Glass Box ─────────────────────────────────");
        Console.ResetColor();

        WriteRows(rows);

        // Cost-attribution consequence: which turn burned the most tokens (the value MAF hides).
        var responses = chatTrace.Entries.Where(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Response }).ToList();
        if (responses.Count > 0)
        {
            var heaviest = responses
                .Select((e, i) => (Turn: i, Tokens: (e.TokenUsage?.PromptTokens ?? 0) + (e.TokenUsage?.CompletionTokens ?? 0)))
                .OrderByDescending(x => x.Tokens)
                .First();
            var total = responses.Sum(e => (e.TokenUsage?.PromptTokens ?? 0) + (e.TokenUsage?.CompletionTokens ?? 0));
            if (total > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n  💡 Cost attribution (MAF can't give you this): turn {heaviest.Turn} burned " +
                                  $"{heaviest.Tokens} tokens ({heaviest.Tokens * 100.0 / total:F0}% of {total}).");
                Console.ResetColor();
            }
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"\n  Trace Fidelity: {fidelity.OverallScore * 100:F0}% — the framework's account is " +
                          $"{(fidelity.OverallScore >= 0.99 ? "ACCURATE" : "INCOMPLETE")} but it HIDES the per-turn detail above.");
        Console.ResetColor();
    }

    /// <summary>Renders the comparison as a paste-able Markdown table (full cell values). Fidelity footer is optional.</summary>
    public static string RenderMarkdown(string title, IReadOnlyList<Row> rows, TraceFidelityReport? fidelity = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### {title} — MAF vs Glass Box");
        sb.AppendLine();
        sb.AppendLine("| Description | MAF | Glass Box | DIFF |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in rows)
        {
            var diff = r.Diff switch
            {
                DiffKind.Agree => "✅ agree",
                DiffKind.MafHides => "🟡 **MAF hides** — Glass Box has it",
                _ => "🔴 **MAF diverges**",
            };
            var note = r.Note is null ? "" : $"<br/>_{r.Note}_";
            sb.AppendLine($"| {Md(r.Description)} | {Md(r.Maf)} | {Md(r.GlassBox)} | {diff}{note} |");
        }
        sb.AppendLine();
        sb.AppendLine(fidelity is not null
            ? $"_Trace Fidelity: **{fidelity.OverallScore * 100:F0}%** — the framework does not lie about totals; it hides the per-turn detail._"
            : "_The framework does not lie about the result; it hides the per-agent cost ledger above._");
        return sb.ToString();
    }

    private static void WriteRows(IReadOnlyList<Row> rows)
    {
        foreach (var r in rows)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  ▸ {r.Description}  [{r.Provenance}]");
            Console.ResetColor();
            Console.WriteLine($"      MAF      : {r.Maf}");
            Console.WriteLine($"      GlassBox : {r.GlassBox}");
            Console.Write("      DIFF     : ");
            (Console.ForegroundColor, var label) = r.Diff switch
            {
                DiffKind.Agree => (ConsoleColor.Green, "AGREE"),
                DiffKind.MafHides => (ConsoleColor.Yellow, "MAF HIDES"),
                _ => (ConsoleColor.Red, "MAF DIVERGES"),
            };
            Console.Write(label);
            Console.ResetColor();
            Console.WriteLine(r.Note is null ? "" : $" — {r.Note}");
        }
    }

    private static string ExecSummary(TraceEntry exec)
    {
        var call = exec.ToolCalls?.FirstOrDefault();
        var ok = call?.Succeeded == true ? "ok" : "FAILED";
        return $"{call?.Name ?? "tool"} {exec.DurationMs}ms {ok}";
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].Replace("\n", " ") + "…";

    private static string Md(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
