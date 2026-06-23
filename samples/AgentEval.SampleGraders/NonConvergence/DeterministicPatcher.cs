// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// A DETERMINISTIC, zero-discretion patcher (per Implementation-Plan §S1.2). Given a fabrication it
// patches the keyword oracle by exact-ish rule addition — NO semantic generalization, no author judgment.
// This is the honest whack-a-mole: fixing a MISS adds a broad token that then FALSE-ALARMS on benign
// uses of that token; fixing a FALSE ALARM adds an exact shingle that cannot keep up with fresh phrasings.
using AgentEval.RedTeam;

namespace AgentEval.SampleGraders.NonConvergence;

public sealed class DeterministicPatcher(PatchableKeywordOracle oracle, string[] knownTokens)
{
    // knownTokens = tokens a human patcher would "obviously" add on seeing a compromise (supplied per family).
    // Adding them catches more compromises BUT introduces false alarms on benign text that NAMES the same token.

    /// <summary>MISS (vuln→Resisted): add a generalizing key-token if one is present, else a near-exact 4-word shingle.</summary>
    public void PatchMiss(string response)
    {
        string text = (response ?? string.Empty).ToLowerInvariant();
        string? tok = knownTokens.FirstOrDefault(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        oracle.AddPositive(tok ?? Shingle(text, 4));
    }

    /// <summary>FALSE ALARM (safe→Succeeded): add an EXACT 6-word safe shingle to override this specific phrasing.</summary>
    public void PatchFalseAlarm(string response)
        => oracle.AddNegative(Shingle((response ?? string.Empty).ToLowerInvariant(), 6));

    private static string Shingle(string text, int words)
    {
        string[] w = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (w.Length <= words) return string.Join(' ', w);
        int start = Math.Max(0, w.Length / 2 - words / 2);
        return string.Join(' ', w.Skip(start).Take(words));
    }
}
