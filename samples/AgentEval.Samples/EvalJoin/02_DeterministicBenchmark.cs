// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples.EvalJoin;

/// <summary>
/// Sample M2 — M1's single admitted eval, re-expressed as a <see cref="BenchmarkDefinition"/> and run
/// as a real benchmark: three cases, two arms, two reps, into an isolated workspace.
/// </summary>
/// <remarks>
/// <para>
/// M1 proved the join for ONE case. This is the shape you use when the question is "does it do this
/// reliably, and is it better than the alternative?" — which needs cases, arms and reps, and needs
/// them kept apart. Nothing here is new machinery: the eval is
/// <see cref="AskedCityWasLookedUpEval"/>, <b>unchanged</b>, admitted through the same door with the
/// same floor.
/// </para>
/// <para>
/// <b>What the definition holds, and what it deliberately does not.</b> Cases and floored checks —
/// no subject, no judge, no store. The subject is bound in a <see cref="BenchmarkArm"/>, once, in a
/// typed field. That is why the same definition can be run against the scripted agent and against a
/// deliberately-broken one without either arm being mentioned in it.
/// </para>
/// <para>
/// <b>The broken arm IS the negative control.</b> There is no <c>INegativeControl</c> and no controls
/// slot on a definition (ADR-030 Q5, answered: defer the API, take the arm). A control is an arm you
/// built to be wrong, scored against the live one by <see cref="BenchmarkScore.AgainstReference"/>
/// with the case as the unit.
/// </para>
/// <para>
/// Offline and free: the only <see cref="IChatClient"/> is the in-process
/// <see cref="ScriptedChatClient"/>, so nothing is bought.
/// </para>
/// </remarks>
public static class DeterministicBenchmark
{
    /// <summary>The arm that looks up what it was asked about.</summary>
    public const string LiveArm = "scripted-live";

    /// <summary>The arm that looks up a plausible WRONG city — the negative control.</summary>
    public const string BrokenArm = "broken-wrong-city";

    /// <summary>How many times each arm runs the whole definition. Reps collapse per CASE, never pooled.</summary>
    public const int Reps = 2;

    /// <summary>Everything the benchmark produced, so the demo can print it and a test can assert on it.</summary>
    /// <param name="Definition">What was measured.</param>
    /// <param name="Live">Every rep of the live arm.</param>
    /// <param name="Broken">Every rep of the control arm.</param>
    /// <param name="Workspace">The isolated <c>.agenteval</c> root the runs were written into.</param>
    public sealed record BenchmarkOutcome(
        BenchmarkDefinition Definition,
        IReadOnlyList<BenchmarkRun> Live,
        IReadOnlyList<BenchmarkRun> Broken,
        string Workspace);

    /// <summary>
    /// One case: a question, the city it is really about, and the city a confused arm would pick.
    /// </summary>
    /// <param name="Id">Stable case identity — the unit of analysis, never the display name.</param>
    /// <param name="Question">What the user asked.</param>
    /// <param name="Target">The city the question is about.</param>
    /// <param name="Decoy">What the broken arm looks up instead. Always a real city from the roster.</param>
    private sealed record WeatherCase(string Id, string Question, string Target, string Decoy);

    private static readonly WeatherCase[] s_cases =
    [
        new("direct",
            $"What is the weather in {EvalWithChanceFloor.AskedCity} today?",
            EvalWithChanceFloor.AskedCity, "Zurich"),

        new("indirect",
            $"I land in {EvalWithChanceFloor.AskedCity} this evening — should I pack a coat?",
            EvalWithChanceFloor.AskedCity, "Bern"),

        // The case that separates the arms for a REASON rather than by luck: two cities are named and
        // only one is asked about. An arm that grabs the first city it sees looks up Zurich.
        new("distractor",
            $"I'm flying from Zurich to {EvalWithChanceFloor.AskedCity}. What's the weather at my destination?",
            EvalWithChanceFloor.AskedCity, "Zurich"),
    ];

