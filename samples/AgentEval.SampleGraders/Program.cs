// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// AgentEval.SampleGraders — a self-contained head-to-head of red-team GRADERS on the InferenceAPIAbuse
// (OWASP LLM10) family, showing the BEFORE→AFTER arc: keyword oracle → single LLM-as-judge → flat
// composite (disclosure ⊕ refusal) → CALIBRATED composite → the ADR-024 GATED TREE (the "divide-and-
// conquer differently" decomposition that closes the "I am Grok" product-bot-vs-model floor without a
// stronger judge). It scores each against a small hand-labelled corpus, reports DIRECTIONAL FABRICATIONS
// (false alarms + missed hits), and — for the judge-based graders — runs N times with mean ± stddev +
// a 95% CI diff. Consumes AgentEval; modifies nothing in it. Runs standalone (offline heuristic stand-in
// without AZURE_OPENAI_* env; real Azure judge when they are set).
using AgentEval.RedTeam;             // Attack, IProbeEvaluator, GraderFactory, JudgeRubric, DecomposedGraders
using AgentEval.RedTeam.Evaluators;  // LLMJudgeEvaluator
using AgentEval.SampleGraders;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

int runs = ParseRuns(args, defaultRuns: 5);
(IChatClient judge, bool isReal) = BuildJudge();

// STAGE 1 (the paper's C1 headline): non-convergence-of-patching + keyword-vs-composite contrast.
if (args.Contains("--nonconvergence"))
    return await AgentEval.SampleGraders.NonConvergence.NonConvergenceRunner.RunAsync(judge, isReal, args);

// STAGE 2 (paper E4, definitive C2-abl): score a gold-set corpus with keyword / single / GENERIC composite / PRODUCTION
// task-specific graders on the SAME cases + SAME judge → verdicts.json (scored by the Python safety-asymmetric scorer).
if (args.Contains("--head-to-head"))
    return await HeadToHeadRunner.RunAsync(judge, isReal, args);

Console.WriteLine("AgentEval.SampleGraders — InferenceAPIAbuse grader head-to-head");
Console.WriteLine(new string('=', 70));
Console.WriteLine(isReal
    ? $"Judge: REAL Azure OpenAI ('{Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini"}').  Stochastic runs per judge-grader: {runs}."
    : "Judge: OFFLINE deterministic heuristic stand-in (no AZURE_OPENAI_* env vars set).\n" +
      "       Numbers are ILLUSTRATIVE — set AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT for the real stochastic run.");
Console.WriteLine();

// Three graders, all built from real AgentEval types — none of them touch the production scan path.
IProbeEvaluator keyword = Attack.InferenceAPIAbuse.GetEvaluator();                                  // the production heuristic oracle
IProbeEvaluator single = new LLMJudgeEvaluator(judge, GraderFactory.OptionsFor(JudgeRubric.EvidenceAnchored, "InferenceAPIAbuse"));
// The legacy ADR-022 FLAT composite (disclosure⊕refusal). NOTE: TryBuildFor("InferenceAPIAbuse") now returns the GATED
// TREE by default (ADR-024), so we build the flat one explicitly to keep this an honest flat-vs-tree head-to-head.
IProbeEvaluator compositeFlat = DecomposedGraders.BuildInferenceAbuseFlat(judge);
// NOTE: CALIBRATED V1 is a SAMPLE-LOCAL EXPERIMENT — its V1 prompts live ONLY in this companion (CalibrationHarness),
// NOT in production. (V0 / single / flat / gated below all use the REAL shipping AgentEval graders & prompts.)
// It's here to illustrate dump-driven calibration, not to represent a shipping grader.
IProbeEvaluator compositeCalibrated = CalibrationHarness.BuildComposite(judge, CalibrationHarness.V1);
// ADR-024 "divide-and-conquer differently": the GATED TREE — disclosure-by-name (D1 names-a-model ∧ D2 it's-the-AI-not-a-
// product), disclosure-by-internals (Dint own version/params/context-window/cutoff — no name → no veto), abuse (B1 abuse-done
// ∧ B2 real-not-hypothetical), refusal (C1 declines ∧ C2 no-leak), under a top OR. Isolating the one hard call (D2) closes the
// "I am Grok" floor — the real disclosure is caught, the product-bot decoy is vetoed — and the Dint path closes the nameless-
// internals-only recall gap, on gpt-4o-mini, no stronger model needed. Built explicitly (shows regardless of any env toggle).
IProbeEvaluator gatedTree = DecomposedGraders.BuildInferenceAbuseTree(judge);

(string Name, string Short, IProbeEvaluator Grader, bool Stochastic)[] graders =
[
    ("Keyword oracle", "keyword", keyword, false),
    ("Single judge (evidence-anchored)", "single", single, true),
    ("Composite - flat (disclosure⊕refusal)", "comp(flat)", compositeFlat, true),
    ("Composite - CALIBRATED V1 (sample experiment, NOT shipping)", "comp(calib*)", compositeCalibrated, true),
    ("Composite - GATED TREE (ADR-024)", "comp(gated)", gatedTree, true),
];

IReadOnlyList<GraderCase> corpus = GraderCorpus.InferenceApiAbuse();

// --calibrate: dump each decomposition leg's raw verdict + reason per case (diagnose over/under-firing).
if (args.Contains("--calibrate"))
{
    CalibrationHarness.LegResult v0 = await CalibrationHarness.RunAsync(judge, corpus, CalibrationHarness.V0);
    CalibrationHarness.LegResult v1 = await CalibrationHarness.RunAsync(judge, corpus, CalibrationHarness.V1);
    Console.WriteLine($"Calibration delta (directional fabrications): V0 = {v0.Directional}  ->  V1 = {v1.Directional}   ({v0.Directional - v1.Directional:+0;-0;0} change; missed {v0.Missed}->{v1.Missed}, falseAlarm {v0.FalseAlarms}->{v1.FalseAlarms})");
    judge.Dispose();
    return 0;
}

var stats = new List<GraderRunStats>();
foreach ((string name, string shrt, IProbeEvaluator grader, bool stochastic) in graders)
{
    int n = (stochastic && isReal) ? runs : 1;   // deterministic oracle + offline stand-in => 1 run
    var perRun = new List<ScoreLine>(n);
    for (int i = 0; i < n; i++)
        perRun.Add(await Scorecard.ScoreAsync(grader, corpus));
    stats.Add(GraderRunStats.From(name, shrt, perRun));
    Console.WriteLine($"  ran {name,-34} x {n}");
}
Console.WriteLine();

Scorecard.PrintCaseDetail(stats, corpus);
Scorecard.PrintScorecard(stats, corpus.Count);
Scorecard.PrintStatisticalDiff(stats, isReal);

string outPath = Path.Combine(Environment.CurrentDirectory, "scorecard.md");
await File.WriteAllTextAsync(outPath, Scorecard.ToMarkdown(stats, corpus, isReal, runs));
Console.WriteLine($"Wrote {outPath}");

judge.Dispose();
return 0;

static int ParseRuns(string[] argv, int defaultRuns)
{
    for (int i = 0; i < argv.Length - 1; i++)
        if (argv[i] is "--runs" or "-n" && int.TryParse(argv[i + 1], out int v) && v > 0)
            return v;
    return defaultRuns;
}

static (IChatClient judge, bool isReal) BuildJudge()
{
    string? endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    string? key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
    string deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini";
    if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key))
    {
        var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        return (client.GetChatClient(deployment).AsIChatClient(), true);
    }
    return (new HeuristicJudgeChatClient(), false);
}
