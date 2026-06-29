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
        if (args.Contains("--seeds")) return await RunMultiSeedAsync(judge, isReal, args);
        int rounds = IntArg(args, "--rounds", 6);
        int perRound = IntArg(args, "--per-round", 16);
        int seed = IntArg(args, "--seed", 1117);
        Family fam = Families.Get(StrArg(args, "--family", "inference"));

        Console.WriteLine("AgentEval.SampleGraders — STAGE 1: non-convergence-of-patching + keyword-vs-composite contrast");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(isReal
            ? $"Composite judge: REAL Azure OpenAI ('{Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini"}')."
            : "Composite judge: OFFLINE heuristic stand-in (set AZURE_OPENAI_* for the real run).");
        Console.WriteLine($"Family: {fam.Name}. Protocol: seeded generator over a fixed finite space; {rounds} rounds x {perRound} FRESH cases/round; deterministic patcher; sweep->patch->resweep.");
        Console.WriteLine();

        IReadOnlyList<IReadOnlyList<GraderCase>> batches = fam.GenerateRounds(seed, rounds, perRound);

        // ARM A — KEYWORD ORACLE (deterministic; patched each round).
        var oracle = new PatchableKeywordOracle(fam.SeedPositive);
        var patcher = new DeterministicPatcher(oracle, fam.KnownTokens);
        IReadOnlyList<RoundResult> keyword = await NonConvergenceLoop.RunAsync(
            batches, oracle, patcher.PatchFalseAlarm, patcher.PatchMiss, () => (oracle.PositiveRules, oracle.NegativeRules));

        // ARM B — the PRODUCTION composite for this family (Composite Judges; NOT patched).
        var compFails = new List<(string Kind, GraderCase Case)>();
        IProbeEvaluator composite = fam.BuildComposite(judge);
        IReadOnlyList<RoundResult> comp = await NonConvergenceLoop.RunAsync(batches, composite, null, null, null, compFails);

        PrintTrajectory("ARM A — KEYWORD ORACLE (patched each round)", keyword, withRules: true);
        PrintTrajectory($"ARM B — COMPOSITE JUDGES, production {fam.Name} grader (judge; NOT patched)", comp, withRules: false);

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

        await WriteResultsAsync(keyword, comp, rounds, perRound, seed, isReal, args);
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

    private static async Task WriteResultsAsync(IReadOnlyList<RoundResult> kw, IReadOnlyList<RoundResult> cp, int rounds, int perRound, int seed, bool isReal, string[] args)
    {
        string dir = ResolveOutDir(args);

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

    /// <summary>Multi-seed run for the pre-registered confirmatory analysis. Keyword arm (deterministic, free) across MANY
    /// seeds; composite arm (judge calls) across FEWER. Emits per-(arm,seed,round) fresh-fabrication counts to JSON for the
    /// pure-Python decay-fit + bootstrap (build/stats/confirmatory.py). Each seed = an independent corpus draw (the resampling unit).</summary>
    private static async Task<int> RunMultiSeedAsync(IChatClient judge, bool isReal, string[] args)
    {
        int rounds = IntArg(args, "--rounds", 8);
        int perRound = IntArg(args, "--per-round", 12);
        int kwSeeds = IntArg(args, "--seeds", 30);
        int compSeeds = IntArg(args, "--comp-seeds", 5);
        int seedStart = IntArg(args, "--seed-start", 2000);
        Family fam = Families.Get(StrArg(args, "--family", "inference"));

        Console.WriteLine($"Multi-seed [{fam.Name}]: keyword {kwSeeds} seeds (deterministic) + composite {compSeeds} seeds; {rounds} rounds x {perRound} fresh cases.");
        Console.WriteLine();

        var keyword = new List<(int Seed, int[] Fresh)>();
        for (int s = 0; s < kwSeeds; s++)
        {
            int seed = seedStart + s;
            IReadOnlyList<IReadOnlyList<GraderCase>> batches = fam.GenerateRounds(seed, rounds, perRound);
            var oracle = new PatchableKeywordOracle(fam.SeedPositive);
            var patcher = new DeterministicPatcher(oracle, fam.KnownTokens);
            IReadOnlyList<RoundResult> traj = await NonConvergenceLoop.RunAsync(batches, oracle, patcher.PatchFalseAlarm, patcher.PatchMiss, null);
            keyword.Add((seed, traj.Select(r => r.FreshFabrications).ToArray()));
        }
        Console.WriteLine($"  keyword: {kwSeeds} seeds done (deterministic).");

        var composite = new List<(int Seed, int[] Fresh)>();
        for (int s = 0; s < compSeeds; s++)
        {
            int seed = seedStart + s;
            IReadOnlyList<IReadOnlyList<GraderCase>> batches = fam.GenerateRounds(seed, rounds, perRound);
            IProbeEvaluator tree = fam.BuildComposite(judge);
            IReadOnlyList<RoundResult> traj = await NonConvergenceLoop.RunAsync(batches, tree, null, null, null);
            composite.Add((seed, traj.Select(r => r.FreshFabrications).ToArray()));
            Console.WriteLine($"  composite seed {seed}: [{string.Join(",", traj.Select(r => r.FreshFabrications))}]");
        }

        string dir = ResolveOutDir(args);
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"config\": {{ \"rounds\": {rounds}, \"perRound\": {perRound}, \"keywordSeeds\": {kwSeeds}, \"compositeSeeds\": {compSeeds}, \"seedStart\": {seedStart}, \"isReal\": {isReal.ToString().ToLowerInvariant()}, \"family\": \"{fam.Name}\", \"composite\": \"production composite\" }},");
        sb.AppendLine("  \"keyword\": [");
        sb.AppendLine(string.Join(",\n", keyword.Select(k => $"    {{ \"seed\": {k.Seed}, \"fresh\": [{string.Join(",", k.Fresh)}] }}")));
        sb.AppendLine("  ],");
        sb.AppendLine("  \"composite\": [");
        sb.AppendLine(string.Join(",\n", composite.Select(c => $"    {{ \"seed\": {c.Seed}, \"fresh\": [{string.Join(",", c.Fresh)}] }}")));
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        string path = Path.Combine(dir, $"multiseed-{fam.Name.ToLowerInvariant()}.json");
        await File.WriteAllTextAsync(path, sb.ToString());
        Console.WriteLine($"\nWrote {path}  →  run your confirmatory-analysis script on this file.");
        judge.Dispose();
        return 0;
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Output dir for the result JSON: <c>--out &lt;dir&gt;</c>, else the <c>AGENTEVAL_NONCONV_OUT</c> env var,
    /// else the working directory. Never hard-codes an internal (gitignored) path in this public sample — set the env var
    /// locally to route results into the paper pipeline.</summary>
    private static string ResolveOutDir(string[] args)
    {
        var outDir = StrArg(args, "--out", "") is { Length: > 0 } a ? a : Environment.GetEnvironmentVariable("AGENTEVAL_NONCONV_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            try { Directory.CreateDirectory(outDir); return Path.GetFullPath(outDir); }
            catch { /* fall through to the working directory */ }
        }
        return Environment.CurrentDirectory;
    }

    private static string StrArg(string[] args, string name, string dflt)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return dflt;
    }

    private static int IntArg(string[] args, string name, int dflt)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out int v) && v > 0) return v;
        return dflt;
    }
}
