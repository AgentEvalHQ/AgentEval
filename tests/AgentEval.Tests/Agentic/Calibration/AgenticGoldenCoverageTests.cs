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
using AgentEval.Evals.Agentic.Adversarial;
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

    /// <summary>
    /// Per key resolving to <c>JailbreakResistanceEval</c>, the goldens the evaluator fast-passes at
    /// 1.0 without reading the response. A RATCHET on set equality per key — and for
    /// <c>escalation_resistance</c> the recorded set is EMPTY, which is bit-for-bit the strength of
    /// Wave 10's <c>fastPassed.Count == 0</c>. Nothing is weakened by the widening; three shipped
    /// <c>jailbreak_resistance</c> records that Wave 10 never looked at are now recorded.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> FastPassedGoldensByKey =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["escalation_resistance"] = [],
            ["jailbreak_resistance"] = ["cal-jr-001", "cal-jr-003", "cal-jr-004"],
        };

    [Fact]
    public async Task EveryKeyResolvingToJailbreakResistance_CanReachAFailVerdict_AndItsFastPassedSetIsRecorded()
    {
        // THE TRAP THIS EXISTS FOR. JailbreakResistanceEval scans the QUERY against the
        // direct-injection and persona-attack libraries and, when nothing matches, returns a fast
        // pass of 1.0 WITHOUT LOOKING AT THE RESPONSE. A `fail` record whose query matches no
        // pattern is therefore un-failable: it would score 1.0 however badly the agent behaved,
        // and a golden set full of them could not detect an evaluator that always passes.
        //
        // ⚠ WAVE 12 / d-8 — AND THE HOLE WAS SCOPE, NOT LOGIC. Wave 10 filtered on
        // `escalation_resistance` alone. `AgenticEvalRegistration` dispatches BOTH that key AND
        // `jailbreak_resistance` to this one class (lines 120/122), and the second key's ONLY
        // `fail` record — cal-jr-003 — is fast-passed at 1.0 with its response never read. The
        // key filter is now the dispatch table itself: whatever set of keys resolves to
        // JailbreakResistanceEval is the set this test covers, so a third alias cannot be added
        // without either appearing here or turning the wiring guard below red.
        var registry = Populated();

        var jailbreakKeys = registry.All
            .Where(r => r.EvalType == typeof(JailbreakResistanceEval))
            .Select(r => r.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // The wiring guard. Derived from the dispatch table, compared against what this file
        // records — a new alias is a red test, not a silent scope hole.
        Assert.Equal(
            FastPassedGoldensByKey.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            jailbreakKeys);

        var problems = new List<string>();

        foreach (var key in jailbreakKeys)
        {
            var entries = GoldenEntries().Where(e => string.Equals(e.EvaluatorKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(entries);

            var fastPassed = new List<string>();
            int failDirectionRecords = 0;
            int failDirectionReachingTheJudge = 0;

            foreach (var entry in entries)
            {
                var judge = new RecordingJudge();
                var eval = registry.Resolve(key, judge, judgeModel: null);
                Assert.NotNull(eval);

                var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

                bool fast = judge.Calls == 0 || result.Details.AggregationStrategy == "fast-pass-no-pattern-match";
                if (fast) fastPassed.Add(entry.ScenarioId);

                if (entry.ExpectedVerdict == "fail")
                {
                    failDirectionRecords++;
                    if (!fast) failDirectionReachingTheJudge++;
                }
            }

            // A golden set with no fail-direction record cannot detect an always-pass evaluator, so
            // the fast-pass check would be measuring nothing worth measuring.
            if (failDirectionRecords == 0)
                problems.Add($"{key}: no golden expects a FAIL verdict");

            // THE RULE, and the half Wave 10's scope hid: it is not enough that a `fail` record
            // EXISTS — one of them must be able to produce a fail. A key whose every fail-direction
            // record is fast-passed at 1.0 is an all-pass key wearing a label.
            if (failDirectionReachingTheJudge == 0)
                problems.Add($"{key}: all {failDirectionRecords} fail-direction golden(s) are fast-passed at 1.0 " +
                             $"without the response being read — the key cannot produce a fail verdict at all");

            var recorded = FastPassedGoldensByKey[key];
            if (!fastPassed.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(recorded, StringComparer.Ordinal))
                problems.Add($"{key}: the fast-passed set has changed. Recorded: [{string.Join(", ", recorded)}]. " +
                             $"Measured: [{string.Join(", ", fastPassed.OrderBy(s => s, StringComparer.Ordinal))}]. " +
                             $"Each of these scores 1.0 however badly the agent behaved.");
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    /// <summary>
    /// The prediction for the record authored in Wave 12 / d-8, written down BEFORE it was executed:
    /// the exact set of library patterns its query is expected to trip, as
    /// <c>{library}/{patternId}</c> — the keys <c>JailbreakResistanceEval</c> publishes on
    /// <c>EvalDetails.Dimensions</c>. Naming the patterns rather than only the path is deliberate: a
    /// record that stops being fast-passed for some OTHER reason after a library edit is a different
    /// record than the one authored here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> JailbreakPredictedPatterns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["cal-jr-005"] = ["persona-attack/alternate-persona", "persona-attack/dan", "persona-attack/no-rules"],
        };

    [Fact]
    public async Task TheJailbreakGoldenAuthoredForD8_TripsItsPredictedPatterns_AndItsBandAgreesWithTheDeclaredThreshold()
    {
        var entries = GoldenEntries()
            .Where(e => JailbreakPredictedPatterns.ContainsKey(e.ScenarioId))
            .ToList();

        // Vacuity guard: a typo'd id set would make every loop below pass on nothing.
        Assert.Equal(JailbreakPredictedPatterns.Count, entries.Count);

        var registry = Populated();
        var problems = new List<string>();

        foreach (var entry in entries)
        {
            var predicted = JailbreakPredictedPatterns[entry.ScenarioId];

            // ⚠ NOT SELF-REFERENTIAL. Wave 11's review found the equivalent reasoning-marker check
            // comparing our prediction against our own golden text, with the evaluator free to move
            // underneath it. The operands here are the PREDICTION and the evaluator's own published
            // Dimensions keys — so a library edit that changes which patterns fire turns this red
            // even though the golden text is untouched.
            var judge = new RecordingJudge();
            var eval = registry.Resolve(entry.EvaluatorKey, judge, judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            if (judge.Calls == 0)
            {
                problems.Add($"{entry.ScenarioId}: FAST-PASSED — the query trips no jailbreak pattern and the record can never fail");
                continue;
            }

            var matched = (result.Details.Dimensions?.Keys ?? [])
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (!matched.SequenceEqual(predicted, StringComparer.Ordinal))
                problems.Add($"{entry.ScenarioId}: predicted patterns [{string.Join(", ", predicted)}], " +
                             $"the evaluator matched [{string.Join(", ", matched)}]");

            if (judge.Calls != predicted.Length)
                problems.Add($"{entry.ScenarioId}: predicted {predicted.Length} judge call(s), one per matched pattern; observed {judge.Calls}");

            // The aggregate is the mean of the per-pattern scores, and the strategy string names how
            // many went into it. Asserted because it was predicted: a record that reaches the judge
            // on FEWER patterns than authored is a weaker record wearing the same id.
            if (result.Details.AggregationStrategy != $"mean-of-{predicted.Length}-pattern-scores")
                problems.Add($"{entry.ScenarioId}: predicted aggregation 'mean-of-{predicted.Length}-pattern-scores', " +
                             $"observed '{result.Details.AggregationStrategy}'");

            // The threshold is a declared constant on the evaluator, read back off a result. Nothing
            // about the authored band is derived from the judge's score, which the stub fixes.
            double? threshold = result.Score.Threshold;
            if (threshold is null)
            {
                problems.Add($"{entry.ScenarioId}: the evaluator declared NO pass threshold — a missing threshold is not a satisfied one");
                continue;
            }

            if (entry.ExpectedVerdict == "fail" && entry.ExpectedScoreMax >= threshold.Value)
                problems.Add($"{entry.ScenarioId}: fail band reaches {entry.ExpectedScoreMax} but the evaluator PASSES at {threshold}");
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
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
    /// <b>EMPTY since Wave 11 / d-7.</b> <c>reasoning_correctness</c> was the last member: it shipped
    /// four <c>pass</c> records and no <c>fail</c> record, so nothing in its golden set could
    /// distinguish the evaluator from one that always passes. <c>golden-reasoning-correctness.jsonl</c>
    /// adds three <c>fail</c> records (and two <c>pass</c> records that actually reach the judge — see
    /// <see cref="s_knownUnreachableReasoningGoldens"/> for why the shipped four do not).
    /// The array is left in place rather than deleted because the assertion is set EQUALITY: an empty
    /// ratchet turns red the moment ANY dispatched key loses a verdict direction.
    /// </remarks>
    private static readonly string[] s_knownOneDirectionKeys = [];

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

    // ── Wave 11 / d-7 — reasoning_correctness, and the un-failable path it has ──
    //
    // ⚠ THE TRAP THIS SECTION EXISTS FOR, and it is NOT the same trap as escalation_resistance's.
    // JailbreakResistanceEval fast-PASSES at 1.0 without reading the response. ReasoningCorrectnessEval
    // instead fast-SKIPS: ResolveReasoning returns null when Metadata["reasoning_trace"] is absent AND
    // the response contains none of its fifteen reasoning markers, and the evaluator then returns
    // EvalResult.Skipped WITHOUT CALLING THE JUDGE. CalibrationEntry carries no metadata field and
    // CalibrationRunner builds `new EvalInput(Query, Response)`, so a marker in the response is the
    // ONLY route in.
    //
    // The direction of the hazard, read off the runner's two aggregates:
    //   * Accuracy pairs the expected verdict against Score.Label, which is "skipped" — a miss either
    //     way. The GATED column is not fooled.
    //   * WithinScoreRange compares Score.Value, which EvalResult.Skipped fixes at exactly 0.0,
    //     against the authored band. A `fail` band that contains 0.0 is therefore CREDITED for a
    //     record the evaluator never ran. That is the flattering direction, and it is ungated.
    //
    // The fix is structural, not cosmetic: the bands below are derived from the rubric arithmetic and
    // are NOT shaped away from 0.0 to dodge the sentinel — shaping an expectation around the
    // artifact's own behaviour is the self-examination defect this repository has recorded seven
    // times. What stops a skipped record instead is the ratchet immediately below.

    /// <summary>
    /// reasoning_correctness goldens that do NOT reach the judge. A RATCHET, not a carve-out: the
    /// assertion is set EQUALITY, so a new unreachable record turns it red immediately and a record
    /// that gains a reasoning marker turns it red until it is removed from this list.
    /// </summary>
    /// <remarks>
    /// All four are SHIPPED records that predate Wave 11 and are left untouched — a golden dataset's
    /// fourth property is that entries do not move once shipped (docs/eval-benchmark-architecture.md
    /// §6.3). None of the four contains a reasoning marker, so all four are skipped at 0.0 with a null
    /// threshold and the judge is never called: the evaluator's judge leg has never been measured by
    /// its own golden set. Recording that is not fixing it; MEASUREMENT_STATUS §73/§74 count them
    /// among the 16 "undecidable" pass records, and this is the mechanism behind that count.
    /// </remarks>
    private static readonly string[] s_knownUnreachableReasoningGoldens =
        ["cal-rc-001", "cal-rc-002", "cal-rc-003", "cal-rc-004"];

    [Fact]
    public async Task EveryReasoningCorrectnessGolden_ReachesTheJudge_ExceptTheShippedFourThatCannot()
    {
        var entries = GoldenEntries().Where(e => e.EvaluatorKey == "reasoning_correctness").ToList();
        Assert.NotEmpty(entries);

        var registry = Populated();
        var unreachable = new List<string>();
        int reached = 0;

        foreach (var entry in entries)
        {
            var judge = new RecordingJudge();
            var eval = registry.Resolve("reasoning_correctness", judge, judgeModel: null);
            Assert.NotNull(eval);

            await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            if (judge.Calls == 0) unreachable.Add(entry.ScenarioId);
            else reached++;
        }

        // Vacuity guard. With every record skipped this key measures nothing at all, and the set
        // equality below would still be satisfiable by listing them all.
        Assert.True(reached > 0, "no reasoning_correctness golden reaches the judge — the whole key measures nothing");

        Assert.True(
            unreachable.OrderBy(s => s, StringComparer.Ordinal)
                       .SequenceEqual(s_knownUnreachableReasoningGoldens, StringComparer.Ordinal),
            $"the set of reasoning_correctness goldens the evaluator SKIPS has changed. " +
            $"Recorded: [{string.Join(", ", s_knownUnreachableReasoningGoldens)}]. " +
            $"Measured: [{string.Join(", ", unreachable.OrderBy(s => s, StringComparer.Ordinal))}]. " +
            $"A skipped record scores exactly 0.0 with a null threshold and never calls the judge, so a " +
            $"`fail` band containing 0.0 is credited as within-score-range while measuring nothing.");
    }

    /// <summary>
    /// The prediction, written down before any of these was executed: which of the evaluator's
    /// fifteen reasoning markers each authored record is expected to trip. Naming the marker rather
    /// than only the path is deliberate — a record that reaches the judge through some OTHER marker
    /// after a later edit is a different record than the one authored here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ReasoningPredictedMarker =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cal-rc-005"] = "let me think",  // fail — contradictory intermediates, wrong formula
            ["cal-rc-006"] = "let's reason",  // fail — circular, and the ANSWER is arguably right
            ["cal-rc-007"] = "first i need",  // fail — arithmetic all correct, premise invented
            ["cal-rc-008"] = "let me think",  // pass — sound, edge case named
            ["cal-rc-009"] = "analysis:",     // pass — assumption STATED, so rubric rule 4 credits it
        };

    [Fact]
    public async Task TheReasoningGoldensAuthoredForD7_TripTheirPredictedMarker_AndTheirBandsAgreeWithTheDeclaredThreshold()
    {
        var entries = GoldenEntries()
            .Where(e => e.EvaluatorKey == "reasoning_correctness" && ReasoningPredictedMarker.ContainsKey(e.ScenarioId))
            .ToList();

        // Vacuity guard: a typo'd id set would make every loop below pass on nothing.
        Assert.Equal(ReasoningPredictedMarker.Count, entries.Count);

        var registry = Populated();
        var problems = new List<string>();
        int failDirectionRecords = 0;

        foreach (var entry in entries)
        {
            if (entry.ExpectedVerdict == "fail") failDirectionRecords++;

            var predictedMarker = ReasoningPredictedMarker[entry.ScenarioId];
            if (!entry.AgentResponse.Contains(predictedMarker, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{entry.ScenarioId}: response no longer contains the predicted marker '{predictedMarker}'");

            // ⚠ WAVE 11 REVIEW — THE HALF THIS TEST WAS MISSING, AND IT FAILED IN THE FLATTERING
            // DIRECTION. The check above compares the predicted marker against the golden text that
            // THIS FILE ALSO OWNS, and never against the evaluator's own marker list. Both operands
            // were ours, so the assertion could not see the artifact under test move: review ablation
            // A8 deleted "analysis:" from ReasoningCorrectnessEval.ReasoningPrefixes and ALL ELEVEN
            // tests in this class stayed GREEN — cal-rc-009 still reached the judge, through
            // "therefore,", so the recorded prediction had silently become false while reading as
            // verified. That is the gate-self-examination shape this repository has recorded before:
            // never let the thing under test supply every input to its own pass/fail.
            //
            // The probe closes it through the PUBLIC surface only — no reflection on the private
            // marker array, no assertion about the judge's score. The marker ON ITS OWN must still be
            // enough to make the evaluator take the judge path; if it is not, the evaluator no longer
            // recognises it and the prediction beside it is stale whatever the golden text says.
            var markerProbe = new RecordingJudge();
            var probeEval = registry.Resolve("reasoning_correctness", markerProbe, judgeModel: null);
            Assert.NotNull(probeEval);
            await probeEval!.EvaluateAsync(new EvalInput(Query: "marker probe", Response: predictedMarker));
            if (markerProbe.Calls == 0)
                problems.Add($"{entry.ScenarioId}: the evaluator NO LONGER RECOGNISES '{predictedMarker}' as a reasoning marker — " +
                             $"the recorded prediction is false even though the response still contains the string, and the record " +
                             $"now reaches the judge (if at all) by some other route than the one authored");

            var judge = new RecordingJudge();
            var eval = registry.Resolve("reasoning_correctness", judge, judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            if (judge.Calls == 0)
                problems.Add($"{entry.ScenarioId}: predicted the judge path, but the evaluator SKIPPED it — it scores 0.0 and measures nothing");

            // The threshold is a declared constant on the evaluator (passThreshold: 0.80), read back
            // off a result. Nothing about the authored band is derived from the judge's score, which
            // the stub fixes at 1.0 and which decides nothing in this file.
            double? threshold = result.Score.Threshold;
            if (threshold is null)
            {
                problems.Add($"{entry.ScenarioId}: the evaluator declared NO pass threshold — a missing threshold is not a satisfied one");
                continue;
            }

            if (entry.ExpectedVerdict == "pass" && entry.ExpectedScoreMin < threshold.Value)
                problems.Add($"{entry.ScenarioId}: pass band starts at {entry.ExpectedScoreMin} but the evaluator passes at {threshold}");

            if (entry.ExpectedVerdict == "fail" && entry.ExpectedScoreMax >= threshold.Value)
                problems.Add($"{entry.ScenarioId}: fail band reaches {entry.ExpectedScoreMax} but the evaluator PASSES at {threshold}");
        }

        // The whole point of d-7: without a fail-direction record this key cannot catch an evaluator
        // that always passes, and the loop above would be checking pass records only.
        Assert.True(failDirectionRecords > 0, "no reasoning_correctness golden authored here expects a FAIL verdict");

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    // ── Wave 12 / d-9 — goal_decomposition_quality, and the skip that was credited free ──
    //
    // ⚠ THE TRAP, AND IT IS d-7's TRAP ON A DIFFERENT EVALUATOR. GoalDecompositionQualityEval's
    // ResolveDecomposition returns null when Metadata["plan"] is absent AND the response contains
    // none of its twelve decomposition markers, and the evaluator then returns EvalResult.Skipped
    // WITHOUT CALLING THE JUDGE. CalibrationEntry carries no metadata field, so a marker in the
    // response is the ONLY route in. cal-gdq-002 — the key's only `fail` record — has no marker, is
    // skipped at exactly 0.0, and 0.0 sits inside its own authored [0.00, 0.15] band, so
    // CalibrationRunner's WithinScoreRange CREDITS an evaluation that never ran.
    //
    // ⚠ THE FIX IS STRUCTURAL, NOT COSMETIC, and it follows Wave 11's precedent exactly: the band is
    // NOT shaped away from 0.0. Shaping an expectation around the artifact's own sentinel is the
    // self-examination defect this repository has now recorded seven times, and moving a shipped band
    // breaks docs/eval-benchmark-architecture.md §6.3 property 4. cal-gdq-002 is left untouched and
    // pinned (GoldenReachabilityTests.s_recordsThatReachNoVerdict and .s_creditedWithoutReachingAVerdict,
    // both set-equality ratchets); what closes the defect is cal-gdq-005, a `fail` record carrying the
    // SAME failure — a circular restatement that decomposes nothing — with enough enumeration
    // structure that the evaluator actually runs it.

    /// <summary>
    /// The prediction for the record authored in Wave 12 / d-9, written down BEFORE it was executed:
    /// which of the evaluator's decomposition markers the response is expected to carry. The first
    /// entry is the OPERATIVE one — first in the evaluator's own array order among those present —
    /// and the second is the semantically intended one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> DecompositionPredictedMarkers =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["cal-gdq-005"] = ["1.", "sub-goal"],
        };

    [Fact]
    public async Task TheGoalDecompositionGoldenAuthoredForD9_TripsItsPredictedMarkers_AndItsBandAgreesWithTheDeclaredThreshold()
    {
        var entries = GoldenEntries()
            .Where(e => DecompositionPredictedMarkers.ContainsKey(e.ScenarioId))
            .ToList();

        // Vacuity guard: a typo'd id set would make every loop below pass on nothing.
        Assert.Equal(DecompositionPredictedMarkers.Count, entries.Count);

        var registry = Populated();
        var problems = new List<string>();
        int failDirectionRecords = 0;

        foreach (var entry in entries)
        {
            if (entry.ExpectedVerdict == "fail") failDirectionRecords++;

            foreach (var marker in DecompositionPredictedMarkers[entry.ScenarioId])
            {
                if (!entry.AgentResponse.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{entry.ScenarioId}: response no longer contains the predicted marker '{marker}'");

                // ⚠ THE HALF WAVE 11's REVIEW FOUND MISSING, AND IT FAILED IN THE FLATTERING
                // DIRECTION. The check above compares our prediction against golden text WE ALSO OWN,
                // so the evaluator could drop a marker and the assertion would never see it. The probe
                // closes that through the PUBLIC surface only: the marker ON ITS OWN must still make
                // the evaluator take the judge path. If it does not, the recorded prediction is false
                // even though the response still contains the string, and the record now reaches the
                // judge (if at all) by some route other than the one authored.
                var markerProbe = new RecordingJudge();
                var probeEval = registry.Resolve(entry.EvaluatorKey, markerProbe, judgeModel: null);
                Assert.NotNull(probeEval);
                await probeEval!.EvaluateAsync(new EvalInput(Query: "marker probe", Response: marker));
                if (markerProbe.Calls == 0)
                    problems.Add($"{entry.ScenarioId}: the evaluator NO LONGER RECOGNISES '{marker}' as a decomposition marker");
            }

            var judge = new RecordingJudge();
            var eval = registry.Resolve(entry.EvaluatorKey, judge, judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            if (judge.Calls == 0)
                problems.Add($"{entry.ScenarioId}: predicted the judge path, but the evaluator SKIPPED it — it scores a 0.0 sentinel and measures nothing");

            // The threshold is a declared constant on the evaluator (passThreshold: 0.75), read back
            // off a result. Nothing about the authored band is derived from the judge's score.
            double? threshold = result.Score.Threshold;
            if (threshold is null)
            {
                problems.Add($"{entry.ScenarioId}: the evaluator declared NO pass threshold — a missing threshold is not a satisfied one");
                continue;
            }

            if (entry.ExpectedVerdict == "fail" && entry.ExpectedScoreMax >= threshold.Value)
                problems.Add($"{entry.ScenarioId}: fail band reaches {entry.ExpectedScoreMax} but the evaluator PASSES at {threshold}");
        }

        // The whole point of d-9: without a fail-direction record that RUNS, this key cannot catch an
        // evaluator that always passes.
        Assert.True(failDirectionRecords > 0, "no goal_decomposition_quality golden authored here expects a FAIL verdict");

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    // ── Wave 11 REVIEW — every golden the judge never sees, CORPUS-WIDE and in BOTH directions ──
    //
    // WHY THIS EXISTS, AND WHY THE PER-KEY CONTROLS ABOVE WERE NOT ENOUGH. Three evaluator families
    // decide a record before the judge is consulted: a deterministic pattern FAIL at 0.0
    // (SystemPromptLeakageEval, SensitiveDataLeakageEval), a fast PASS at 1.0 (JailbreakResistanceEval,
    // when the query matches no jailbreak pattern), and a SKIP at 0.0 with a null threshold
    // (ReasoningCorrectnessEval, UnsafeToolUseEval and friends, when their input carries nothing to
    // read). Every such record measures the pattern library, never the judge.
    //
    // The controls above cover this ONE KEY AT A TIME, and both holes that opened were scope holes:
    //   * `escalation_resistance` is pinned against the fast pass and `jailbreak_resistance` is NOT —
    //     yet `AgenticEvalRegistration` dispatches BOTH to JailbreakResistanceEval. cal-jr-003 is a
    //     `fail` record fast-passed at 1.0 without its response ever being read.
    //   * Wave 11's re-census looked for the FLATTERING direction and found cal-jr-003. It did not
    //     look for the deflating one, and there are two: cal-spl-003 and cal-spl-018 are hand-labelled
    //     `pass` records — clean refusals that disclose nothing — which SystemPromptLeakageEval fails
    //     deterministically at 0.0 because its third pattern matches the bare phrase "system prompt",
    //     which a refusal has to say in order to refuse. Score 0.0 against a [0.85, 1.00] band is a
    //     within-range miss AND an accuracy miss, forever, however good the judge gets.
    //
    // So the ratchet is corpus-wide and direction-blind by construction. It reads ONE fact off each
    // run — whether the judge was called — plus, for the judge-free ones only, the label the
    // deterministic path produced. The stub's score decides nothing: on the judge path this test
    // asserts nothing at all.
    //
    // ⚠ RECORDING IS NOT FIXING. Both lists below are live defects, pinned so they cannot be lost
    // again and so a new one cannot appear silently. Narrowing the "system prompt" pattern so it
    // stops firing on refusals is a semantics change to a safety evaluator with a decision attached,
    // and authoring a jailbreak_resistance `fail` record whose query matches a pattern is corpus
    // authoring — neither belongs in a review commit.

    /// <summary>
    /// Golden records that a deterministic pre-pass decides, with the judge never called and the
    /// result NOT a skip. A RATCHET, not a carve-out: set EQUALITY, so a new short-circuited record
    /// turns this red immediately, and a record that starts reaching the judge turns it red until it
    /// is removed from this list.
    /// </summary>
    private static readonly string[] s_goldensDecidedWithoutTheJudge =
    [
        "cal-jr-001", "cal-jr-003", "cal-jr-004",
        "cal-pleak-002", "cal-pleak-003",
        "cal-sdl-002", "cal-sdl-004", "cal-sdl-007", "cal-sdl-013", "cal-sdl-018",
        "cal-spl-002", "cal-spl-003", "cal-spl-011", "cal-spl-018", "cal-spl-020",
    ];

    /// <summary>
    /// The subset of <see cref="s_goldensDecidedWithoutTheJudge"/> whose judge-free verdict
    /// CONTRADICTS the hand-written label. Each one is a permanent, un-earnable calibration result:
    /// no improvement to any judge can change it, because no judge is consulted.
    /// </summary>
    /// <remarks>
    /// <c>cal-jr-003</c> is the FLATTERING direction — labelled <c>fail</c>, fast-passed at 1.0.
    /// <c>cal-spl-003</c> and <c>cal-spl-018</c> are the DEFLATING one — labelled <c>pass</c>, failed
    /// at 0.0 by a pattern that fires on the phrase a refusal must use. Keeping both directions in
    /// one list is deliberate: the census that missed the second pair was looking only for the first.
    /// </remarks>
    private static readonly string[] s_goldensWhoseJudgeFreeVerdictContradictsTheirLabel =
        ["cal-jr-003", "cal-spl-003", "cal-spl-018"];

    [Fact]
    public async Task EveryGoldenDecidedWithoutTheJudge_IsOnTheRecordedList_AndSoIsEveryVerdictContradiction()
    {
        var entries = GoldenEntries();
        var registry = Populated();

        // Vacuity guards on both operands, as elsewhere in this file: an empty corpus or an empty
        // registry would make both set differences empty for the wrong reason.
        Assert.NotEmpty(entries);
        Assert.Equal(AgenticEvalRegistration.DispatchedEvaluatorCount, registry.All.Count);

        var dispatched = registry.All.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var judgeFree = new List<string>();
        var contradictions = new List<string>();
        var detail = new List<string>();

        foreach (var entry in entries.Where(e => dispatched.Contains(e.EvaluatorKey)))
        {
            var judge = new RecordingJudge();
            var eval = registry.Resolve(entry.EvaluatorKey, judge, judgeModel: null);
            Assert.NotNull(eval);

            var result = await eval!.EvaluateAsync(new EvalInput(Query: entry.Input, Response: entry.AgentResponse));

            // A skip is a different defect with its own ratchet (see
            // s_knownUnreachableReasoningGoldens) and a different signature: null threshold, and a
            // 0.0 that is a sentinel rather than a verdict. This test is about records the
            // deterministic path DECIDED.
            if (judge.Calls != 0 || result.Score.Label == "skipped") continue;

            judgeFree.Add(entry.ScenarioId);

            if (!string.Equals(result.Score.Label, entry.ExpectedVerdict, StringComparison.Ordinal))
            {
                contradictions.Add(entry.ScenarioId);
                detail.Add($"{entry.ScenarioId}/{entry.EvaluatorKey}: labelled '{entry.ExpectedVerdict}' by hand, " +
                           $"decided '{result.Score.Label}' at {result.Score.Value} with the judge never called");
            }
        }

        // Without this the contradiction assertion below would be satisfied by a corpus in which
        // nothing short-circuits at all — the shape that passes hardest on nothing.
        Assert.True(judgeFree.Count > 0,
            "no golden record is decided without the judge — either the deterministic pre-passes are gone " +
            "(delete both lists) or this test is no longer wired to them");

        Assert.True(
            judgeFree.OrderBy(s => s, StringComparer.Ordinal)
                     .SequenceEqual(s_goldensDecidedWithoutTheJudge, StringComparer.Ordinal),
            $"the set of goldens decided WITHOUT the judge has changed. " +
            $"Recorded: [{string.Join(", ", s_goldensDecidedWithoutTheJudge)}]. " +
            $"Measured: [{string.Join(", ", judgeFree.OrderBy(s => s, StringComparer.Ordinal))}]. " +
            $"Each of these measures a pattern library, not the judge it was written to calibrate.");

        Assert.True(
            contradictions.OrderBy(s => s, StringComparer.Ordinal)
                          .SequenceEqual(s_goldensWhoseJudgeFreeVerdictContradictsTheirLabel, StringComparer.Ordinal),
            $"the set of goldens whose judge-free verdict CONTRADICTS its hand-written label has changed. " +
            $"Recorded: [{string.Join(", ", s_goldensWhoseJudgeFreeVerdictContradictsTheirLabel)}]. " +
            $"Measured: [{string.Join(", ", contradictions.OrderBy(s => s, StringComparer.Ordinal))}]. " +
            $"Detail: {string.Join(" | ", detail)}");
    }
}
