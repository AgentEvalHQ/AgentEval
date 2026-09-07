// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

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
/// Sample M1 — the smallest end-to-end run of AE-04's join: a <b>real MAF agent run</b> becomes an
/// <see cref="EvalInput"/>, an <see cref="IEval"/> is admitted through the door that will not open
/// without a chance floor, and the <see cref="EvalResult"/> comes back <b>carrying that floor</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>⏱️ Runtime: milliseconds. No credentials, no network, no spend.</b>
/// </para>
/// <para>
/// 🔴 <b>What is real here and what is scripted — say it once, plainly, because a fixture standing in
/// for the agent run would make this prove nothing.</b> Exactly ONE thing is scripted: the model's
/// token generation, supplied by the library's own <see cref="ScriptedChatClient"/>. Everything the
/// join actually depends on is the shipped code path and is executed for real:
/// </para>
/// <list type="bullet">
///   <item><description>a real <see cref="ChatClientAgent"/> from Microsoft Agent Framework, with a
///   real <see cref="AIFunctionFactory"/> tool on its <see cref="ChatOptions.Tools"/>;</description></item>
///   <item><description>MAF's own function-invocation loop, which calls the C# tool body for real —
///   <c>JoinRun.LookupCount</c> counts the invocations and the sample prints it, so "the tool ran" is
///   observed rather than assumed;</description></item>
///   <item><description><see cref="MAFAgentAdapter"/> and <see cref="MAFEvaluationHarness"/>, i.e.
///   the same entry point every other sample uses;</description></item>
///   <item><description>the real <c>ToolUsageExtractor</c>, reading the real
///   <c>AgentResponse.RawMessages</c> the run produced;</description></item>
///   <item><description><see cref="TestRunEvalProjection.ToEvalInput"/>,
///   <see cref="AgentEvalBuilder.AddEval"/>, <see cref="AgentEvalRunner.EvaluateEvalsAsync"/>, and
///   <see cref="EvalResultPersistence.ToScenarioResult"/> — the join itself, unmodified.</description></item>
/// </list>
/// <para>
/// A scripted model is not a scripted RUN. Nothing here hands the eval a hand-built
/// <see cref="TestResult"/>: the tool call the eval grades is one MAF genuinely dispatched.
/// </para>
/// <para>
/// <b>Why the floor is the point.</b> "The agent looked up the city it was asked about" sounds like a
/// pass at 1.000 until you ask what an agent that understands nothing scores. It picks one of the
/// <see cref="Cities"/> the tool knows, so its floor is 1/N — derived by
/// <see cref="ChanceFloor.UniformChoice"/>, at admission, from the roster, <b>before the agent runs</b>.
/// The eval cannot supply its own bar; <see cref="FloorAdmittedEval"/> throws if it tries.
/// </para>
/// </remarks>
public static class EvalWithChanceFloor
{
    /// <summary>The cities the lookup tool knows. Its SIZE is the eval's chance floor: an arm that guesses picks one of these.</summary>
    public static readonly IReadOnlyList<string> Cities =
        ["Zurich", "Geneva", "Basel", "Bern", "Lugano", "Lausanne", "Lucerne", "Winterthur"];

    /// <summary>The city this run asks about.</summary>
    public const string AskedCity = "Lugano";

    /// <summary>The tool name, frozen in one place.</summary>
    public const string ToolName = "lookup_weather";

    /// <summary>Everything the join produced, so the demo can print it and a test can assert on it.</summary>
    /// <param name="Case">The case that was run.</param>
    /// <param name="Result">What the real harness recorded.</param>
    /// <param name="Input">The projection's output — the stimulus the eval consumed.</param>
    /// <param name="Admitted">The admitted eval, carrying the floor it was admitted under.</param>
    /// <param name="Results">One result per admitted eval.</param>
    /// <param name="Persisted">The result read back through the library's own persistence reader.</param>
    /// <param name="LookupCount">How many times MAF really executed the tool body.</param>
    public sealed record JoinRun(
        TestCase Case,
        TestResult Result,
        EvalInput Input,
        FloorAdmittedEval Admitted,
        IReadOnlyList<EvalResult> Results,
        ScenarioResult Persisted,
        int LookupCount);

