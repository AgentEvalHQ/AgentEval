// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// The canonical generate → sweep → (count fresh fabrications) → patch → resweep loop (Implementation-Plan §S1).
// Each round is swept with the oracle FROZEN; fabrications are counted, THEN patches are applied — so the per-round
// count is an honest measurement of the oracle as it stood entering that round.
using AgentEval.RedTeam;

namespace AgentEval.SampleGraders.NonConvergence;

/// <summary>One round's fresh-fabrication measurement (both directions) + the oracle's rule counts entering the round.</summary>
public sealed record RoundResult(int Round, int FreshFabrications, int FalseAlarms, int MissedHits, int BatchSize, int PositiveRules, int NegativeRules);

public static class NonConvergenceLoop
{
    public static async Task<IReadOnlyList<RoundResult>> RunAsync(
        IReadOnlyList<IReadOnlyList<GraderCase>> batches,
        IProbeEvaluator oracle,
        Action<string>? patchFalseAlarm,
        Action<string>? patchMiss,
        Func<(int pos, int neg)>? ruleCounts,
        ICollection<(string Kind, GraderCase Case)>? fabricationSink = null,
        ICollection<(EvaluationOutcome Outcome, EvaluationOutcome Expected)>? outcomeSink = null)
    {
        var results = new List<RoundResult>(batches.Count);
        for (int r = 0; r < batches.Count; r++)
        {
            IReadOnlyList<GraderCase> batch = batches[r];
            var faTexts = new List<string>();
            var missTexts = new List<string>();
            foreach (GraderCase c in batch)
            {
                EvaluationOutcome outcome = (await oracle.EvaluateAsync(c.Probe, c.Response).ConfigureAwait(false)).Outcome;
                outcomeSink?.Add((outcome, c.Expected));  // full distribution (to show DETECTION vs mere abstention)
                if (outcome == c.Expected || outcome == EvaluationOutcome.Inconclusive) continue;  // correct, or an honest coverage gap
                if (c.Expected == EvaluationOutcome.Resisted) { faTexts.Add(c.Response); fabricationSink?.Add(("FALSE ALARM", c)); }  // safe → Succeeded
                else { missTexts.Add(c.Response); fabricationSink?.Add(("MISSED HIT", c)); }                                          // vuln → Resisted
            }
            (int pos, int neg) = ruleCounts?.Invoke() ?? (0, 0);
            results.Add(new RoundResult(r + 1, faTexts.Count + missTexts.Count, faTexts.Count, missTexts.Count, batch.Count, pos, neg));

            // Sweep complete → now patch (the oracle was frozen for the whole sweep above).
            if (patchFalseAlarm is not null) foreach (string t in faTexts) patchFalseAlarm(t);
            if (patchMiss is not null) foreach (string t in missTexts) patchMiss(t);
        }
        return results;
    }
}
