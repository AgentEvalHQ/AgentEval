// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Wave 10 / d-4 — the calibration golden corpus, checked against the dispatch table.
//
// MEASUREMENT_STATUS §70.1 found, and §72.10 re-derived, that three DISPATCHED evaluator keys
// (`prompt_leak`, `escalation_resistance`, `protected_material`) had no golden entry at all and
// so could never be calibrated: `bench agentic calibrate` resolved them and then never fed them
// anything. Nothing failed — the run exited 0 with the three keys silently absent, and the
// standing claim "40 dispatched evaluators calibrated" overstated by 3.
//
// The census that found it was a shell pipeline nobody runs. These tests are that pipeline,
// wired to CI, in both directions:
//
//   * every DISPATCHED key must have at least one golden entry (the defect above), and
//   * every GOLDEN key must be either dispatched or a declared Path A' carve-out (the reverse —
//     an entry for a key nothing resolves is dead weight the runner skips on stderr).
//
// ⚠ WHAT THESE TESTS DELIBERATELY DO NOT DO. They do not check that an evaluator AGREES with a
// golden record. That is what a paid calibration run is for, and letting the evaluator's own
// output decide whether the golden is right is precisely the self-examination defect this
// repository has recorded six times. What is asserted here is structural and model-free: that
// the record EXISTS, that its band is well formed, and — for the two evaluators with a
// deterministic pre-pass — that the record actually reaches the machinery it was written to
// exercise instead of being short-circuited.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Calibration;
using Xunit;

namespace AgentEval.Tests.Agentic.Calibration;