    /// <summary>Runs the join and returns everything it produced. Spends nothing.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The run.</returns>
    public static async Task<JoinRun> ExecuteAsync(CancellationToken ct = default)
    {
        // ── 1 · A real MAF agent. Only the MODEL is scripted. ────────────────────────────────
        int lookups = 0;
        var lookupWeather = AIFunctionFactory.Create(
            (string city) => { lookups++; return $"{city}: 14°C, light rain"; },
            ToolName,
            "Look up today's weather for one Swiss city.");

        // The scripted turns are what a provider would have sent: one tool call, then a final answer.
        var model = new ScriptedChatClient()
            .AddToolCall("call-1", ToolName, new Dictionary<string, object?> { ["city"] = AskedCity })
            .AddText($"It is 14°C with light rain in {AskedCity} today.");

        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "WeatherDesk",
            ChatOptions = new ChatOptions { Tools = [lookupWeather] },
        });

        // ── 2 · A real harness run. This is the RUN, not a fixture. ──────────────────────────
        var testCase = new TestCase
        {
            Id = "weather-lugano-1",
            Name = "Looks up the city it was asked about",
            Input = $"What is the weather in {AskedCity} today?",
            ExpectedTools = [ToolName],
            PassingScore = 0,
        };

        var harness = new MAFEvaluationHarness(verbose: false);
        TestResult result = await harness.RunEvaluationAsync(
            new MAFAgentAdapter(agent),
            testCase,
            new EvaluationOptions { TrackTools = true, EvaluateResponse = false, Verbose = false },
            ct).ConfigureAwait(false);

        // ── 3 · The join. (TestCase, TestResult) → EvalInput. ────────────────────────────────
        EvalInput input = testCase.ToEvalInput(result);

        // ── 4 · The door. It does not open without a floor, and the floor is derived HERE —
        //        before anything is scored, from the roster, never from the run's output. ─────
        ChanceFloor floor = ChanceFloor.UniformChoice(Cities.Count);

        AgentEvalRunner runner = await new AgentEvalBuilder()
            .AddEval(new AskedCityWasLookedUpEval(AskedCity), floor)
            .BuildAsync(ct)
            .ConfigureAwait(false);

        // ── 5 · The far end. Every result carries the floor its eval was admitted under. ─────
        IReadOnlyList<EvalResult> results = await runner.EvaluateEvalsAsync(input, ct).ConfigureAwait(false);

        // ── 6 · Read the floor back with the LIBRARY's own reader, not by re-reading our own
        //        dimension dictionary. If it does not survive persistence it did not arrive. ──
        ScenarioResult persisted = EvalResultPersistence.ToScenarioResult(
            results[0], testCase.Id!, testCase.Name, input: testCase.Input);

        return new JoinRun(testCase, result, input, runner.Evals[0], results, persisted, lookups);
    }

    /// <summary>Runs the sample and prints what each stage carried.</summary>
    public static async Task RunAsync()
    {
        PrintHeader();

        JoinRun run = await ExecuteAsync().ConfigureAwait(false);
        EvalResult scored = run.Results[0];
        RecordedChanceFloor? recorded = run.Persisted.Comparability?.ChanceFloor;

        Console.WriteLine("  1 · A REAL MAF agent run (only the model is scripted)");
        Console.WriteLine($"      question        : \"{run.Case.Input}\"");
        Console.WriteLine($"      tool body ran   : {run.LookupCount}× — MAF dispatched it, this sample did not");
        Console.WriteLine($"      answer          : \"{run.Result.ActualOutput}\"");
        Console.WriteLine($"      recorder saw    : {run.Result.ToolUsage?.Count.ToString() ?? "no report"} call(s)");
        Console.WriteLine();

        Console.WriteLine("  2 · The join — (TestCase, TestResult) → EvalInput");
        Console.WriteLine($"      CaseId          : {run.Input.CaseId ?? "(none declared)"}");
        Console.WriteLine($"      ToolCalls       : {Describe(run.Input.ToolCalls)}");
        foreach (var call in run.Input.ToolCalls ?? [])
            Console.WriteLine($"                        · {call.Name}({Args(call)}) → {call.Result}");
        Console.WriteLine();

        Console.WriteLine("  3 · The door — AddEval(eval, floor). There is no floorless overload.");
        Console.WriteLine($"      floor           : {run.Admitted.Floor.Kind} = {run.Admitted.Floor.ComparisonBar:F3}");
        Console.WriteLine($"      derivation      : {run.Admitted.Floor.Derivation}");
        Console.WriteLine();

        Console.WriteLine("  4 · The result, and the floor it came back carrying");
        Console.WriteLine($"      score           : {scored.Score.Value:F3}  ({scored.Score.Label})");
        Console.WriteLine($"      dimension       : {ComparabilityFacts.ChanceFloorDimension} = "
            + $"{(scored.Details.Dimensions?.TryGetValue(ComparabilityFacts.ChanceFloorDimension, out var d) == true ? d.ToString("F3") : "(none)")}");
        Console.WriteLine($"      read back as    : bar {recorded?.Bar?.ToString("F3") ?? "null"}, "
            + $"state {recorded?.State.ToString() ?? "no record at all"}, "
            + $"usable as a bar: {recorded?.IsUsableAsABar.ToString() ?? "n/a"}");
        Console.WriteLine();

        Console.ForegroundColor = scored.Score.Passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"      {(scored.Score.Passed ? "✅" : "❌")} {scored.Details.Summary}");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  Read the score AGAINST the floor, never on its own: an agent that understands");
        Console.WriteLine($"  nothing picks one of {Cities.Count} cities and scores {run.Admitted.Floor.ComparisonBar:F3} here. One case cannot");
        Console.WriteLine("  clear that bar on its own — clearing it is a claim about a COHORT, and this sample");
        Console.WriteLine("  runs one case. What it demonstrates is that the floor travels with the verdict.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string Describe(IReadOnlyList<ToolCall>? calls) => calls switch
    {
        null => "null — NO recorder ran, so a tool question is UNDECIDABLE (not zero)",
        { Count: 0 } => "[] — a recorder ran and saw nothing: a MEASURED zero",
        _ => $"{calls.Count} call(s), chronological",
    };

    private static string Args(ToolCall call) => call.Arguments is null
        ? ""
        : string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"));

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   M1 — an IEval over a REAL agent run, carrying its chance floor              ║
║   agent run → EvalInput → AddEval(eval, floor) → EvalResult (+ floor)         ║
║   offline · scripted model only · no credentials · no spend                   ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}

