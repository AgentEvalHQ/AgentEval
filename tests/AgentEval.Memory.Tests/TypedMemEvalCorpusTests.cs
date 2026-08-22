// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Structural checks over the five shipped TypedMemEval corpora, plus the integrity link between
/// each corpus and its metadata sidecar.
/// </summary>
/// <remarks>
/// These re-assert in CI what the generators asserted at authoring time. The duplication is the
/// point: a generator can be edited, and a corpus can be hand-patched, and neither event announces
/// itself. What ships is what gets checked.
/// </remarks>
public sealed class TypedMemEvalCorpusTests
{
    public static TheoryData<TypedMemEvalVertical> AllVerticals()
    {
        var data = new TheoryData<TypedMemEvalVertical>();
        foreach (var descriptor in TypedMemEvalVerticals.All)
            data.Add(descriptor.Vertical);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Corpus_HasTheDeclaredQuestionCountAndIdScheme(TypedMemEvalVertical vertical)
    {
        var descriptor = TypedMemEvalVerticals.For(vertical);
        var entries = TypedMemEvalCorpus.Load(vertical);

        Assert.Equal(descriptor.QuestionCount, entries.Count);
        Assert.Equal(
            entries.Count,
            entries.Select(e => e.QuestionId).Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in entries)
        {
            Assert.StartsWith($"tme-{descriptor.Abbreviation}-", entry.QuestionId, StringComparison.Ordinal);
            Assert.NotEmpty(entry.Question);
            Assert.NotEmpty(entry.Answer);
            Assert.NotNull(entry.HaystackSessions);
            Assert.NotEmpty(entry.HaystackSessions!);
        }
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Corpus_Sha256_DoesNotDependOnTheCheckoutsLineEndings(TypedMemEvalVertical vertical)
    {
        // The corpora are embedded from a git checkout and hashed into run provenance. A hash that
        // moved when a run migrated from a Windows machine to a Linux runner would report
        // "different corpus" for a corpus nobody touched.
        var json = TypedMemEvalCorpus.ReadJson(vertical);
        var lf = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        // DS197836 reads a "…Sha256" helper as hashing low-entropy content. What is hashed is a
        // multi-hundred-kilobyte corpus, and the hash identifies a dataset rather than protecting
        // a secret, so brute-force entropy is not the property at stake.
        var lfHash = TypedMemEvalCorpus.ComputeSha256(lf);       // DevSkim: ignore DS197836
        var crlfHash = TypedMemEvalCorpus.ComputeSha256(crlf);   // DevSkim: ignore DS197836
        var shipped = TypedMemEvalCorpus.Sha256(vertical);       // DevSkim: ignore DS197836

        Assert.NotEqual(lf, crlf);
        Assert.Equal(lfHash, crlfHash);
        Assert.Equal(lfHash, shipped);
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Metadata_DescribesTheCorpusItShipsWith(TypedMemEvalVertical vertical)
    {
        // The sidecar is deliberately outside the corpus hash, so re-running the reference-model
        // probes does not move it. That freedom needs a check, or a metadata file could quietly
        // describe a corpus that has since been regenerated.
        var metadata = Metadata(vertical);
        Assert.Equal(
            TypedMemEvalCorpus.Sha256(vertical),                 // DevSkim: ignore DS197836
            metadata.GetProperty("corpus_sha256").GetString());
        Assert.Equal(
            TypedMemEvalVerticals.For(vertical).CorpusId,
            metadata.GetProperty("corpus_id").GetString());
        Assert.Equal(
            TypedMemEvalVerticals.For(vertical).QuestionCount,
            metadata.GetProperty("question_count").GetInt32());
        Assert.Equal(
            TypedMemEvalCorpus.ReferenceBudgetSessions,
            metadata.GetProperty("k_ref").GetInt32());
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Metadata_RecordsACalibrationGateInsideItsBand(TypedMemEvalVertical vertical)
    {
        // The calibration gate is the family's only anti-saturation mechanism for the verticals
        // whose structural ceiling is 1.0, which is most of them. A corpus outside its band is
        // either unanswerable noise or saturated and blind to retrieval — the failure the family
        // exists to fix.
        var coverage = Metadata(vertical).GetProperty("coverage");
        var mean = coverage.GetProperty("mean_realised").GetDouble();
        var low = coverage.GetProperty("band_low").GetDouble();
        var high = coverage.GetProperty("band_high").GetDouble();

        Assert.InRange(mean, low, high);
        Assert.False(string.IsNullOrWhiteSpace(coverage.GetProperty("retriever").GetString()));
        Assert.True(coverage.GetProperty("iterations").GetInt32() >= 1);
        Assert.Equal(
            TypedMemEvalVerticals.For(vertical).QuestionCount,
            coverage.GetProperty("per_question").EnumerateObject().Count());
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Metadata_RecordsProbesAgainstTheCorpusItShipsWith(TypedMemEvalVertical vertical)
    {
        // V1, V2, V3 and V6 need a reference model, so they run at authoring time and their records
        // ship. Two things must hold for those records to mean anything: they must exist, and they
        // must describe *this* corpus. A probe record left behind by an earlier corpus would be a
        // validity claim for questions that no longer exist.
        var probes = Metadata(vertical).GetProperty("probes");
        var status = probes.GetProperty("status").GetString();

        Assert.Equal("run", status);
        Assert.Equal(
            TypedMemEvalCorpus.Sha256(vertical),                 // DevSkim: ignore DS197836
            probes.GetProperty("probed_corpus_sha256").GetString()); // DevSkim: ignore DS197836
        Assert.False(
            string.IsNullOrWhiteSpace(probes.GetProperty("reference_deployment").GetString()),
            "a probe record must name the deployment that produced it");

        foreach (var probe in new[]
                 {
                     "v1_oracle_answerability", "v2_non_inferability",
                     "v3_gold_ablated", "v6_leave_one_out"
                 })
        {
            var record = probes.GetProperty(probe);
            var applicable = record.GetProperty("applicable").GetInt32();
            var passed = record.GetProperty("passed").GetInt32();
            Assert.True(passed <= applicable, $"{probe}: more passes than applicable questions");
        }
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Metadata_StatesWhetherAnyStructuralCeilingIsBelowOne(TypedMemEvalVertical vertical)
    {
        // ADR §4 refuses to dress a 1.0 ceiling up as a band. Whether this corpus has structural
        // dispersion at all is recorded as a fact rather than implied by a table nobody reads.
        var ceiling = Metadata(vertical).GetProperty("ceiling");
        var declared = ceiling.GetProperty("structural_below_one").GetBoolean();
        var minimum = ceiling.GetProperty("min").GetDouble();

        Assert.Equal(minimum < 1.0, declared);

        // Only the two dispersion verticals can have one, because only they spread gold across
        // more than K_ref sessions. Everywhere else G is 1 or 2 by the mechanism under test.
        var expected = vertical is TypedMemEvalVertical.Arithmetic or TypedMemEvalVertical.Episodic;
        Assert.Equal(expected, declared);
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Corpus_LabelsGoldOnlyOnAnswerSessions(TypedMemEvalVertical vertical)
    {
        var corpusJson = TypedMemEvalCorpus.ReadJson(vertical);
        var extensions = TypedMemEvalExtensions.Parse(corpusJson);

        foreach (var entry in TypedMemEvalCorpus.Load(vertical))
        {
            var extension = extensions[entry.QuestionId];
            var sessions = entry.HaystackSessions!;
            var gold = extension.GoldSessionIndices.ToHashSet();

            for (var i = 0; i < sessions.Count; i++)
            {
                var hasAnswerTurn = sessions[i].Any(t => t.HasAnswer == true);
                if (!gold.Contains(i))
                {
                    Assert.False(
                        hasAnswerTurn,
                        $"{entry.QuestionId} s{i}: a non-gold session carries a has_answer turn, " +
                        $"which would make coverage count evidence the question does not depend on.");
                }
            }

            // A never-known probe has no gold by design: the correct answer is that the corpus
            // never contained it. Every other question must have evidence to retrieve.
            if (entry.QuestionId.EndsWith("_abs", StringComparison.Ordinal))
                Assert.Empty(extension.GoldSessionIndices);
            else
                Assert.NotEmpty(extension.GoldSessionIndices);
        }
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Corpus_DoesNotPinGoldToTheFirstSessionExceptWhereDeclared(
        TypedMemEvalVertical vertical)
    {
        var extensions = TypedMemEvalExtensions.Parse(TypedMemEvalCorpus.ReadJson(vertical));
        var withGold = extensions.Values.Where(e => e.GoldSessionIndices.Count > 0).ToArray();
        var firstPosition = withGold.Count(e => e.GoldSessionIndices[0] == 0);
        var share = (double)firstPosition / withGold.Length;

        if (vertical == TypedMemEvalVertical.WorkingMemory)
        {
            // Declared carve-out (ADR §5.4): the fact is stated in session 0 by design, which means
            // distance is deliberately confounded with absolute position and recency. That
            // composite IS the construct, and the ADR names it rather than hiding it.
            Assert.Equal(1.0, share);
        }
        else
        {
            Assert.True(
                share <= 0.5,
                $"{vertical}: gold sits first in {firstPosition}/{withGold.Length} questions, which " +
                $"would measure position rather than retrieval.");
        }
    }

    [Fact]
    public void ProspectivePairs_FlipGoldBetweenTheirArms()
    {
        // A pair whose arms share a gold answer has no signal: it would report a system as
        // consistent when the corpus never asked it anything different.
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Prospective));
        var entries = TypedMemEvalCorpus.Load(TypedMemEvalVertical.Prospective)
            .ToDictionary(e => e.QuestionId, StringComparer.Ordinal);

        var pairs = extensions
            .Where(kv => !string.IsNullOrEmpty(kv.Value.PairId))
            .GroupBy(kv => kv.Value.PairId!, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(19, pairs.Length);
        foreach (var pair in pairs)
        {
            var members = pair.ToArray();
            Assert.Equal(2, members.Length);

            var before = members.Single(m => m.Value.Arm == "before");
            var after = members.Single(m => m.Value.Arm == "after");
            var beforeEntry = entries[before.Key];
            var afterEntry = entries[after.Key];

            Assert.Equal(afterEntry.Question, beforeEntry.Question);
            Assert.NotEqual(afterEntry.Answer, beforeEntry.Answer);
            Assert.True(
                string.CompareOrdinal(beforeEntry.QuestionDate, afterEntry.QuestionDate) < 0,
                $"{pair.Key}: the before-arm is not queried earlier than the after-arm.");
        }
    }

    [Fact]
    public void ForgettingCorpus_HoldsTheThreeStatesApart()
    {
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Forgetting));

        var shapes = extensions.Values
            .GroupBy(e => e.Shape, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Assert.Equal(20, shapes["invalidated"]);
        Assert.Equal(15, shapes["still-valid"]);
        Assert.Equal(15, shapes["never-known"]);

        foreach (var (questionId, extension) in extensions)
        {
            switch (extension.Shape)
            {
                case "invalidated":
                    // Two labelled components, statement strictly before invalidation. The
                    // asymmetric-retrieval failure — statement surfaced, invalidation missed —
                    // is only diagnosable because these are labelled individually.
                    Assert.NotNull(extension.GoldComponents);
                    Assert.Equal(2, extension.GoldComponents!.Count);
                    var statement = extension.GoldComponents.Single(c => c.Kind == "statement");
                    var invalidation = extension.GoldComponents.Single(c => c.Kind == "invalidation");
                    Assert.True(
                        statement.SessionIndex < invalidation.SessionIndex,
                        $"{questionId}: the invalidation does not follow the statement.");
                    break;

                case "never-known":
                    // The _abs suffix is load-bearing: IsAbstention infers from it, so misusing it
                    // anywhere else in the family would silently mislabel composition.
                    Assert.EndsWith("_abs", questionId, StringComparison.Ordinal);
                    Assert.Empty(extension.GoldSessionIndices);
                    break;

                case "still-valid":
                    // Two gold sessions since v4 -- a statement and a re-affirmation -- so the
                    // control arm carries the same G as the invalidated arm it is paired with.
                    // With G=1 against G=2 the arms were not comparable: the treatment arm
                    // earned partial credit for finding either session while the control scored
                    // 0 or 1, and the control came out as the hardest retrieval band in the
                    // family, on the arm whose whole job is to be the easy case.
                    Assert.DoesNotContain("_abs", questionId, StringComparison.Ordinal);
                    Assert.Equal(2, extension.GoldSessionIndices.Count);
                    break;
            }
        }
    }

    [Theory]
    [InlineData(TypedMemEvalVertical.Prospective)]
    [InlineData(TypedMemEvalVertical.Arithmetic)]
    public void TimeDependentCorpora_StateNoAbsoluteDateInAnyMessage(TypedMemEvalVertical vertical)
    {
        // V4, and the property the time-dependent verticals rest on. A single printed date in a
        // conversation lets a system that stores no time at all answer from the text, which is
        // exactly the hole these corpora exist to close. The generators enforce it at authoring
        // time; this is the half that runs over what actually ships.
        foreach (var entry in TypedMemEvalCorpus.Load(vertical))
        {
            foreach (var turn in entry.HaystackSessions!.SelectMany(session => session))
            {
                Assert.False(
                    LongMemEvalTimestamps.LooksDated(turn.Content),
                    $"{entry.QuestionId}: message content carries an absolute date: {turn.Content}");
            }
        }
    }

    [Fact]
    public void ArithmeticDeltasAndDurations_RecomputeFromTheirOwnInputs()
    {
        // V5 for the two operations the sum/count check cannot cover. A duration's inputs each span
        // a pair of sessions, so this also pins the from/to pairing that the per-component coverage
        // breakdown is built from.
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Arithmetic));

        var checkedDurations = 0;
        var checkedDeltas = 0;
        foreach (var (questionId, extension) in extensions)
        {
            var derivation = extension.Derivation!;
            switch (derivation.Operation)
            {
                case "duration":
                    // Every input must name the session it started from, or half its gold is
                    // unaccounted for and the component list silently describes a shorter question.
                    Assert.All(
                        derivation.Inputs,
                        input => Assert.NotNull(input.FromSessionIndex));
                    Assert.True(
                        Math.Abs(derivation.Inputs.Sum(i => i.Value) - derivation.Value) < 0.005,
                        $"{questionId}: intervals sum to {derivation.Inputs.Sum(i => i.Value)}, " +
                        $"recorded {derivation.Value}");
                    checkedDurations++;
                    break;

                case "delta":
                    Assert.True(
                        derivation.Inputs.Count >= 2,
                        $"{questionId}: a difference needs at least two inputs");
                    checkedDeltas++;
                    break;
            }
        }

        // A check that silently covered nothing would be worse than no check.
        Assert.Equal(12, checkedDurations);
        Assert.Equal(10, checkedDeltas);
    }

    [Fact]
    public void CalibrationClause_IsNotAGoldTell()
    {
        // The scaffolding the calibration gate appends must not separate gold from distractors.
        // The first build of these corpora had it on distractors only — gold carried it 0 times in
        // 501 sessions — so `clause not present` isolated every piece of gold evidence in every
        // corpus with a one-line string filter. A benchmark whose evidence is separable without
        // reading it measures nothing.
        const string clause = "Also on my mind";

        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            var extensions = TypedMemEvalExtensions.Parse(
                TypedMemEvalCorpus.ReadJson(descriptor.Vertical));

            foreach (var entry in TypedMemEvalCorpus.Load(descriptor.Vertical))
            {
                var gold = extensions[entry.QuestionId].GoldSessionIndices.ToHashSet();
                var sessions = entry.HaystackSessions!;
                if (gold.Count == 0 || gold.Count == sessions.Count)
                    continue;

                double Rate(bool wantGold) => sessions
                    .Where((_, i) => gold.Contains(i) == wantGold)
                    .Average(s => s.Any(turn => turn.Content.Contains(clause, StringComparison.Ordinal)) ? 1.0 : 0.0);

                Assert.True(
                    Math.Abs(Rate(true) - Rate(false)) <= 0.5,
                    $"{entry.QuestionId}: the calibration clause separates gold from distractors " +
                    $"(gold {Rate(true):F2} vs distractors {Rate(false):F2}).");
            }
        }
    }

    [Fact]
    public void ArithmeticDerivations_RecomputeFromTheirOwnInputs()
    {
        // The recorded derivation is what lets the judge score the arithmetic rather than the
        // phrasing. If it did not recompute, the judge would be grading against a number the
        // corpus cannot justify.
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Arithmetic));

        foreach (var (questionId, extension) in extensions)
        {
            var derivation = extension.Derivation;
            Assert.NotNull(derivation);
            Assert.NotEmpty(derivation!.Inputs);
            Assert.NotEmpty(derivation.Unit);

            foreach (var input in derivation.Inputs)
            {
                Assert.Contains(
                    input.SessionIndex, extension.GoldSessionIndices);
            }

            if (derivation.Operation is "sum" or "count")
            {
                var expected = derivation.Operation == "count"
                    ? derivation.Inputs.Count
                    : derivation.Inputs.Sum(i => i.Value);
                Assert.True(
                    Math.Abs(expected - derivation.Value) < 0.005,
                    $"{questionId}: {derivation.Operation} of its inputs is {expected}, " +
                    $"but the recorded value is {derivation.Value}.");
            }
        }
    }

    [Fact]
    public void WorkingMemoryGrid_IsCompleteAndLabelled()
    {
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.WorkingMemory));

