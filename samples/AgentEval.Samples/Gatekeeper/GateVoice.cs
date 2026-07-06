// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Guardrails;
using AgentEval.Tracing;

namespace AgentEval.Samples;

/// <summary>
/// Surfaces "the Gatekeeper's thinking" — the gate verdicts (policy, stage, action, reason) a run recorded into
/// its Glass Box trace — in a distinct colour, so a blocked run shows <b>why</b> it was stopped, not just a dead
/// "(none)". Allow verdicts aren't recorded, so this only speaks when a gate actually acted.
/// </summary>
internal static class GateVoice
{
    private const ConsoleColor Voice = ConsoleColor.Cyan;

    /// <summary>Prints every gate verdict recorded in <paramref name="trace"/> (tool + run gates), ordered by sequence.</summary>
    public static void Speak(AgentTrace trace, string indent = "   ")
    {
        var meta = trace.Metadata;
        if (meta is null)
        {
            return;
        }

        var verdicts = meta
            .Where(kv => GateMetadataReader.IsGateKey(kv.Key))
            .Select(kv => (
                Seq: SeqOf(kv.Key),
                Policy: GateMetadataReader.PolicyFromKey(kv.Key) ?? kv.Key,
                Stage: GateMetadataReader.StageFromKey(kv.Key) ?? "?",
                Action: Field(kv.Value, "action") ?? "?",
                Reason: Field(kv.Value, "reason")))
            .OrderBy(v => v.Seq)
            .ToList();
        if (verdicts.Count == 0)
        {
            return;
        }

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = Voice;
        Console.WriteLine($"{indent}🚪 the Gatekeeper's verdict:");
        foreach (var v in verdicts)
        {
            Console.WriteLine($"{indent}   {Mark(v.Action)} {v.Policy} [{v.Stage}] {v.Action} — {v.Reason}");
        }

        Console.ForegroundColor = prev;
    }

    /// <summary>Prints a single gate/judge verdict directly (for judges used outside the trace-recording seam, e.g. the Tribunal).</summary>
    public static void SpeakVerdict(string title, GateVerdict verdict, string indent = "   ")
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = Voice;
        Console.WriteLine($"{indent}🚪 {title}: {(verdict.Action == GateAction.Block ? "⛔" : "✔️")} {verdict.Action} — {verdict.Reason ?? "(no reason)"}");
        if (verdict.Matches is { Count: > 0 })
        {
            Console.WriteLine($"{indent}   evidence: {string.Join("  |  ", verdict.Matches)}");
        }

        Console.ForegroundColor = prev;
    }

    private static string Mark(string action) => action.ToUpperInvariant() switch
    {
        "BLOCK" => "⛔",
        "WARN" => "⚠️ ",
        "MUTATE" => "✎",
        _ => "•",
    };

    private static int SeqOf(string key)
    {
        var parts = key.Split('.');
        return parts.Length >= 3 && int.TryParse(parts[2], out var s) ? s : 0;
    }

    private static string? Field(object? value, string name)
        => value is IReadOnlyDictionary<string, object?> d && d.TryGetValue(name, out var v) ? v?.ToString() : null;
}