public class AgenticGoldenCoverageTests
{
    /// <summary>
    /// A judge that records whether it was called. Its SCORE decides nothing in this file — only
    /// the CALL COUNT is read, and that is a fact about which code path ran, not about the verdict.
    /// </summary>
    private sealed class RecordingJudge : IEvaluator
    {
        public int Calls { get; private set; }

        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationResult { OverallScore = 100, Summary = "recording-stub" });
        }
    }

    private static IReadOnlyList<CalibrationEntry> GoldenEntries()
    {
        var datasets = new CalibrationDatasetLoader()
            .LoadAllFromAssemblyAsync(typeof(AgenticGoldenCoverageTests).Assembly)
            .GetAwaiter().GetResult();

        return datasets.SelectMany(d => d.Entries).ToList();
    }

    private static EvalRegistry Populated()
    {
        var registry = new EvalRegistry();
        AgenticEvalRegistration.RegisterInto(registry);
        return registry;
    }

    // ── The census, both directions ──────────────────────────────────────────

    [Fact] // d-4. Before Wave 10 this failed on prompt_leak, escalation_resistance, protected_material.
    public void EveryDispatchedEvaluatorKey_HasAtLeastOneGoldenEntry()
    {
        var entries = GoldenEntries();
        var registry = Populated();

        // Vacuity guards on BOTH operands. An empty golden corpus or an empty registry would make
        // the set difference below empty for the wrong reason, and this is a subset assertion —
        // the shape that passes hardest on nothing.
        Assert.NotEmpty(entries);
        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);

        var goldenKeys = entries
            .Select(e => e.EvaluatorKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncalibratable = registry.All
            .Select(e => e.Key)
            .Where(k => !goldenKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncalibratable.Count == 0,
            $"{uncalibratable.Count} dispatched evaluator key(s) have NO golden entry and can therefore " +
            $"never be calibrated — a run resolves them and measures nothing: {string.Join(", ", uncalibratable)}");
    }

    [Fact] // The reverse direction: a golden for a key nothing dispatches is skipped on stderr.
    public void EveryGoldenKey_IsEitherDispatchedOrADeclaredCarveOut()
    {
        var entries = GoldenEntries();
        var registry = Populated();

        Assert.NotEmpty(entries);
        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);
        Assert.NotEmpty(BenchAgenticCalibrateCommand.s_carveOutKeys);

        var dispatched = registry.All.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphaned = entries
            .Select(e => e.EvaluatorKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !dispatched.Contains(k) && !BenchAgenticCalibrateCommand.s_carveOutKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            $"{orphaned.Count} golden key(s) are neither dispatched nor carved out: {string.Join(", ", orphaned)}");
    }

    [Fact] // Makes the subset assertion above the RIGHT one: a carved-out key must not also dispatch.
    public void NoCarvedOutKey_IsAlsoDispatched()
    {
        var registry = Populated();
        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);

        var both = registry.All
            .Select(e => e.Key)
            .Where(BenchAgenticCalibrateCommand.s_carveOutKeys.Contains)
            .ToList();

        Assert.Empty(both);
    }

    // ── Every entry's band is well formed ────────────────────────────────────

    [Fact]
    public void EveryGoldenEntry_HasAWellFormedVerdictAndBand()
    {
        var entries = GoldenEntries();
        Assert.NotEmpty(entries);

        var bad = new List<string>();
        foreach (var e in entries)
        {
            if (e.ExpectedVerdict is not ("pass" or "fail"))
                bad.Add($"{e.ScenarioId}/{e.EvaluatorKey}: verdict '{e.ExpectedVerdict}' is neither pass nor fail");
            if (!(e.ExpectedScoreMin >= 0.0 && e.ExpectedScoreMin <= e.ExpectedScoreMax && e.ExpectedScoreMax <= 1.0))
                bad.Add($"{e.ScenarioId}/{e.EvaluatorKey}: band [{e.ExpectedScoreMin}, {e.ExpectedScoreMax}] is not an ordered sub-range of [0,1]");
            if (string.IsNullOrWhiteSpace(e.Input) || string.IsNullOrWhiteSpace(e.AgentResponse) || string.IsNullOrWhiteSpace(e.Rationale))
                bad.Add($"{e.ScenarioId}/{e.EvaluatorKey}: input, response or rationale is blank");
        }

        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    // ── The two evaluators with a deterministic pre-pass ─────────────────────
    //
    // Both short-circuit before the judge, so a record can be structurally incapable of testing
    // what it was written for while looking perfectly well formed on disk. These two tests are
    // the only ones in this file that execute an evaluator, and they read ONE fact off the run:
    // which path it took. No verdict, no score, no band is decided by the evaluator here.

    /// <summary>
    /// The prediction, written down before any of these was executed. `deterministic` means the
    /// response trips one of SystemPromptLeakageEval's six leakage regexes and is failed at 0.0
    /// with the judge never consulted; `judge` means it does not, and the LLM criteria decide.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PromptLeakPredictedPath =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cal-pleak-001"] = "judge",         // clean refusal, discloses nothing
            ["cal-pleak-002"] = "deterministic", // verbatim preamble: `You are a ... assistant`
            ["cal-pleak-003"] = "deterministic", // `My instructions are ...`
            ["cal-pleak-004"] = "judge",         // PARAPHRASE leak — trips no pattern by design
            ["cal-pleak-005"] = "judge",         // admits a constraint exists, discloses no content
        };

    [Fact]
    public async Task PromptLeakGoldens_TakeThePredictedPath_AndTheDeterministicOnesLandInTheirBand()
    {
        var entries = GoldenEntries().Where(e => e.EvaluatorKey == "prompt_leak").ToList();

        // Vacuity guard: no entries would make every loop below pass.
        Assert.Equal(PromptLeakPredictedPath.Count, entries.Count);

        var registry = Populated();
        var problems = new List<string>();

        foreach (var entry in entries)
        {
            var judge = new RecordingJudge();
            var eval = registry.Resolve("prompt_leak", judge, judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            string actualPath = judge.Calls == 0 ? "deterministic" : "judge";
            string predicted = PromptLeakPredictedPath[entry.ScenarioId];
            if (actualPath != predicted)
                problems.Add($"{entry.ScenarioId}: predicted the {predicted} path, took the {actualPath} one");

            // Only the deterministic path is model-free, so only it can be checked against the
            // authored expectation here. The judge-path records are checked by a paid calibration
            // run and by nothing in this file.
            if (predicted == "deterministic" && actualPath == "deterministic")
            {
                if (result.Score.Label != entry.ExpectedVerdict)
                    problems.Add($"{entry.ScenarioId}: expected verdict '{entry.ExpectedVerdict}', deterministic path gave '{result.Score.Label}'");
                if (result.Score.Value < entry.ExpectedScoreMin || result.Score.Value > entry.ExpectedScoreMax)
                    problems.Add($"{entry.ScenarioId}: score {result.Score.Value} is outside the authored band [{entry.ExpectedScoreMin}, {entry.ExpectedScoreMax}]");
            }
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [Fact]
    public async Task EveryEscalationResistanceGolden_MatchesAPattern_SoNoneCanBeFastPassed()
    {
        // THE TRAP THIS EXISTS FOR. JailbreakResistanceEval scans the QUERY against the
        // direct-injection and persona-attack libraries and, when nothing matches, returns a fast
        // pass of 1.0 WITHOUT LOOKING AT THE RESPONSE. A `fail` record whose query matches no
        // pattern is therefore un-failable: it would score 1.0 however badly the agent behaved,
        // and a golden set full of them could not detect an evaluator that always passes.
        var entries = GoldenEntries().Where(e => e.EvaluatorKey == "escalation_resistance").ToList();

        Assert.NotEmpty(entries);

        var registry = Populated();
        var fastPassed = new List<string>();
        int failDirectionRecords = 0;

        foreach (var entry in entries)
        {
            if (entry.ExpectedVerdict == "fail") failDirectionRecords++;

            var judge = new RecordingJudge();
            var eval = registry.Resolve("escalation_resistance", judge, judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            if (judge.Calls == 0 || result.Details.AggregationStrategy == "fast-pass-no-pattern-match")
                fastPassed.Add(entry.ScenarioId);
        }

        // A golden set with no fail-direction record cannot detect an always-pass evaluator, so
        // the fast-pass check above would be measuring nothing worth measuring.
        Assert.True(failDirectionRecords > 0, "no escalation_resistance golden expects a FAIL verdict");

        Assert.True(
            fastPassed.Count == 0,
            $"{fastPassed.Count} escalation_resistance golden(s) match NO jailbreak pattern and are fast-passed " +
            $"at 1.0 regardless of the response — they can never fail: {string.Join(", ", fastPassed)}");
    }

    // ── Band-vs-threshold self-consistency, for the three keys Wave 10 authored ──

    [Fact]
    public async Task TheThreeKeysAuthoredForD4_HavePassBandsThatCannotDisagreeWithTheirOwnVerdict()
    {
        // A `pass` record whose band dips BELOW the evaluator's pass threshold can be simultaneously
        // "within score range" and labelled fail by the same result — the two halves of the
        // calibration report then disagree about one entry. This is asserted only for the keys
        // authored here; the shipped corpus predates the rule and is not touched by it.
        string[] keys = ["prompt_leak", "escalation_resistance", "protected_material"];
        var entries = GoldenEntries().Where(e => keys.Contains(e.EvaluatorKey, StringComparer.OrdinalIgnoreCase)).ToList();

        Assert.Equal(15, entries.Count);

        var registry = Populated();
        var straddling = new List<string>();

        foreach (var entry in entries.Where(e => e.ExpectedVerdict == "pass"))
        {
            var eval = registry.Resolve(entry.EvaluatorKey, new RecordingJudge(), judgeModel: null);
            Assert.NotNull(eval);

            // The threshold is a declared constant on the evaluator, read back off a result. It is
            // not a verdict, and nothing about the authored band is derived from the judge here.
            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            // A missing threshold is not a satisfied one: an evaluator that declares no bar cannot
            // be shown to agree with a band, so the record is reported rather than waved through.
            double? threshold = result.Score.Threshold;
            if (threshold is null)
            {
                straddling.Add($"{entry.ScenarioId}/{entry.EvaluatorKey}: the evaluator declared NO pass threshold, so the band cannot be checked against one");
                continue;
            }

            if (entry.ExpectedScoreMin < threshold.Value)
                straddling.Add($"{entry.ScenarioId}/{entry.EvaluatorKey}: pass band starts at {entry.ExpectedScoreMin} but the evaluator passes at {threshold}");
        }

        Assert.True(straddling.Count == 0, string.Join(" | ", straddling));
    }

    [Fact]
    public async Task TheThreeKeysAuthoredForD4_HaveFailBandsThatCannotDisagreeWithTheirOwnVerdict()
    {
        // ⚠ THE MIRROR OF THE TEST ABOVE, AND THE HALF WAVE 10 LEFT OPEN. The pass rule was
        // enforced and the fail rule was not, so a `fail` record whose band REACHES ABOVE the
        // evaluator's pass threshold was accepted: a judge score of 0.93 against a band of
        // [0.00, 0.95] is "within score range" — the entry is credited — while the same result is
        // labelled `pass` against an entry that says `fail`. Wave 11 re-executed the gap: raising
        // cal-pm-002's fail band from 0.20 to 0.95 left all seven tests GREEN.
        //
        // Scope matches the test above deliberately: corpus-wide, 0 of 123 shipped fail records
        // straddle, but 9 of them are on evaluators that declare NO threshold at all and would be
        // reported here rather than waved through. That census is MEASUREMENT_STATUS §73, not a
        // gate on a corpus that predates the rule.
        string[] keys = ["prompt_leak", "escalation_resistance", "protected_material"];
        var entries = GoldenEntries().Where(e => keys.Contains(e.EvaluatorKey, StringComparer.OrdinalIgnoreCase)).ToList();

        Assert.Equal(15, entries.Count);

        var registry = Populated();
        var straddling = new List<string>();
        int checkedRecords = 0;

        foreach (var entry in entries.Where(e => e.ExpectedVerdict == "fail"))
        {
            checkedRecords++;

            var eval = registry.Resolve(entry.EvaluatorKey, new RecordingJudge(), judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            // Same rule as above: a missing threshold is not a satisfied one.
            double? threshold = result.Score.Threshold;
            if (threshold is null)
            {
                straddling.Add($"{entry.ScenarioId}/{entry.EvaluatorKey}: the evaluator declared NO pass threshold, so the band cannot be checked against one");
                continue;
            }

            if (entry.ExpectedScoreMax >= threshold.Value)
                straddling.Add($"{entry.ScenarioId}/{entry.EvaluatorKey}: fail band reaches {entry.ExpectedScoreMax} but the evaluator PASSES at {threshold} — a score inside this band is credited as within range and labelled pass at once");
        }

        // Vacuity guard: with no fail-direction record the loop above asserts nothing at all, and
        // the sibling test below is what stops that from happening silently.
        Assert.True(checkedRecords > 0, "no fail-direction record was checked — the loop asserted nothing");

        Assert.True(straddling.Count == 0, string.Join(" | ", straddling));
    }

    // ── Both directions, corpus-wide ─────────────────────────────────────────

    /// <summary>
    /// Dispatched keys whose golden set is known to carry only ONE verdict direction. This is a
    /// RATCHET, not a carve-out: the assertion is set EQUALITY, so removing the last one-direction
    /// key turns this red until the entry is deleted, and a new one turns it red immediately.
    /// </summary>
    /// <remarks>
    /// <c>reasoning_correctness</c> ships four <c>pass</c> records and no <c>fail</c> record, so
    /// nothing in its golden set can distinguish the evaluator from one that always passes.
    /// MEASUREMENT_STATUS §73 files it; it is not Wave 10's and is not fixed here, because
    /// authoring reasoning goldens is corpus work with its own review.
    /// </remarks>
    private static readonly string[] s_knownOneDirectionKeys = ["reasoning_correctness"];

    [Fact]
    public void EveryDispatchedKeyWithGoldens_CarriesBothVerdictDirections()
    {
        // THE RULE. A golden set of nothing but `pass` records cannot catch an evaluator that
        // always passes, and a set of nothing but `fail` records cannot catch one that always
        // fails. Wave 10 asserted this for escalation_resistance alone; Wave 11 re-executed the
        // gap by rewriting protected_material's three fail records as passes — five all-pass
        // records, and the whole suite stayed GREEN.
        var entries = GoldenEntries();
        var registry = Populated();

        Assert.NotEmpty(entries);
        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);

        var dispatched = registry.All.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var oneDirection = entries
            .Where(e => dispatched.Contains(e.EvaluatorKey))
            .GroupBy(e => e.EvaluatorKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => !g.Any(e => e.ExpectedVerdict == "pass") || !g.Any(e => e.ExpectedVerdict == "fail"))
            .Select(g => g.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Vacuity guard on the operand that could make the difference empty for the wrong reason.
        Assert.NotEmpty(entries.Where(e => dispatched.Contains(e.EvaluatorKey)));

        Assert.True(
            oneDirection.SequenceEqual(s_knownOneDirectionKeys, StringComparer.Ordinal),
            $"the set of dispatched keys whose goldens carry only ONE verdict direction has changed. " +
            $"Recorded: [{string.Join(", ", s_knownOneDirectionKeys)}]. Measured: [{string.Join(", ", oneDirection)}]. " +
            $"A key that GAINED its missing direction must be removed from s_knownOneDirectionKeys; a key that " +
            $"LOST one has a golden set that can no longer catch an evaluator which always answers that way.");
    }
}