    /// <summary>Runs the benchmark and returns everything it produced. Spends nothing.</summary>
    /// <param name="workspaceRoot">Where to write the runs. A temp directory in the demo.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static async Task<BenchmarkOutcome> ExecuteAsync(string workspaceRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        // ── 1 · The definition. DATA — no subject, no judge, no store. ───────────────────────
        //        The floor is derived from the ROSTER, before anything runs, exactly as in M1: an
        //        arm that guesses picks one of the eight cities the tool knows.
        var definition = new BenchmarkDefinition(
            Key: "weather-lookup",
            Version: "1.0.0",
            Cases: [.. s_cases.Select(c => new TestCase
            {
                Id = c.Id,
                Name = $"Looks up the asked city — {c.Id}",
                Input = c.Question,
                ExpectedTools = [EvalWithChanceFloor.ToolName],
                PassingScore = 0,
            })],
            Checks:
            [
                new AdmittedCheck(
                    new AskedCityWasLookedUpEval(EvalWithChanceFloor.AskedCity),
                    ChanceFloor.UniformChoice(EvalWithChanceFloor.Cities.Count)),
            ]);

        // ── 2 · Two arms over the same definition. The subject is bound HERE. ────────────────
        var live = Arm(LiveArm, c => c.Target);
        var broken = Arm(BrokenArm, c => c.Decoy);

        var store = new FileSystemOutputStore(workspaceRoot);

        // An isolated workspace, created through the library rather than by hand-writing
        // solution.json. Idempotent: running the sample twice into the same root is fine.
        await store.InitializeSolutionAsync("EvalJoin Benchmark Demo", ct).ConfigureAwait(false);

        var subject = new SubjectIdentity(SubjectKind.Agent, "WeatherDesk");
        var runner = new BenchmarkRunner(store, subject);

        // ── 3 · Reps are separate RUNS, never rows inside one. Two arms or two reps in a single
        //        run collide scenario ids and hide the arm from `compare`, which pairs DIRECTORIES.
        var liveRuns = new List<BenchmarkRun>(Reps);
        var brokenRuns = new List<BenchmarkRun>(Reps);
        for (int rep = 0; rep < Reps; rep++)
        {
            liveRuns.Add(await runner.RunAsync(definition, live, ct: ct).ConfigureAwait(false));
            brokenRuns.Add(await runner.RunAsync(definition, broken, ct: ct).ConfigureAwait(false));
        }

