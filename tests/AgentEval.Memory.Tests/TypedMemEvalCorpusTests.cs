// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

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
                    Assert.DoesNotContain("_abs", questionId, StringComparison.Ordinal);
                    Assert.Single(extension.GoldSessionIndices);
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

        Assert.Equal(new[] { 1, 5, 15, 40 }, byDistance.Keys.Select(k => k!.Value).Order().ToArray());
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

        var threshold = v7.GetProperty("threshold_auc").GetDouble();
        var features = v7.GetProperty("features");
        var exempt = v7.GetProperty("exempt_features")
            .EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);

        foreach (var name in v7.GetProperty("refused_features").EnumerateArray()
                     .Select(e => e.GetString()!))
        {
            var auc = features.GetProperty(name).GetDouble();
            Assert.True(
                auc < threshold,
                $"{vertical}: '{name}' separates gold from distractors at AUC {auc:F3}, at or above " +
                $"the {threshold} refusal threshold — a classifier can find the evidence without " +
                $"reading it.");
        }

        // WorkingMemory pins its fact to session 0 (ADR §5.4), so position separates gold perfectly
        // and is meant to. Asserted as a declaration rather than left as an unexplained pass.
        if (vertical == TypedMemEvalVertical.WorkingMemory)
            Assert.Contains("position_in_haystack", exempt);
        else
            Assert.Empty(exempt);
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
