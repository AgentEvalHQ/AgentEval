// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Builds a <see cref="StreamingOptions"/> with all five callbacks wired to a
/// console trace, so an eval run prints a live, color-coded record of:
/// </summary>
/// <list type="bullet">
///   <item>⚡ time-to-first-token (TTFT) — latency from request start to first chunk;</item>
///   <item>💬 streamed text chunks (real-time agent response, dimmed);</item>
///   <item>🔧 tool start (yellow) with name + ordinal + truncated argument preview;</item>
///   <item>✅ / ❌ tool complete (green / red) with name + duration + error message;</item>
///   <item>📈 periodic metrics-update snapshots (token count + cost so far).</item>
/// </list>
/// <remarks>
/// <para>
/// Centralised here so every TravelDemo.Evals eval that uses the streaming
/// surface (today: Eval01) shares an identical, copy-paste-free trace shape.
/// Eval02 (workflow path) uses <c>WorkflowEvaluationHarness</c> which has no
/// streaming surface today — those evals get verbose-mode harness output
/// instead of the callbacks below.
/// </para>
/// <para>
/// The text-chunk callback uses <c>Console.Write</c> (no newline) so the
/// agent's response streams in left-to-right exactly as it arrives — the
/// same visual feel as a live ChatGPT response. Tool events are printed on
/// their own lines so they don't interleave with the streaming text.
/// </para>
/// </remarks>
public static class StreamingTracePrinter
{
    /// <summary>
    /// Returns a <see cref="StreamingOptions"/> with all five callbacks
    /// wired to console output. Pass to
    /// <c>MAFEvaluationHarness.RunEvaluationStreamingAsync(agent, testCase, streamingOptions: …)</c>.
    /// </summary>
    public static StreamingOptions Create()
    {
        var firstTokenStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastMetricsUpdateAt = DateTimeOffset.UtcNow;

        return new StreamingOptions
        {
            OnFirstToken = ttft =>
            {
                Console.WriteLine(); // ensure new line before the badge
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  ⚡ First token in {ttft.TotalMilliseconds:F0} ms");
                Console.ResetColor();
                Console.WriteLine();
            },

            OnTextChunk = chunk =>
            {
                // Streaming response text — dimmed grey so tool events stand out.
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(chunk);
                Console.ResetColor();
            },

            OnToolStart = tool =>
            {
                // Newline first so we don't jam against a mid-line text chunk.
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"  🔧 [{tool.Order:D2}] {tool.Name}(");
                Console.Write(FormatArguments(tool.Arguments));
                Console.WriteLine(")");
                Console.ResetColor();
            },

            OnToolComplete = tool =>
            {
                Console.ForegroundColor = tool.Exception is null
                    ? ConsoleColor.Green
                    : ConsoleColor.Red;
                var status   = tool.Exception is null ? "✅" : "❌";
                var duration = tool.Duration is { } d
                    ? $"{d.TotalMilliseconds:F0} ms"
                    : "—";
                Console.WriteLine($"     {status} {tool.Name} → {duration}{(tool.Exception is null ? "" : $" — {tool.Exception.Message}")}");
                Console.ResetColor();
            },

            OnMetricsUpdate = metrics =>
            {
                // Periodic snapshots can be very chatty — gate to once per
                // ~2 seconds so the trace stays readable on long runs.
                var now = DateTimeOffset.UtcNow;
                if ((now - lastMetricsUpdateAt).TotalSeconds < 2) return;
                lastMetricsUpdateAt = now;

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine(
                    $"  📈 metrics @ {metrics.TotalDuration.TotalSeconds:F1}s: " +
                    $"{metrics.TotalTokens} tok " +
                    $"({metrics.PromptTokens}↗ / {metrics.CompletionTokens}↘)" +
                    (metrics.EstimatedCost is { } c ? $", ~${c:F4}" : ""));
                Console.ResetColor();
            }
        };

        // Keep the firstTokenStopwatch reference alive across closures even
        // though we don't read it after init — the harness invokes the
        // OnFirstToken callback with the TTFT it measured internally.
        _ = firstTokenStopwatch;
    }

    private static string FormatArguments(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return "";
        var pairs = args.Select(kvp =>
        {
            var v = kvp.Value?.ToString() ?? "null";
            if (v.Length > 40) v = v[..37] + "…";
            return $"{kvp.Key}={v}";
        });
        var joined = string.Join(", ", pairs);
        return joined.Length <= 100 ? joined : joined[..97] + "…";
    }
}
