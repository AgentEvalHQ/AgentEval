// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// ADR-021 (Phase B.2) "5b": the LIVE judge-agreement run. Drives a REAL LLM judge (Azure OpenAI from env) over the
// whole OracleHonestyCorpus and EMITS the agreement report (it asserts only that the run completed — the figure is
// measured, not asserted). Env-gated on AGENTEVAL_RUN_5B=1 so it never runs (or costs money) in the normal suite.
using AgentEval.Cli.Commands;        // AzureChatAgentFactory.TryBuildChatClientFromEnv
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;  // LLMJudgeEvaluator
using Xunit.Abstractions;

namespace AgentEval.Tests.RedTeam.Honesty;

public class JudgeAgreement5bLiveRun(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task Run_5b_LiveJudgeAgreement_OverCorpus()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_5B") != "1")
            return;   // inert unless explicitly opted in (live LLM cost)

        var (judgeClient, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judgeClient is not null, $"no judge client from env (exit {exit}); set AZURE_OPENAI_* env vars");

        var cases = OracleHonestyCorpus.All;
        var rubric = (Environment.GetEnvironmentVariable("AGENTEVAL_5B_RUBRIC") ?? "").Trim().ToLowerInvariant() switch
        {
            "lenient" => JudgeRubric.Lenient,
            "evidence-anchored" or "anchored" or "evidence" => JudgeRubric.EvidenceAnchored,
            _ => JudgeRubric.Strict,
        };
        // Per-case judge so the per-ORACLE evidence-anchored discriminator applies, mirroring production routing
        // (GraderFactory.For passes attack.Name == the corpus oracle). For strict/lenient the oracle arg is ignored,
        // so those rubrics are unaffected. LLMJudgeEvaluator is a thin wrapper, so building one per case is cheap.
        // Pre-fetch the judge verdict for every case with bounded concurrency, then feed the harness a lookup so the
        // measurement is fast and deterministic in ordering.
        var verdicts = new EvaluationOutcome[cases.Count];
        using (var gate = new SemaphoreSlim(8))
        {
            await Task.WhenAll(Enumerable.Range(0, cases.Count).Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    var judge = new LLMJudgeEvaluator(judgeClient!, GraderFactory.OptionsFor(rubric, cases[i].Oracle));
                    verdicts[i] = (await judge.EvaluateAsync(cases[i].Probe, cases[i].Response)).Outcome;
                }
                finally { gate.Release(); }
            }));
        }

        var indexOf = new Dictionary<OracleHonestyCase, int>();
        for (var i = 0; i < cases.Count; i++) indexOf[cases[i]] = i;

        var report = await JudgeAgreementHarness.MeasureAsync(
            cases, (c, _) => Task.FromResult(verdicts[indexOf[c]]));

        // Per-oracle directional-fabrication breakdown (the honesty-critical view).
        var byOracle = cases
            .Select((c, i) => (c, v: verdicts[i]))
            .GroupBy(t => t.c.Oracle)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var safeFab = g.Count(t => t.c.Expect == HonestyExpectation.SafeMustNotSucceed && t.v == EvaluationOutcome.Succeeded);
                var vulnFab = g.Count(t => t.c.Expect == HonestyExpectation.VulnerableMustNotResist && t.v == EvaluationOutcome.Resisted);
                return $"  {g.Key,-18} cases={g.Count(),-4} safe→succeeded={safeFab} vuln→resisted={vulnFab}";
            });

        var lines = new List<string>
        {
            "ADR-021 §8 — 5b live judge-agreement run",
            $"judge deployment: {deployment} | rubric: {rubric}",
            report.ToString(),
            $"verdict mix: succeeded={verdicts.Count(v => v == EvaluationOutcome.Succeeded)} " +
            $"resisted={verdicts.Count(v => v == EvaluationOutcome.Resisted)} " +
            $"inconclusive={verdicts.Count(v => v == EvaluationOutcome.Inconclusive)}",
            "per-oracle directional fabrications:",
        };
        lines.AddRange(byOracle);

        // AGENTEVAL_5B_DUMP=1: list the SPECIFIC directional-fabrication cases so a prompt/rubric fix can be designed
        // against the real failures (not guessed). Each line = direction + oracle + origin + a response snippet.
        if (Environment.GetEnvironmentVariable("AGENTEVAL_5B_DUMP") == "1")
        {
            var misses = cases
                .Select((c, i) => (c, v: verdicts[i]))
                .Where(t => (t.c.Expect == HonestyExpectation.SafeMustNotSucceed && t.v == EvaluationOutcome.Succeeded)
                         || (t.c.Expect == HonestyExpectation.VulnerableMustNotResist && t.v == EvaluationOutcome.Resisted))
                .OrderBy(t => t.c.Oracle)
                .Select(t =>
                {
                    var dir = t.c.Expect == HonestyExpectation.SafeMustNotSucceed ? "FALSE-ALARM (safe→succeeded)" : "MISSED-HIT (vuln→resisted)";
                    var snip = t.c.Response.Length <= 220 ? t.c.Response : t.c.Response[..220] + "…";
                    return $"  [{t.c.Oracle}] {dir} origin={t.c.Origin}\n      response: {snip}";
                });
            lines.Add("");
            lines.Add("directional-fabrication cases (AGENTEVAL_5B_DUMP):");
            lines.AddRange(misses);
        }

        var text = string.Join("\n", lines);

        _output.WriteLine(text);
        Console.WriteLine(text);
        var path = Path.Combine(Path.GetTempPath(), "agenteval-5b-agreement-report.txt");
        await File.WriteAllTextAsync(path, text);
        Console.WriteLine($"[5b] report written to {path}");

        Assert.True(report.Total > 0);   // measure, don't assert the figure
    }
}
