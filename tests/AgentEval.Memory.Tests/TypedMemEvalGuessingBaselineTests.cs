// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The corpus's own chance floors, crossed to the consumer surface (C-F).
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Why this exists.</b> A consumer of a TypedMemEval run gets aggregate outcome counts and
/// nothing else — no per-question results. So "correct 35 of 80" reads as 44% understanding.
/// <b>Procedural declares a chance floor on all 80 of its questions</b>, summing to 27.2: on that
/// vertical about a third of any score is available for guessing, and until now nothing on the C#
/// surface said so.
/// </para>
/// <para>
/// ⚠ <b>It is an upper bound on free score, never a subtraction.</b> It assumes the system answers
/// every closed-choice question. One that abstains scores below it without being worse, because
/// abstention is not a wrong answer in this family.
/// </para>
/// <para>
/// ⚠ <b>And it is NOT <c>ChanceFloor</c>, nor <c>CalibratedFloorMean</c>.</b> The library's
/// <c>ChanceFloor</c> models an arm's declared draw budget applied at an admission door;
/// <c>CalibratedFloorMean</c> is BM25 retrieval coverage — a competent baseline, not a null model.
/// Conflating either with this would let "beat word matching" masquerade as "cleared chance".
/// </para>
/// </remarks>
public class TypedMemEvalGuessingBaselineTests
{
    [Fact]
    public void ProceduralDeclaresAFloorOnEveryQuestion_AndAThirdOfAnyScoreIsFree()
    {
        var baseline = TypedMemEvalCorpus.GuessingBaseline(TypedMemEvalVertical.Procedural);

        Assert.NotNull(baseline);
        Assert.Equal(80, baseline!.Value.Total);
        Assert.Equal(80, baseline.Value.Declared);

        // 27.2 of 80. Asserted as a band, not a literal: the exact sum moves if a shape's k changes,
        // and pinning it to four decimals would make a legitimate corpus edit look like a defect.
        Assert.InRange(baseline.Value.Guessing, 26.0, 28.5);
    }

    [Fact]
    public void SevenVerticalsDeclareNoFloorAtAll_SoThisIsNeverABlanketCorrection()
    {
        // The concentration is the point. If every vertical had one, a single family-wide correction
        // would do; because three do and seven do not, it must be reported per vertical or it would
        // silently deflate the seven that give nothing away.
        foreach (var vertical in new[]
                 {
                     TypedMemEvalVertical.Arithmetic, TypedMemEvalVertical.Bitemporal,
                     TypedMemEvalVertical.Episodic, TypedMemEvalVertical.Forgetting,
                     TypedMemEvalVertical.Prospective, TypedMemEvalVertical.Temporal,
                     TypedMemEvalVertical.WorkingMemory,
                 })
        {
            Assert.Null(TypedMemEvalCorpus.GuessingBaseline(vertical));
        }

        // …and the three that do. Stated as a positive control: a bug that returned null for
        // everything would pass the loop above and prove nothing.
        Assert.NotNull(TypedMemEvalCorpus.GuessingBaseline(TypedMemEvalVertical.Procedural));
        Assert.NotNull(TypedMemEvalCorpus.GuessingBaseline(TypedMemEvalVertical.Conjunction));
        Assert.NotNull(TypedMemEvalCorpus.GuessingBaseline(TypedMemEvalVertical.Semantic));
    }

    [Fact]
    public void TheBaselineNeverExceedsTheQuestionCount_NorFallsBelowChanceOnOne()
    {
        // A floor is a probability, so the sum cannot exceed the number of questions declaring one.
        // ⚠ This does NOT catch a nested-floor double-count: measured, 0 of 565 questions declare
        //   two floors, so the "largest, once per question" rule is a no-op on real data and an
        //   ablation through this path cannot fail. Found by ablating and watching it stay green.
        //   TheNestedFloorRuleTakesTheLargestOnce covers that rule directly instead.
        foreach (var vertical in new[]
                 {
                     TypedMemEvalVertical.Procedural, TypedMemEvalVertical.Conjunction,
                     TypedMemEvalVertical.Semantic,
                 })
        {
            var baseline = TypedMemEvalCorpus.GuessingBaseline(vertical)!.Value;

            Assert.True(baseline.Declared <= baseline.Total,
                $"{vertical}: {baseline.Declared} declared of {baseline.Total} total");
            Assert.True(baseline.Guessing <= baseline.Declared,
                $"{vertical}: guessing {baseline.Guessing} exceeds {baseline.Declared} declared — "
              + "a question was counted twice");
            Assert.True(baseline.Guessing > 0, $"{vertical}: a declared floor summed to zero");
        }
    }

    [Fact]
    public void TheNestedFloorRuleTakesTheLargestOnce()
    {
        // Constructed, not drawn from a corpus, because no shipped corpus exercises it (0 of 565).
        // Two floors on one question, at different depths: the rule must return the LARGEST once,
        // never their sum. Summing would overstate how much score was free, which is the
        // flattering direction and therefore the one to guard.
        using var nested = System.Text.Json.JsonDocument.Parse(
            """{"shape":"x","chance_floor":0.25,"join":{"chance_floor":0.5}}""");

        Assert.Equal(0.5, TypedMemEvalCorpus.LargestDeclaredFloor(nested.RootElement));

        // …and the ordinary single-floor case still returns it.
        using var single = System.Text.Json.JsonDocument.Parse("""{"chance_floor":0.3333}""");
        Assert.Equal(0.3333, TypedMemEvalCorpus.LargestDeclaredFloor(single.RootElement));

        // …and a block declaring none returns null rather than 0. A 0.0 floor would say "nothing
        // is free here", which is a measurement; absence is not a zero.
        using var none = System.Text.Json.JsonDocument.Parse("""{"shape":"x","event_count":3}""");
        Assert.Null(TypedMemEvalCorpus.LargestDeclaredFloor(none.RootElement));
    }

    [Fact]
    public void ConjunctionAndSemanticDeclareOnSomeQuestionsOnly_NotAll()
    {
        // The partial case, which the all-80 Procedural test cannot see: a vertical where only part
        // of the corpus is closed-choice. If the walk ever started defaulting a floor onto questions
        // that declare none, Declared would jump to Total here and this fails.
        var conjunction = TypedMemEvalCorpus.GuessingBaseline(TypedMemEvalVertical.Conjunction)!.Value;
        Assert.Equal(65, conjunction.Total);
        Assert.InRange(conjunction.Declared, 1, 64);

        var semantic = TypedMemEvalCorpus.GuessingBaseline(TypedMemEvalVertical.Semantic)!.Value;
        Assert.Equal(50, semantic.Total);
        Assert.InRange(semantic.Declared, 1, 49);
    }
}
