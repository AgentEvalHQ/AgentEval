// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// STAGE 1 driver: runs the SAME generate→sweep→patch→resweep loop on two arms and contrasts them —
//   ARM A  keyword oracle (deterministic, patched each round)   → expected: fresh fabrications do NOT vanish (C1)
//   ARM B  composite (calibrated V1 judge, NOT patched)         → expected: fresh fabrications stay ~0 (the contrast)
// Numbers are whatever the run produces — measure, never assert.
using System.Text;
using AgentEval.RedTeam;
using Microsoft.Extensions.AI;

namespace AgentEval.SampleGraders.NonConvergence;

public static class NonConvergenceRunner
{
    public static async Task<int> RunAsync(IChatClient judge, bool isReal, string[] args)
    {
        int rounds = IntArg(args, "--rounds", 6);
        int perRound = IntArg(args, "--per-round", 16);
        int seed = IntArg(args, "--seed", 1117);

        Console.WriteLine("AgentEval.SampleGraders — STAGE 1: non-convergence-of-patching + keyword-vs-composite contrast");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(isReal
            ? $"Composite judge: REAL Azure OpenAI ('{Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini"}')."
            : "Composite judge: OFFLINE heuristic stand-in (set AZURE_OPENAI_* for the real run).");
        Console.WriteLine($"Protocol: seeded generator over a fixed finite space; {rounds} rounds x {perRound} FRESH cases/round; deterministic patcher; sweep->patch->resweep.");
        Console.WriteLine();

        IReadOnlyList<IReadOnlyList<GraderCase>> batches = CaseGenerator.GenerateRounds(seed, rounds, perRound);

        // ARM A — KEYWORD ORACLE (deterministic; patched each round).
        var oracle = new PatchableKeywordOracle();
        var patcher = new DeterministicPatcher(oracle);
        IReadOnlyList<RoundResult> keyword = await NonConvergenceLoop.RunAsync(
            batches, oracle, patcher.PatchFalseAlarm, patcher.PatchMiss, () => (oracle.PositiveRules, oracle.NegativeRules));

        // ARM B — COMPOSITE (PRODUCTION decompose routing — the SAME grader the proof's §5b validates; NOT patched).
        var compFails = new List<(string Kind, GraderCase Case)>();
        IProbeEvaluator composite = DecomposedGraders.TryBuildFor("InferenceAPIAbuse", judge)
            ?? throw new InvalidOperationException("DecomposedGraders has no build for InferenceAPIAbuse.");
        IReadOnlyList<RoundResult> comp = await NonConvergenceLoop.RunAsync(batches, composite, null, null, null, compFails);

        PrintTrajectory("ARM A — KEYWORD ORACLE (patched each round)", keyword, withRules: true);
        PrintTrajectory("ARM B — COMPOSITE, production decompose (judge; NOT patched; proof §5b grader)", comp, withRules: false);

        if (compFails.Count > 0)
        {
            Console.WriteLine($"DIAGNOSTIC — the {compFails.Count} cases the COMPOSITE got wrong (look: borderline over-labelling, or a real gap?):");
            foreach ((string kind, GraderCase c) in compFails)
                Console.WriteLine($"  [{kind,-11}] truth={c.Expected,-9} | {Trunc(c.Response, 88)}");
            Console.WriteLine();
        }

        int tail = Math.Min(3, rounds);
        double kwTail = keyword.TakeLast(tail).Average(r => (double)r.FreshFabrications);
        double cpTail = comp.TakeLast(tail).Average(r => (double)r.FreshFabrications);
        Console.WriteLine($"Tail (last {tail} rounds) mean fresh fabrications:  keyword = {kwTail:0.0}   composite = {cpTail:0.0}");
        Console.WriteLine(kwTail > cpTail + 0.5
            ? $"=> keyword patching does NOT drive fresh fabrications to the composite's level (gap = {kwTail - cpTail:0.0}). The non-convergence contrast HOLDS on this run."
            : "=> the gap did not appear on this run — reporting honestly; inspect generator diversity / rounds / seed.");
        Console.WriteLine();

        await WriteResultsAsync(keyword, comp, rounds, perRound, seed, isReal);
        judge.Dispose();
        return 0;
    }

    private static void PrintTrajectory(string title, IReadOnlyList<RoundResult> traj, bool withRules)
    {
        Console.WriteLine(title + ":");
        Console.WriteLine(withRules
            ? $"  {"round",-6} {"fresh-fab",-10} {"false-alarm",-12} {"missed",-7} {"+rules",-7} {"-rules",-7}"
            : $"  {"round",-6} {"fresh-fab",-10} {"false-alarm",-12} {"missed",-7}");
        foreach (RoundResult r in traj)
            Console.WriteLine(withRules
                ? $"  {r.Round,-6} {r.FreshFabrications,-10} {r.FalseAlarms,-12} {r.MissedHits,-7} {r.PositiveRules,-7} {r.NegativeRules,-7}"
                : $"  {r.Round,-6} {r.FreshFabrications,-10} {r.FalseAlarms,-12} {r.MissedHits,-7}");
        Console.WriteLine();
    }

    private static async Task WriteResultsAsync(IReadOnlyList<RoundResult> kw, IReadOnlyList<RoundResult> cp, int rounds, int perRound, int seed, bool isReal)
    {
        string dir;
        try
        {
            dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "strategy", "redteam", "paper", "build", "results"));
            Directory.CreateDirectory(dir);
        }
        catch { dir = Environment.CurrentDirectory; }

        var sb = new StringBuilder();
        sb.AppendLine($"{{ \"experiment\": \"nonconvergence-contrast\", \"isReal\": {isReal.ToString().ToLowerInvariant()}, \"rounds\": {rounds}, \"perRound\": {perRound}, \"seed\": {seed},");
        sb.Append("  \"keyword\": [");
        sb.Append(string.Join(", ", kw.Select(r => $"{{\"round\":{r.Round},\"fresh\":{r.FreshFabrications},\"fa\":{r.FalseAlarms},\"miss\":{r.MissedHits},\"pos\":{r.PositiveRules},\"neg\":{r.NegativeRules}}}")));
        sb.AppendLine("],");
        sb.Append("  \"composite\": [");
        sb.Append(string.Join(", ", cp.Select(r => $"{{\"round\":{r.Round},\"fresh\":{r.FreshFabrications},\"fa\":{r.FalseAlarms},\"miss\":{r.MissedHits}}}")));
        sb.AppendLine("] }");

        string path = Path.Combine(dir, "nonconvergence-result.json");
        await File.WriteAllTextAsync(path, sb.ToString());
        Console.WriteLine($"Wrote {path}");
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..(n - 1)] + "…";

    private static int IntArg(string[] args, string name, int dflt)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out int v) && v > 0) return v;
        return dflt;
    }
}
