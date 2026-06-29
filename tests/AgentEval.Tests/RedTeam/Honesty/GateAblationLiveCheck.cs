// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// GATE-ABLATION A-B (ADR-024). Split-then-gate is NOT one-size-fits-all: it CLOSED the InferenceAbuse "I am Grok" floor
// but REGRESSED InsecureOutput, because the right composite-judge structure depends on the task (independent axes ⇒ gate
// helps; one coupled judgment ⇒ flat wins). This harness is how you DECIDE per oracle instead of generalizing: for each
// oracle that has BOTH a flat and a gated form, it grades that oracle's honesty corpus through EACH structure with the
// same live judge and reports DIRECTIONAL FABRICATIONS (safe→succeeded + vuln→resisted) for both, then recommends the
// structure with fewer and flags any mismatch with what currently ships. Adding an oracle's experiment = one registry
// line. Env-gated on AGENTEVAL_RUN_5B=1 (live LLM cost); informational — asserts only that the run completed (like 5b).
//
//   AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini AGENTEVAL_RUN_5B=1 AGENTEVAL_GATE_AB_REPS=3 \
//     dotnet test --filter "FullyQualifiedName~GateAblation_PerOracle_FlatVsGated"
//
// Use REPS≥3 for InferenceAbuse: its Grok-floor miss is nondeterministic (flips 0↔1), so a single rep can read 0 for the
// flat grader and hide the gain — averaging over reps recovers the true signal.
using AgentEval.Cli.Commands;        // AzureChatAgentFactory.TryBuildChatClientFromEnv
using AgentEval.RedTeam;
using Microsoft.Extensions.AI;       // IChatClient
using Xunit.Abstractions;

namespace AgentEval.Tests.RedTeam.Honesty;

public class GateAblationLiveCheck(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>One per oracle that has BOTH a flat and a gated composite. <c>Default</c> names the structure that
    /// currently SHIPS (<see cref="DecomposedGraders"/>), so the report can flag "the data disagrees with the default".</summary>
    private sealed record GateExperiment(
        string Oracle, Func<IChatClient, IProbeEvaluator> Flat, Func<IChatClient, IProbeEvaluator> Gated, string Default);

    // The reusable registry — extend this when a new oracle gets a gated variant.
    private static readonly GateExperiment[] Experiments =
    [
        new("InferenceAPIAbuse", DecomposedGraders.BuildInferenceAbuseFlat, DecomposedGraders.BuildInferenceAbuseTree, "gated"),
        new("InsecureOutput",    DecomposedGraders.BuildInsecureOutputFlat, DecomposedGraders.BuildInsecureOutputTree, "flat"),
    ];

    [Fact]
    public async Task GateAblation_PerOracle_FlatVsGated()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_5B") != "1")
            return;   // inert unless explicitly opted in (live LLM cost)

        var (judge, _, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit}); set AZURE_OPENAI_* env vars");

        int reps = int.TryParse(Environment.GetEnvironmentVariable("AGENTEVAL_GATE_AB_REPS"), out var r) && r > 0 ? r : 1;

        var lines = new List<string>
        {
            "",
            $"Gate-ablation A-B (flat composite vs gated tree) — directional fabrications, lower is better.  reps={reps}",
            "",
            $"  {"oracle",-18} {"cases",-6} {"flat (safe+vuln=tot)",-24} {"gated (safe+vuln=tot)",-24} verdict",
            "  " + new string('-', 104),
        };

        foreach (var exp in Experiments)
        {
            var cases = OracleHonestyCorpus.All.Where(c => c.Oracle == exp.Oracle).ToList();
            var flat = await CountFabsAsync(exp.Flat(judge!), cases, reps);
            var gated = await CountFabsAsync(exp.Gated(judge!), cases, reps);

            double delta = gated.Total - flat.Total;                 // <0 ⇒ gate reduces fabs (helps)
            string recommend = flat.Total <= gated.Total ? "flat" : "gated";
            string movement = Math.Abs(delta) < 1e-9 ? "TIES"
                : delta < 0 ? $"gate HELPS ({delta:0.0})"
                : $"gate HURTS (+{delta:0.0})";
            string defaultFlag = recommend == exp.Default ? $"default '{exp.Default}' ✓"
                : $"⚠ default is '{exp.Default}' but data favors '{recommend}'";

            lines.Add($"  {exp.Oracle,-18} {cases.Count,-6} {Fmt(flat),-24} {Fmt(gated),-24} {movement}; {defaultFlag}");
        }

        lines.Add("");
        lines.Add("Reading: 'gate HELPS' ⇒ the conflated decision was independent axes (decompose); 'gate HURTS' ⇒ it was a");
        lines.Add("single coupled judgment (keep flat). This is the ADR-024 decision, measured — never promote a gate on intuition.");

        _output.WriteLine(string.Join("\n", lines));
        Assert.True(true);   // measured, not asserted (the figure is the deliverable, like the 5b run)
    }

    private static string Fmt((double Safe, double Vuln, double Total) f) => $"{f.Safe:0.0}+{f.Vuln:0.0}={f.Total:0.0}";

    /// <summary>Average directional fabrications for one grader over the oracle's cases across <paramref name="reps"/>
    /// runs (averaging absorbs the live judge's run-to-run nondeterminism). safe→succeeded = a fabricated compromise on a
    /// SAFE response; vuln→resisted = a missed real hit.</summary>
    private static async Task<(double Safe, double Vuln, double Total)> CountFabsAsync(
        IProbeEvaluator grader, IReadOnlyList<OracleHonestyCase> cases, int reps)
    {
        double safeSum = 0, vulnSum = 0;
        for (int k = 0; k < reps; k++)
        {
            var verdicts = new EvaluationOutcome[cases.Count];
            using (var gate = new SemaphoreSlim(8))
            {
                await Task.WhenAll(Enumerable.Range(0, cases.Count).Select(async i =>
                {
                    await gate.WaitAsync();
                    try { verdicts[i] = (await grader.EvaluateAsync(cases[i].Probe, cases[i].Response)).Outcome; }
                    finally { gate.Release(); }
                }));
            }
            for (int i = 0; i < cases.Count; i++)
            {
                if (cases[i].Expect == HonestyExpectation.SafeMustNotSucceed && verdicts[i] == EvaluationOutcome.Succeeded) safeSum++;
                else if (cases[i].Expect == HonestyExpectation.VulnerableMustNotResist && verdicts[i] == EvaluationOutcome.Resisted) vulnSum++;
            }
        }
        return (safeSum / reps, vulnSum / reps, (safeSum + vulnSum) / reps);
    }
}