        var byDistance = extensions.Values
            .GroupBy(e => e.DistanceSessions)
            .ToDictionary(g => g.Key, g => g.Count());

        // Five rungs since v4. The old ladder had two that could not fail -- d=1 gives H=2 and
        // d=5 gives H=6, and BM25 at K_ref=5 realised 1.00 on both -- so half the vertical sat
        // in a structurally unfailable band and the ladder graded at three levels, not four.
        Assert.Equal(new[] { 8, 15, 25, 40, 60 }, byDistance.Keys.Select(k => k!.Value).Order().ToArray());
        Assert.All(byDistance.Values, count => Assert.Equal(12, count));
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void NoCheapFeatureSeparatesGoldFromDistractors(TypedMemEvalVertical vertical)
    {
        // V7. The clause-parity check that preceded this one was specific to one marker string, and
        // the next tell would not have been a clause: measured before this rule existed, gold was
        // findable by capitalisation density at AUC 0.99 in Forgetting and by length at 0.96 in
        // Episodic. This asserts the recorded measurement is present, describes THIS corpus, and
        // clears the bar on every feature that is not a declared carve-out.
        var probes = Metadata(vertical).GetProperty("probes");
        Assert.True(
            probes.TryGetProperty("v7_separability", out var v7),
            $"{vertical} carries no V7 record. Run tools/stamp_typedmemeval_separability.py.");

        Assert.Equal(
            TypedMemEvalCorpus.Sha256(vertical),                 // DevSkim: ignore DS197836
            v7.GetProperty("probed_corpus_sha256").GetString());

        // The bar and the feature list are C# constants, NOT read from the record. Taking either
        // from the artifact under test would let the artifact define its own passing grade: a
        // refused_features array trimmed to two entries, or a threshold_auc of 0.99, would sail
        // through. The record supplies only the corpus hash, which is the one thing it can be
        // trusted to state about itself.
        var exempt = vertical == TypedMemEvalVertical.WorkingMemory
            ? new HashSet<string>(StringComparer.Ordinal) { "position_in_haystack" }
            : [];

        // Re-measured from the corpus text, not read back from `features`. A stamped number that
        // nothing recomputes is a claim; this is the check. It is also deliberately a SECOND
        // implementation of the metric — the Python one certified a Forgetting corpus that a
        // one-line substring filter could pick apart, and the bug was in how it paired sessions.
        var measured = SeparabilityAucs(vertical, exempt);
        foreach (var (name, auc) in measured)
        {
            Assert.True(
                auc < SeparabilityMaxAuc,
                $"{vertical}: '{name}' separates gold from distractors at AUC {auc:F3}, at or above " +
                $"the {SeparabilityMaxAuc} refusal threshold — a classifier can find the evidence " +
                $"without reading it.");
        }

        // Every refused feature was actually measured; a silently missing one would pass by absence.
        Assert.Equal(
            RefusedFeatures.Except(exempt).OrderBy(n => n, StringComparer.Ordinal),
            measured.Keys.OrderBy(n => n, StringComparer.Ordinal));

        // WorkingMemory pins its fact to session 0 (ADR §5.4), so position separates gold perfectly
        // and is meant to. Equality, not containment: containment would let a future exemption be
        // added to the record and pass unnoticed.
        var recordedExempt = v7.GetProperty("exempt_features")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(exempt.OrderBy(n => n, StringComparer.Ordinal),
                     recordedExempt.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>The refusal bar, mirroring <c>SEPARABILITY_MAX_AUC</c> in typedmemeval_common.py.</summary>
    // A probe arm that returns nothing is not measuring the model, and both directions of that
    // mistake have already shipped. V8/V9 counted silence as a wrong answer, which understated a
    // ceiling. V3 and V6 pass when an ablated context FAILS to reproduce the answer, so silence
    // scores as a PASS and certifies validity the evidence does not support -- 258 of V3's 330
    // probe answers were empty. This gate exists so the class dies at authoring time rather than
    // being rediscovered forensically, which is how both instances were found.
    //
    // The rate is measured over PROBE ANSWERS. Judge grades are a separate population -- 0 of 1246
    // empty, i.e. healthy -- and pooling them into the denominator is what made this instrument
    // understate itself on its own first release.
    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Metadata_KeepsEveryProbeArmBelowItsEmptyResponseCeiling(TypedMemEvalVertical vertical)
    {
        var probes = Metadata(vertical).GetProperty("probes");
        Assert.True(
            probes.TryGetProperty("empty_rate_by_arm", out var byArm),
            $"{vertical} carries no empty-rate record. Re-run tools/run_typedmemeval_probes.py.");

        // Equality against a C# list, not "every arm present is under its ceiling". The first cut
        // of this gate checked only the latter, and v9strip -- a real arm -- produced no bucket at
        // all because the tally parsed an arm token as `v` plus digits and quietly filed its 700
        // calls under v1. An arm with no row cleared the ceiling by not being there, which is the
        // pass-by-absence shape this suite has been bitten by before (see the V7 refused-features
        // check, which is written this way for the same reason). Adding an arm to the runner now
        // fails here until it is named and monitored.
        var recorded = byArm.EnumerateObject().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        Assert.False(
            recorded.Contains("unknown"),
            $"{vertical} has probe calls that could not be attributed to a named arm. An arm " +
            "nobody can name is an arm nobody is watching — fix _arm_and_kind, do not ignore it.");
        Assert.Equal(
            ExpectedProbeArms.OrderBy(n => n, StringComparer.Ordinal),
            recorded.OrderBy(n => n, StringComparer.Ordinal));

        foreach (var arm in byArm.EnumerateObject())
        {
            // `rate` is PROBE ANSWERS only. Judge grades ride alongside in judge_* and are
            // deliberately not pooled in: doing so put 1246 healthy grades into the denominators
            // and understated every affected arm (v3 78.2% read as 66.7%, v9 7.3% as 3.8%).
            Assert.Equal("probe_answers", arm.Value.GetProperty("population").GetString());

            if (!arm.Value.TryGetProperty("rate", out var rateNode) ||
                rateNode.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            // The ceiling and the ratchet are C# constants for the same reason the separability
            // bar is: a record that supplied its own threshold would always clear it.
            var rate = rateNode.GetDouble();
            var ceiling = KnownEmptyRateRatchet.TryGetValue(arm.Name, out var allowed)
                ? allowed
                : EmptyRateCeiling;

            Assert.True(
                rate <= ceiling,
                $"probe arm {arm.Name} on {vertical} returned no answer on {rate:P1} of calls, " +
                $"above its {ceiling:P1} ceiling. An empty completion is not evidence: on V8/V9 it " +
                "understates a ceiling, and on V3/V6 it scores as a PASS and overstates validity. " +
                "Raise the completion budget or fix capture — do not raise this number.");
        }
    }

    /// <summary>Empty-response ceiling for any arm without a recorded ratchet entry.</summary>
    private const double EmptyRateCeiling = 0.05;

    /// <summary>
    /// Known-bad arms awaiting the capture re-run, as a RATCHET: these may only ever shrink.
    /// V3 and V6 are the anti-conservative pair — their results are uncitable until re-run — and
    /// they are pinned here rather than waived so the defect stays visible and cannot regress.
    /// When the re-run lands, lower these to <see cref="EmptyRateCeiling"/> and delete the entry.
    /// </summary>
    private static readonly Dictionary<string, double> KnownEmptyRateRatchet = new(StringComparer.Ordinal)
    {
        // Probe-answer rates. These are HIGHER than the 0.6667 / 0.2114 published in 0.27.0-beta,
        // and nothing got worse between the two: that cut pooled judge grades into the denominator
        // and mis-filed 700 v9strip calls under v1, so every affected arm was understated. The
        // numbers moved because the instrument was corrected, and the ratchet firing on that move
        // is the ratchet working.
        ["v3"] = 0.7818,
        ["v6"] = 0.2696,

        // v9 is a ceiling BREACH, not a waiver. Its true probe-answer rate has always been above
        // EmptyRateCeiling; pooling 102 judge grades into 110 probe calls is the only reason it
        // ever read as passing. Recorded here so it stays visible and can only shrink, rather than
        // quietly clearing a bar it does not clear. Direction is conservative -- silence scores as
        // a failure on V9, so the published retrieval ceiling is a lower bound.
        ["v9"] = 0.0727,
    };

    /// <summary>
    /// Every arm the probe runner is expected to tally. Named here rather than inferred from the
    /// record, so an arm that stops reporting fails instead of passing by absence.
    /// </summary>
    private static readonly string[] ExpectedProbeArms =
        ["v1", "v2", "v3", "v6", "v8", "v9", "v9strip"];

    private const double SeparabilityMaxAuc = 0.75;

    /// <summary>
    /// Numeric shape features a corpus may not be separable by, mirroring the generator's list.
    /// </summary>
    /// <remarks>
    /// The two phrase-recurrence features (<c>gold_marker_ngram</c>, <c>boilerplate_ngram</c>) are
    /// deliberately absent: they need the n-gram machinery, and re-implementing that here would be
    /// transcription rather than an independent check. They are re-measured in CI by
    /// <c>stamp_typedmemeval_separability.py --check</c> instead. Everything numeric is recomputed
    /// here, including the per-role and first-turn slices — the slices are the ones that matter,
    /// because a corpus whose pooled totals balance can still have a perfectly separable first turn.
    /// </remarks>
    /// <summary>
    /// Ordinal positions the (position, role) occupancy features cover. Mirrors
    /// <c>_ROLE_ORDER_POSITIONS</c> in <c>tools/typedmemeval_common.py</c>.
    /// </summary>
    private const int RoleOrderPositions = 4;

    private static readonly string[] RefusedFeatures =
    [
        "session_length_chars", "turn_count", "position_in_haystack", "digit_density",
        "uppercase_density", "sentence_count", "punctuation_density", "em_dash_density",
        "mean_turn_chars", "type_token_ratio",
        "user_length_chars", "user_uppercase_density", "user_sentence_count",
        "user_punctuation_density", "user_type_token_ratio", "user_mean_turn_chars",
        "assistant_length_chars", "assistant_uppercase_density", "assistant_sentence_count",
        "assistant_punctuation_density", "assistant_type_token_ratio", "assistant_mean_turn_chars",
        "first_user_length_chars", "first_user_uppercase_density", "first_user_sentence_count",
        "first_assistant_length_chars", "first_assistant_uppercase_density",
        "first_assistant_sentence_count",
        // Turn-role ORDER. Added after the consuming project found Episodic v4 gold identifiable
        // without reading a word: gold ran u|a|a|u|a while every distractor ran u|a|u|a|a, and the
        // published feature set had no ordinal slot beyond the first and no (position, role)
        // occupancy. Per-role COUNTS were all at exactly 0.5000 — a successful equalisation is what
        // made the residual invisible, because appending to the tail cannot repair a prefix.
        "role_sequence",
        "position_0_is_user", "position_0_is_assistant",
        "position_1_is_user", "position_1_is_assistant",
        "position_2_is_user", "position_2_is_assistant",
        "position_3_is_user", "position_3_is_assistant",
        // Per-(role, ordinal slot) length, for slots after the first. Requested by the consuming
        // project, which found per-slot lengths separating where the per-role totals did not: the
        // length equalisation balances the aggregate and can still leave the SECOND turn of a role
        // separable. Slot 0 is covered by the first_* axes above.
        "user_slot1_length_chars", "user_slot2_length_chars", "user_slot3_length_chars",
        "assistant_slot1_length_chars", "assistant_slot2_length_chars",
        "assistant_slot3_length_chars",
    ];

    /// <summary>
    /// Single-feature AUC over (gold, distractor) pairs formed WITHIN a question, pooled across
    /// questions and folded once to [0.5, 1.0].
    /// </summary>
    /// <remarks>
    /// Pairing within the question is the whole point. The attacker is handed one haystack and asked
    /// which session holds the evidence, so comparisons across questions answer a different and
    /// much easier question — pooling diluted a real Forgetting tell from 0.903 to 0.616, and
    /// questions with no gold at all contributed distractor-only values that made the number better
    /// the more abstention questions the vertical had.
    /// </remarks>
    private static Dictionary<string, double> SeparabilityAucs(
        TypedMemEvalVertical vertical, HashSet<string> exempt)
    {
        var entries = TypedMemEvalCorpus.Load(vertical);
        var results = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var name in RefusedFeatures.Where(f => !exempt.Contains(f)))
        {
            // Categorical, so it is not a per-session scalar: the score is the best any single
            // role sequence achieves as a presence indicator. Measured this way a shape whose gold
            // reads u|a|a|u|a while every distractor reads u|a|u|a|a is caught as ONE feature,
            // rather than requiring the right ordinal position to have been guessed in advance.
            if (name == "role_sequence")
            {
                results[name] = WorstRoleSequenceAuc(entries);
                continue;
            }

            double wins = 0;
            long pairs = 0;
            foreach (var entry in entries)
            {
                var sessions = entry.HaystackSessions ?? [];
                var goldIds = new HashSet<string>(entry.AnswerSessionIds ?? [], StringComparer.Ordinal);
                var ids = entry.HaystackSessionIds ?? [];
                List<double> gold = [], other = [];
                for (var i = 0; i < sessions.Count; i++)
                {
                    var value = Feature(name, sessions[i], i, sessions.Count);
                    if (i < ids.Count && goldIds.Contains(ids[i])) gold.Add(value);
                    else other.Add(value);
                }
                foreach (var g in gold)
                    foreach (var o in other)
                        wins += g > o ? 1.0 : g == o ? 0.5 : 0.0;
                pairs += (long)gold.Count * other.Count;
            }

            var auc = pairs == 0 ? 0.5 : wins / pairs;
            results[name] = Math.Max(auc, 1.0 - auc);
        }
        return results;
    }

    /// <summary>The best folded AUC any single turn-role sequence achieves as a gold indicator.</summary>
    private static double WorstRoleSequenceAuc(IReadOnlyList<LongMemEvalEntry> entries)
    {
        static string Signature(List<LongMemEvalTurn> session) =>
            string.Concat(session.Select(t => (t.Role ?? "?")[0]));

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            foreach (var session in entry.HaystackSessions ?? [])
                candidates.Add(Signature(session));

        var worst = 0.5;
        foreach (var candidate in candidates)
        {
            double wins = 0;
            long pairs = 0;
            foreach (var entry in entries)
            {
                var sessions = entry.HaystackSessions ?? [];
                var ids = entry.HaystackSessionIds ?? [];
                var goldIds = new HashSet<string>(entry.AnswerSessionIds ?? [], StringComparer.Ordinal);
                List<double> gold = [], other = [];
                for (var i = 0; i < sessions.Count; i++)
                {
                    var value = Signature(sessions[i]) == candidate ? 1.0 : 0.0;
                    if (i < ids.Count && goldIds.Contains(ids[i])) gold.Add(value);
                    else other.Add(value);
                }
                foreach (var g in gold)
                    foreach (var o in other)
                        wins += g > o ? 1.0 : g == o ? 0.5 : 0.0;
                pairs += (long)gold.Count * other.Count;
            }

            if (pairs == 0) continue;
            var auc = wins / pairs;
            worst = Math.Max(worst, Math.Max(auc, 1.0 - auc));
        }
        return worst;
    }

    private static double Feature(string name, List<LongMemEvalTurn> session, int index, int count)
    {
        var text = string.Join(" ", session.Select(t => t.Content ?? string.Empty));
        double Ratio(double numerator, double denominator) => numerator / Math.Max(1.0, denominator);

        string RoleText(string role) => string.Join(
            " ", session.Where(t => t.Role == role).Select(t => t.Content ?? string.Empty));
        int RoleTurns(string role) => session.Count(t => t.Role == role);
        string SlotText(string role, int ordinal) => session
            .Where(t => t.Role == role).Select(t => t.Content ?? string.Empty)
            .Skip(ordinal).FirstOrDefault() ?? string.Empty;

        foreach (var role in new[] { "user", "assistant" })
        {
            if (name == $"{role}_length_chars") return RoleText(role).Length;
            if (name == $"{role}_uppercase_density")
                return Ratio(RoleText(role).Count(char.IsUpper), RoleText(role).Length);
            if (name == $"{role}_sentence_count")
                return RoleText(role).Count(c => c is '.' or '!' or '?');
            if (name == $"{role}_punctuation_density")
                return Ratio(
                    RoleText(role).Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)),
                    RoleText(role).Length);
            if (name == $"{role}_type_token_ratio") return TypeTokenRatio(RoleText(role));
            if (name == $"{role}_mean_turn_chars")
                return Ratio(RoleText(role).Length, RoleTurns(role));
            if (name == $"first_{role}_length_chars") return SlotText(role, 0).Length;
            if (name == $"first_{role}_uppercase_density")
                return Ratio(SlotText(role, 0).Count(char.IsUpper), SlotText(role, 0).Length);
            if (name == $"first_{role}_sentence_count")
                return SlotText(role, 0).Count(c => c is '.' or '!' or '?');

            // (position, role) occupancy. The first four positions only: beyond that the sessions
            // in a question no longer share a common length, so the feature stops being comparable
            // rather than becoming safe.
            for (var position = 0; position < RoleOrderPositions; position++)
            {
                if (name != $"position_{position}_is_{role}") continue;
                return position < session.Count && session[position].Role == role ? 1.0 : 0.0;
            }

            for (var slot = 1; slot < RoleOrderPositions; slot++)
            {
                if (name != $"{role}_slot{slot}_length_chars") continue;
                return SlotText(role, slot).Length;
            }
        }

        return name switch
        {
            "session_length_chars" => text.Length,
            "turn_count" => session.Count,
            "position_in_haystack" => index / (double)Math.Max(1, count - 1),
            "digit_density" => Ratio(text.Count(char.IsDigit), text.Length),
            "uppercase_density" => Ratio(text.Count(char.IsUpper), text.Length),
            "sentence_count" => text.Count(c => c is '.' or '!' or '?'),
            "punctuation_density" =>
                Ratio(text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)), text.Length),
            "em_dash_density" => Ratio(text.Count(c => c == '—'), text.Length),
            "mean_turn_chars" => Ratio(text.Length, session.Count),
            "type_token_ratio" => TypeTokenRatio(text),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown V7 feature."),
        };
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void DeclaredStructureIsRederivedFromTheShippedCorpus(TypedMemEvalVertical vertical)
    {
        // The ADR lists H, G and the ceiling table as CI-checked. They were stamped by the
        // generator from the corpus it had just built and then re-read by nothing, which is a
        // record rather than a check — the same shape of gap as a probe nobody re-runs. Every
        // number below is derived here from the shipped bytes and compared to what is declared.
        var metadata = Metadata(vertical);
        var structure = metadata.GetProperty("structure");
        var entries = TypedMemEvalCorpus.Load(vertical);

        var goldCounts = new List<int>();
        var distractorCounts = new List<int>();
        foreach (var entry in entries)
        {
            var ids = entry.HaystackSessionIds ?? [];
            var gold = (entry.AnswerSessionIds ?? []).Count(id => ids.Contains(id));
            goldCounts.Add(gold);
            distractorCounts.Add(ids.Count - gold);
        }

        // H counts non-gold sessions only (ADR §4), never double-counting G.
        Assert.Equal(structure.GetProperty("h_min").GetInt32(), distractorCounts.Min());
        Assert.Equal(structure.GetProperty("h_max").GetInt32(), distractorCounts.Max());

        var declaredG = structure.GetProperty("g_distribution").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetInt32());
        var actualG = goldCounts.GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(declaredG.OrderBy(p => p.Key), actualG.OrderBy(p => p.Key));

        // The structural ceiling: with a budget of K_ref sessions, a question needing G of them can
        // at best surface min(G, K_ref)/G. Recomputed rather than trusted.
        var kRef = metadata.GetProperty("k_ref").GetInt32();
        var ceiling = metadata.GetProperty("ceiling").GetProperty("by_g");
        foreach (var g in actualG.Keys.Where(g => g > 0))
        {
            var expected = Math.Round(Math.Min(g, kRef) / (double)g, 4);
            Assert.Equal(expected, ceiling.GetProperty(g.ToString(CultureInfo.InvariantCulture)).GetDouble(), 3);
        }
    }

    [Fact]
    public void EveryRuntimeStringNamesTheShippedRevision()
    {
        // Revision drift has now been a defect twice: the runner's citation rule said v2 in one
        // sentence and v1 in the next, and the projected result — the copy that reaches a
        // consumer's report — named a revision the corpora had already left behind. Two question
        // sets sharing a label is the exact failure the family's identity rule exists to prevent,
        // so the strings a consumer can actually read are checked against the one constant.
        var revision = TypedMemEvalVerticalDescriptor.CorpusRevision;
        var citation = TypedMemEvalEvalResultAdapter.CitationRule;

        // Every runtime-visible string, not just the citation rule. The CLI's own `--help` text
        // still read "TypedMemEval v1" two revisions later, because it was a separate literal that
        // nothing tied to the constant — which is the same defect in a place a user sees first.
        var family = AgentEval.Core.Benchmarks.BenchmarkFamilyRegistry.TryGet("typedmemeval");
        Assert.NotNull(family);
        var strings = new List<string> { citation, family!.Description };
        strings.AddRange(family.Presets.Select(p => p.Description));

        foreach (var text in strings)
        {
            var named = System.Text.RegularExpressions.Regex.Matches(text, @"\bv\d+\b")
                .Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();
            Assert.Equal([revision], named);
        }

        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            Assert.EndsWith($"-{revision}", descriptor.CorpusId, StringComparison.Ordinal);
            Assert.Equal(revision, descriptor.Revision);
        }
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void SessionsAreEmittedInTimestampOrder(TypedMemEvalVertical vertical)
    {
        // The ADR states this as a property the corpora have. It was true of the data and asserted
        // by nothing, which makes it a description rather than a guarantee — and Forgetting depends
        // on it, because its statement-before-invalidation constraint is stated in both session
        // order AND timestamp order, so a corpus where the two disagree satisfies one reading while
        // violating the other.
        foreach (var entry in TypedMemEvalCorpus.Load(vertical))
        {
            var dates = (entry.HaystackDates ?? [])
                .Select(d => LongMemEvalTimestamps.TryParse(d)
                    ?? throw new FormatException($"{entry.QuestionId}: unparseable timestamp '{d}'"))
                .ToList();
            Assert.Equal(dates.OrderBy(d => d).ToList(), dates);
        }
    }

    private static double TypeTokenRatio(string text)
    {
        var tokens = text.ToLowerInvariant()
            .Split((char[])[' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '"', '\''],
                   StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        return tokens.Count == 0 ? 0.0 : tokens.Distinct(StringComparer.Ordinal).Count() / (double)tokens.Count;
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Selection_IsDeterministicUnderTheSameSeedAndBudget(TypedMemEvalVertical vertical)
    {
        // A benchmark whose sample moved between runs would make every band meaningless, because
        // the runs would not have measured the same questions.
        var options = new ExternalBenchmarkOptions { MaxQuestions = 12, RandomSeed = 4242 };

        var first = TypedMemEvalCorpus.Load(vertical, options).Select(e => e.QuestionId).ToArray();
        var second = TypedMemEvalCorpus.Load(vertical, options).Select(e => e.QuestionId).ToArray();

        Assert.Equal(12, first.Length);
        Assert.Equal(first, second);

        var different = TypedMemEvalCorpus
            .Load(vertical, new ExternalBenchmarkOptions { MaxQuestions = 12, RandomSeed = 99 })
            .Select(e => e.QuestionId)
            .ToArray();
        Assert.NotEqual(first, different);
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void Extensions_CoverEveryQuestionAndAgreeWithTheirCorpus(TypedMemEvalVertical vertical)
    {
        var descriptor = TypedMemEvalVerticals.For(vertical);
        var extensions = TypedMemEvalExtensions.Parse(TypedMemEvalCorpus.ReadJson(vertical));

        Assert.Equal(descriptor.QuestionCount, extensions.Count);
        foreach (var (questionId, extension) in extensions)
        {
            Assert.Equal(descriptor.Slug, extension.Vertical);
            Assert.False(
                string.IsNullOrWhiteSpace(extension.Shape),
                $"{questionId} has no shape, so its outcomes could not be reported per stratum.");
        }
    }

    private static JsonElement Metadata(TypedMemEvalVertical vertical)
        => JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical)).RootElement.Clone();
}