/// <summary>
/// The eval: did the agent look up the city it was ASKED about? Deterministic, code-only, no judge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three outcomes, kept apart, because two of them are not passes.</b> A <see langword="null"/>
/// <see cref="EvalInput.ToolCalls"/> means no recorder saw the run: the question is UNDECIDABLE and
/// the result is <see cref="EvalScore.NotApplicable"/>, never a zero and never a pass. An empty list
/// is a MEASURED zero — a recorder ran and the agent called nothing — which is a decidable FAIL.
/// Collapsing the two would turn missing evidence into a clean result.
/// </para>
/// <para>
/// 🔴 <b>This eval never mentions its own chance floor.</b> The bar is supplied at admission by
/// <see cref="AgentEvalBuilder.AddEval"/> and written onto the result afterwards. An eval that
/// emitted its own <c>chance_floor</c> would be the artifact under test supplying the bar it is
/// judged against — <see cref="FloorAdmittedEval"/> throws rather than merging it.
/// </para>
/// </remarks>
/// <param name="expectedCity">The city the question was about.</param>
public sealed class AskedCityWasLookedUpEval(string expectedCity)
    : AtomicCodeEval("asked_city_was_looked_up", "Asked city was looked up", "tool-use", "1.0.0")
{
    private readonly string _expectedCity = !string.IsNullOrWhiteSpace(expectedCity)
        ? expectedCity
        : throw new ArgumentException("An expected city is required.", nameof(expectedCity));

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ToolCalls is null)
        {
            return Undecidable(
                "no tool recorder ran, so nothing here can say which city was looked up. An absent "
                + "record is not an empty one and an empty one is not a pass.");
        }

        var lookups = input.ToolCalls
            .Where(c => string.Equals(c.Name, EvalWithChanceFloor.ToolName, StringComparison.OrdinalIgnoreCase))
            // A FAILED call is not a lookup. The projection guarantees this prefix leads on every
            // failure, so nothing the tool wrote can suppress it.
            .Where(c => c.Result is null
                     || !c.Result.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, StringComparison.Ordinal))
            .ToList();

        var cities = lookups
            .Select(c => c.Arguments is not null && c.Arguments.TryGetValue("city", out var v) ? v?.ToString()?.Trim() : null)
            .Where(city => !string.IsNullOrEmpty(city))
            .ToList();

        bool hit = cities.Any(city => string.Equals(city, _expectedCity, StringComparison.OrdinalIgnoreCase));

        string summary = hit
            ? $"the agent looked up '{_expectedCity}' ({lookups.Count} successful {EvalWithChanceFloor.ToolName} call(s))"
            : lookups.Count == 0
                ? $"a recorder ran and saw no successful {EvalWithChanceFloor.ToolName} call, so '{_expectedCity}' was never looked up"
                : $"the agent looked up {string.Join(", ", cities.Select(c => $"'{c}'"))} but never '{_expectedCity}'";

        EvalResult scored = Build(
            value: hit ? 1.0 : 0.0,
            passed: hit,
            severity: hit ? "none" : "medium",
            dimensions: null,
            evidence: [new EvalEvidence("tool-calls", EvalWithChanceFloor.ToolName, summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }

    private EvalResult Undecidable(string reason) => new(
        Metric: new(Key, Name, Category, Version),
        // ⚠ EvalScore.NotApplicable, not a 0.0 "fail". A score that is not a measurement can never
        //   be Passed, and the library guards that on the pair — see EvalScore's remarks.
        Score: EvalScore.NotApplicable(),
        Details: new(null, [new EvalEvidence("tool-calls", EvalWithChanceFloor.ToolName, reason)], [reason], null, null)
        {
            Summary = reason,
        },
        Provenance: new("atomic-code", null, null, null, null, 0, false),
        EvaluatedAt: DateTimeOffset.UtcNow);
}
