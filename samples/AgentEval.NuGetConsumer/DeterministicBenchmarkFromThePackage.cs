// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Plan task 4.2 — the join, exercised from OUTSIDE, against the published package.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This file is the proof, and the proof is that it compiles.</b> Every type below —
/// <see cref="AtomicCodeEval"/>, <see cref="AdmittedCheck"/>, <see cref="ChanceFloor"/>,
/// <see cref="BenchmarkDefinition"/>, <see cref="BenchmarkArm"/>, <see cref="BenchmarkRunner"/>,
/// <see cref="BenchmarkScore"/> — is reached through <c>PackageReference Include="AgentEval"</c>,
/// with <b>no ProjectReference anywhere in this project</b>. Against 0.34.0-beta this file does not
/// build: `FloorAdmittedEval` is absent from that assembly, which is the falsifiable half of the
/// release's own acceptance.
/// </para>
/// <para>
/// AE-04 was a REACHABILITY defect — the library was fine and no path reached it — so a proof that
/// the fix landed has to be a consumer standing where the original consumers stood: outside the
/// repository, holding nothing but the package.
/// </para>
/// <para>
/// It spends nothing. There is no <c>IChatClient</c>, no agent and no network: the arm is a
/// <see cref="BenchmarkArm.From"/> closure over a lookup table, which is exactly the shape a
/// consumer with no <c>TestResult</c> uses.
/// </para>
/// </remarks>
public static class DeterministicBenchmarkFromThePackage
{
    /// <summary>The three cities the question could be about. The floor's pool, named once.</summary>
    private static readonly string[] Cities = ["Zurich", "Geneva", "Lugano"];

    /// <summary>What each case asks, and the city its answer must name.</summary>
    private static readonly (string Id, string Question, string Answer)[] Cases =
    [
        ("direct",     "What is the capital canton city of Zurich?", "Zurich"),
        ("indirect",   "Which Swiss city hosts the UN's European HQ?", "Geneva"),
        ("distractor", "I fly Zurich to Lugano. Name my destination.", "Lugano"),
    ];

    /// <summary>Runs the benchmark and prints what it measured.</summary>
    /// <param name="brokenArm">When true, the arm answers with the wrong city every time.</param>
    public static async Task RunAsync(bool brokenArm = false)
    {
        Console.WriteLine();
        Console.WriteLine("═══ Deterministic benchmark, from the PUBLISHED package ══════════════════════");
        Console.WriteLine("    No ProjectReference. No agent. No network. No spend.");
        Console.WriteLine();

        // ── 1 · The definition. Data only — no subject, no judge, no store. ─────────────────
        var definition = new BenchmarkDefinition(
            Key: "swiss-city-lookup",
            Version: "1.0.0",
            Cases: [.. Cases.Select(c => new TestCase { Id = c.Id, Name = c.Id, Input = c.Question })],
            Checks:
            [
                // The floor is derived from the POOL, before anything runs: an arm that understood
                // nothing picks one of the three cities.
                new AdmittedCheck(new NamesTheRightCityEval(), ChanceFloor.UniformChoice(Cities.Length)),
            ]);

        // ── 2 · An arm with no TestResult behind it. ────────────────────────────────────────
        var arm = BenchmarkArm.From(brokenArm ? "broken" : "live", (testCase, _) =>
        {
            var expected = Cases.Single(c => c.Id == testCase.Id).Answer;
            var answered = brokenArm
                ? Cities.First(c => !string.Equals(c, expected, StringComparison.Ordinal))
                : expected;

            return Task.FromResult(new EvalInput(
                Query: testCase.Input,
                Response: $"Your destination is {answered}.")
            {
                // The check reads its expectation off the CASE, never off the answer.
                GroundTruth = expected,
            });
        });

        // ── 3 · A real run directory, created through the library. ──────────────────────────
        var workspace = Path.Combine(Path.GetTempPath(), "agenteval-nuget-consumer");
        Directory.CreateDirectory(workspace);

        var store = new FileSystemOutputStore(workspace);
        await store.InitializeSolutionAsync("NuGetConsumer 4.2 proof").ConfigureAwait(false);

        var runner = new BenchmarkRunner(store, new SubjectIdentity(SubjectKind.Agent, "PackageConsumer"));
        var run = await runner.RunAsync(definition, arm).ConfigureAwait(false);

        // ── 4 · Score it. The census FIRST — a rate with no denominator is not a measurement. ─
        var key = definition.Checks[0].Eval.Key;
        var census = BenchmarkScore.Census([run]).Single(c => c.CheckKey == key).Census;
        var floor = BenchmarkScore.AgainstFloor([run]).Single(c => c.CheckKey == key).Comparison;

        Console.WriteLine($"  arm         {run.ArmId}");
        Console.WriteLine($"  census      measured {census.Measured}, n/a {census.NotApplicable}, "
                        + $"not measured {census.NotMeasured} (of {census.Total})");
        Console.WriteLine($"  vs chance   {floor.Successes}/{floor.Trials} above a floor of "
                        + $"{floor.FloorUsed:0.000}, p = {Render(floor.PValue)}, above = {floor.AboveFloor}");

        if (floor.UnderpoweredByConstruction)
        {
            Console.WriteLine($"              ⚠ UNDERPOWERED BY CONSTRUCTION — the minimum attainable p at "
                            + $"n = {floor.Trials} is {floor.MinimumAttainableP:0.000}, so NO result could have "
                            + "reached α. A property of the corpus, not of the arm.");
        }

        Console.WriteLine();
        Console.WriteLine($"  run written under {workspace}");
        Console.WriteLine("  Every type used here came from the AgentEval NuGet package — this project has");
        Console.WriteLine("  no ProjectReference. Against 0.34.0-beta it would not compile.");
    }

    private static string Render(double p) => double.IsNaN(p) ? "n/a (undecidable)" : p.ToString("0.0000");
}

/// <summary>Does the answer name the city the case is about?</summary>
/// <remarks>
/// A deterministic check with no judge. It reads its expectation from
/// <see cref="EvalInput.GroundTruth"/> — the CASE — never from the response, so it cannot excuse
/// itself on exactly the answers it would have failed.
/// </remarks>
public sealed class NamesTheRightCityEval()
    : AtomicCodeEval("consumer.names_the_right_city", "Names the right city", "lookup", "1.0.0")
{
    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.GroundTruth))
        {
            const string NoGold = "this case declares no expected city, so there is nothing to check against.";
            return NotApplicable(NoGold, new EvalEvidence("case", "ground-truth", NoGold));
        }

        if (string.IsNullOrWhiteSpace(input.Response))
        {
            // ⚠ Undecidable, not a fail: an empty response and an uncaptured one are the same
            // string here, and scoring it would turn a blindness into a measurement.
            const string NoAnswer = "the response is empty, which is indistinguishable from one that was never captured.";
            return NotApplicable(NoAnswer, new EvalEvidence("response", "empty", NoAnswer));
        }

        var hit = input.Response.Contains(input.GroundTruth, StringComparison.OrdinalIgnoreCase);
        var summary = hit
            ? $"the answer names '{input.GroundTruth}'."
            : $"the answer does not name '{input.GroundTruth}'.";

        var scored = Build(hit ? 1.0 : 0.0, hit, hit ? "none" : "medium",
            dimensions: null, evidence: [new EvalEvidence("response", "city", summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