        return new BenchmarkOutcome(definition, liveRuns, brokenRuns, workspaceRoot);
    }

    /// <summary>
    /// An arm that runs a REAL MAF agent per case and projects the result through the real projection.
    /// </summary>
    /// <remarks>
    /// <see cref="BenchmarkArm.FromHarness"/> is the one-line shortcut when a single agent answers
    /// every case. Here each case needs its own scripted turn — the whole point is that the two arms
    /// look up different cities — so the harness call lives inside the closure. It is still a real
    /// <c>MAFEvaluationHarness</c> run and a real <c>ToEvalInput</c> projection; only the MODEL is
    /// scripted, which is what makes this free.
    /// </remarks>
    private static BenchmarkArm Arm(string armId, Func<WeatherCase, string> cityToLookUp) =>
        BenchmarkArm.From(armId, async (testCase, ct) =>
        {
            var weatherCase = s_cases.Single(c => string.Equals(c.Id, testCase.Id, StringComparison.Ordinal));
            var city = cityToLookUp(weatherCase);

            var lookupWeather = AIFunctionFactory.Create(
                (string city) => $"{city}: 14°C, light rain",
                EvalWithChanceFloor.ToolName,
                "Look up today's weather for one Swiss city.");

            var model = new ScriptedChatClient()
                .AddToolCall("call-1", EvalWithChanceFloor.ToolName, new Dictionary<string, object?> { ["city"] = city })
                .AddText($"It is 14°C with light rain in {city} today.");

            var agent = new ChatClientAgent(model, new ChatClientAgentOptions
            {
                Name = "WeatherDesk",
                ChatOptions = new ChatOptions { Tools = [lookupWeather] },
            });

            var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(
                new MAFAgentAdapter(agent),
                testCase,
                new EvaluationOptions { TrackTools = true, EvaluateResponse = false, Verbose = false },
                ct).ConfigureAwait(false);

            return testCase.ToEvalInput(result);
        });

    /// <summary>Runs the sample and prints what the benchmark measured.</summary>
    public static async Task RunAsync()
    {
        // A STABLE workspace, reused across invocations rather than a fresh GUID each time. Two
        // reasons, and they pull the same way: the `agenteval compare` command printed at the end has
        // to point at directories that still exist, and a sample run in a CI sweep must not leak a
        // new directory per invocation. InitializeSolutionAsync is idempotent, and runs accumulate
        // inside — which is what a run store is for.
        var workspace = Path.Combine(Path.GetTempPath(), "agenteval-samples", "eval-join-benchmark");
        Directory.CreateDirectory(workspace);

        Console.WriteLine();
        Console.WriteLine("M2 — one definition, two arms, two reps. Offline; nothing is bought.");
        Console.WriteLine();

        var outcome = await ExecuteAsync(workspace).ConfigureAwait(false);
        var checkKey = outcome.Definition.Checks[0].Eval.Key;

        Console.WriteLine($"  definition   {outcome.Definition.Key}@{outcome.Definition.Version} — "
                        + $"{outcome.Definition.Cases.Count} case(s) × {outcome.Definition.Checks.Count} check(s)");
        Console.WriteLine($"  arms         {LiveArm} and {BrokenArm}, {Reps} rep(s) each "
                        + $"⇒ {outcome.Live.Count + outcome.Broken.Count} run directories");
        Console.WriteLine();

        // ── The census FIRST. A rate with no denominator beside it is not a measurement. ──
        var census = BenchmarkScore.Census(outcome.Live).Single(c => c.CheckKey == checkKey).Census;
        Console.WriteLine($"  census       measured {census.Measured}, n/a {census.NotApplicable}, "
                        + $"not measured {census.NotMeasured} (of {census.Total} case(s))");

        // ── Against the floor. RepCollapse.All is "it does this EVERY time". ─────────────
        var floorRow = BenchmarkScore.AgainstFloor(outcome.Live, RepCollapse.All)
            .Single(c => c.CheckKey == checkKey).Comparison;

        Console.WriteLine($"  vs chance    {floorRow.Successes}/{floorRow.Trials} above a floor of "
                        + $"{floorRow.FloorUsed:0.000}, p = {Render(floorRow.PValue)}, "
                        + $"above = {floorRow.AboveFloor}");

        if (floorRow.UnderpoweredByConstruction)
        {
            Console.WriteLine($"               ⚠ UNDERPOWERED BY CONSTRUCTION — the minimum attainable p at this n "
                            + $"is {floorRow.MinimumAttainableP:0.000}, so no result could have reached α. "
                            + "That is a property of the DESIGN, not of the arm.");
        }

        // ── Against the control. The case is the unit; reps never pair. ──────────────────
        var paired = BenchmarkScore.AgainstReference(outcome.Broken, outcome.Live, RepCollapse.All)
            .Single(c => c.CheckKey == checkKey).Comparison;

        Console.WriteLine($"  vs control   {paired.Wins} win / {paired.Losses} loss / {paired.Ties} tie "
                        + $"over n = {paired.EffectiveN}; unit = {paired.Unit.Cases} case(s) from "
                        + $"{paired.Unit.TotalReps} rep-observation(s)");
        Console.WriteLine($"               ↑ n is the CASE count. Counting the {paired.Unit.TotalReps} reps as "
                        + "independent would inflate it by "
                        + $"×{paired.Unit.PseudoReplicationInflation:0.00} and narrow every interval.");

        // `agenteval compare` is a pure function of two run DIRECTORIES, so print the paths it
        // actually takes. A sample that prints a command which does not run is worse than one
        // that prints nothing.
        var layout = new FileSystemLayout(outcome.Workspace);
        var subjectRef = new SubjectIdentity(SubjectKind.Agent, "WeatherDesk");

        Console.WriteLine();
        Console.WriteLine($"  runs written under {outcome.Workspace}  (a stable temp workspace, reused each run)");
        Console.WriteLine("  compare rep 1 of each arm with:");
        Console.WriteLine($"    agenteval compare \\");
        Console.WriteLine($"      --baseline  \"{layout.RunDir(subjectRef, outcome.Broken[0].RunId)}\" \\");
        Console.WriteLine($"      --candidate \"{layout.RunDir(subjectRef, outcome.Live[0].RunId)}\"");
    }

    private static string Render(double p) => double.IsNaN(p) ? "n/a (undecidable)" : p.ToString("0.0000");
}
