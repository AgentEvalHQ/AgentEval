// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// Shared generation utilities so EVERY family uses the SAME (fixed) seeded shuffle + round-splitting —
// distinct seeds give distinct corpora (the earlier `(uint)seed | 1u` collapsed even/odd pairs; never duplicate that).
using AgentEval.RedTeam;

namespace AgentEval.SampleGraders.NonConvergence;

public static class GenUtil
{
    /// <summary>Deterministic Fisher–Yates; Knuth multiplicative-hash seed so DISTINCT seeds → DISTINCT shuffles.</summary>
    public static void Shuffle<T>(IList<T> list, int seed)
    {
        uint s = unchecked((uint)seed * 2654435761u + 1u);
        for (int i = list.Count - 1; i > 0; i--)
        {
            s = unchecked(s * 1664525u + 1013904223u);
            int j = (int)(s % (uint)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Shuffle vuln+safe, then deal FRESH non-overlapping ≈half/half batches per round.</summary>
    public static IReadOnlyList<IReadOnlyList<GraderCase>> SplitRounds(
        List<GraderCase> vuln, List<GraderCase> safe, int seed, int rounds, int perRound)
    {
        Shuffle(vuln, seed);
        Shuffle(safe, seed ^ 0x5bd1e995);
        int halfV = perRound / 2, halfS = perRound - halfV;
        var batches = new List<IReadOnlyList<GraderCase>>(rounds);
        int vi = 0, si = 0;
        for (int r = 0; r < rounds; r++)
        {
            var batch = new List<GraderCase>(perRound);
            for (int k = 0; k < halfV && vi < vuln.Count; k++) batch.Add(vuln[vi++]);
            for (int k = 0; k < halfS && si < safe.Count; k++) batch.Add(safe[si++]);
            Shuffle(batch, seed + r);
            batches.Add(batch);
        }
        return batches;
    }

    public static AttackProbe Probe(string id, string attackName, string technique, string prompt) => new()
    {
        Id = id, Prompt = prompt, Difficulty = Difficulty.Moderate, AttackName = attackName, Technique = technique,
    };
}
