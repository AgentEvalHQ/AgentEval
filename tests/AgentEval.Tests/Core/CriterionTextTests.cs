// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Pins the ONE rule that un-renders <see cref="ChatClientEvaluator"/>'s own ordinal, and pins the
/// round trip through the evaluator that renders it.
/// </summary>
/// <remarks>
/// The hazard this covers is not hypothetical: the evaluator prepends <c>"{i + 1}. "</c> to every
/// criterion in the judge prompt, a faithful judge echoes that back, and the three-character offset
/// defeated exact, whitespace-normalised AND prefix matching in two separate consumers. The tests
/// below are written so that the NEGATIVE half is as loud as the positive one — a rule that
/// rewrites text is only safe while it is shown to leave unmatched text alone.
/// </remarks>
public class CriterionTextTests
{
    // ── StripLeadingEnumerator ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1. Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("12. Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("1) Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("(1) Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("#1 Every recommendation is grounded", "#1 Every recommendation is grounded")]
    [InlineData("a. Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("A. Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("iv. Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("- Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("* Every recommendation is grounded", "Every recommendation is grounded")]
    [InlineData("• Every recommendation is grounded", "Every recommendation is grounded")]
    public void StripLeadingEnumerator_RemovesTheFormsAListRendererProduces(string input, string expected)
        => Assert.Equal(expected, CriterionText.StripLeadingEnumerator(input));

    [Theory]
    // No separator after the label: not an enumeration, it is a sentence starting with a word.
    [InlineData("Every recommendation is grounded")]
    // A label longer than three characters is a WORD. Stripping a word is how a normaliser starts
    // inventing matches, which is the failure mode this whole type exists to avoid.
    [InlineData("Rule. Every recommendation is grounded")]
    [InlineData("Note: cite the catalogue")]
    // A bullet mark with no space is part of the text (a leading minus sign, a footnote star).
    [InlineData("-1 is not a score")]
    [InlineData("*emphasis* matters")]
    // Nothing left after the marker: stripping to empty would turn a criterion into an absence.
    [InlineData("1.")]
    [InlineData("")]
    public void StripLeadingEnumerator_LeavesAnythingThatIsNotOneMarkerAlone(string input)
        => Assert.Equal(input, CriterionText.StripLeadingEnumerator(input));

    [Fact]
    public void StripLeadingEnumerator_RemovesExactlyOneMarker()
        => Assert.Equal("2. the second one", CriterionText.StripLeadingEnumerator("1. 2. the second one"));

    // ── The SHORT-WORD hole. Found by the review pass of 2026-09-06, in BOTH directions. ───────

    [Theory]
    // A two- or three-letter WORD followed by a hyphen, a colon or a full stop is not a label.
    // Every one of these was eaten by the first shipped rule, which asked only "is it short?".
    [InlineData("Re-check the sources")]
    [InlineData("AI-generated text is labelled")]
    [InlineData("Top-3 results are relevant")]
    [InlineData("No: the answer refuses")]
    [InlineData("Do. not. hallucinate")]
    [InlineData("Q&A pairs are cited")]
    // A Roman-numeral-shaped run is still not a marker when nothing follows the separator but more
    // word: a marker in a rendered list is followed by a space.
    [InlineData("iv-league schools are named")]
    [InlineData("i.e. the second reading")]
    public void StripLeadingEnumerator_DoesNotEatAShortLeadingWord(string input)
        => Assert.Equal(input, CriterionText.StripLeadingEnumerator(input));

    [Fact]
    public void AreSameCriterion_DoesNotJoinTwoCriteriaThatDifferByAShortLeadingWord()
    {
        // ⚠ THE UNSAFE DIRECTION. This returned true on the first shipped rule, because both sides
        // normalised to "check the sources" — a similarity match made by the type whose remarks say
        // it makes none. In CalibratedEvaluator that is one criterion's verdict aggregated under
        // another criterion's name.
        Assert.False(CriterionText.AreSameCriterion("Re-check the sources", "Check the sources"));
        Assert.False(CriterionText.AreSameCriterion("AI-generated text is labelled",
                                                   "generated text is labelled"));
        Assert.Null(CriterionText.MatchDeclared("Check the sources", ["Re-check the sources"]));
    }

    [Fact]
    public void AreSameCriterion_StillJoinsTheOrdinalEchoOfAShortLeadingWordCriterion()
    {
        // ⚠ THE OTHER DIRECTION, and it failed at the same time: the declared side lost its "Re-"
        // while the echoed side lost only the ordinal, so the two normalised forms differed and the
        // echo this whole type exists to rejoin did not rejoin.
        Assert.True(CriterionText.AreSameCriterion("Re-check the sources", "1. Re-check the sources"));
        Assert.Equal("Re-check the sources",
                     CriterionText.MatchDeclared("1. Re-check the sources", ["Re-check the sources"]));
    }

    [Theory]
    // The forms that ARE markers keep working — a Roman numeral, a single letter, digits.
    [InlineData("iv. the fourth", "the fourth")]
    [InlineData("III) the third", "the third")]
    [InlineData("b: the second", "the second")]
    [InlineData("12. the twelfth", "the twelfth")]
    public void StripLeadingEnumerator_StillRemovesARealLabel(string input, string expected)
        => Assert.Equal(expected, CriterionText.StripLeadingEnumerator(input));

    [Fact]
    public void StripLeadingEnumerator_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => CriterionText.StripLeadingEnumerator(null!));

    // ── Normalize ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_CollapsesWhitespaceLowerCasesAndStripsOneMarker()
        => Assert.Equal("every recommendation is grounded",
                        CriterionText.Normalize("  1.   Every    recommendation\n is  GROUNDED  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t \n ")]
    public void Normalize_EmptyInputsAllNormaliseToEmpty(string? input)
        => Assert.Equal(string.Empty, CriterionText.Normalize(input));

    // ── AreSameCriterion ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AreSameCriterion_SeesThroughTheOrdinalWeRendered()
        => Assert.True(CriterionText.AreSameCriterion("1. Is accurate", "Is accurate"));

    [Fact]
    public void AreSameCriterion_IsSymmetric()
        => Assert.True(CriterionText.AreSameCriterion("Is accurate", "1. Is accurate"));

    [Fact]
    public void AreSameCriterion_DifferentCriteriaStayDifferent()
        => Assert.False(CriterionText.AreSameCriterion("1. Is accurate", "2. Is concise"));

    [Fact]
    public void AreSameCriterion_TwoAbsencesAreNotTheSameCriterion()
    {
        // An empty criterion carries no claim. Matching one absence to another is how a join
        // starts producing verdicts for criteria nobody stated.
        Assert.False(CriterionText.AreSameCriterion("", ""));
        Assert.False(CriterionText.AreSameCriterion(null, null));
        Assert.False(CriterionText.AreSameCriterion("   ", null));
    }

    // ── MatchDeclared ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MatchDeclared_ReturnsTheDeclaredTextVerbatim()
    {
        string[] declared = ["Is accurate", "Is concise"];
        Assert.Equal("Is concise", CriterionText.MatchDeclared("2. Is  CONCISE", declared));
    }

    [Fact]
    public void MatchDeclared_ReturnsNullWhenTheJudgeInventedIt()
    {
        string[] declared = ["Is accurate", "Is concise"];
        Assert.Null(CriterionText.MatchDeclared("3. Is polite", declared));
    }

    [Fact]
    public void MatchDeclared_AmbiguousRubricReturnsNullRatherThanPickingOne()
    {
        // Two declared criteria that differ only by the marker is a RUBRIC defect. Resolving it by
        // taking the first would hide it behind a plausible join.
        string[] declared = ["Is accurate", "1. Is accurate"];
        Assert.Null(CriterionText.MatchDeclared("Is accurate", declared));
    }

    [Fact]
    public void MatchDeclared_ADuplicatedDeclarationIsNotAmbiguous()
    {
        string[] declared = ["Is accurate", "Is accurate"];
        Assert.Equal("Is accurate", CriterionText.MatchDeclared("1. Is accurate", declared));
    }

    [Fact]
    public void MatchDeclared_NullDeclared_Throws()
        => Assert.Throws<ArgumentNullException>(() => CriterionText.MatchDeclared("x", null!));

    // ── RealignToDeclared ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RealignToDeclared_ReAnchorsTheEchoAndKeepsEverythingElse()
    {
        var results = new[]
        {
            new CriterionResult { Criterion = "1. Is accurate", Met = true,  Explanation = "because" },
            new CriterionResult { Criterion = "2. Is concise",  Met = false, Explanation = "rambles" },
        };

        var realigned = CriterionText.RealignToDeclared(results, ["Is accurate", "Is concise"]);

        Assert.Equal(2, realigned.Count);
        Assert.Equal("Is accurate", realigned[0].Criterion);
        Assert.True(realigned[0].Met);
        Assert.Equal("because", realigned[0].Explanation);
        Assert.Equal("Is concise", realigned[1].Criterion);
        Assert.False(realigned[1].Met);
        Assert.Equal("rambles", realigned[1].Explanation);
    }

    [Fact]
    public void RealignToDeclared_LeavesAnInventedCriterionEXACTLYAsTheJudgeWroteIt()
    {
        // This is the half that keeps the fix honest. A consumer whose job is to report "the judge
        // answered something nobody asked" must still be able to.
        var results = new[]
        {
            new CriterionResult { Criterion = "3. Uses a friendly tone", Met = true, Explanation = "e" },
        };

        var realigned = CriterionText.RealignToDeclared(results, ["Is accurate", "Is concise"]);

        Assert.Equal("3. Uses a friendly tone", Assert.Single(realigned).Criterion);
    }

    [Fact]
    public void RealignToDeclared_AVerbatimMatchIsReturnedAsTheSameInstance()
    {
        var one = new CriterionResult { Criterion = "Is accurate", Met = true, Explanation = "e" };
        var realigned = CriterionText.RealignToDeclared([one], ["Is accurate"]);
        Assert.Same(one, Assert.Single(realigned));
    }

    [Fact]
    public void RealignToDeclared_PreservesOrderAndCount()
    {
        var results = new[]
        {
            new CriterionResult { Criterion = "2. Is concise",    Met = true,  Explanation = "a" },
            new CriterionResult { Criterion = "who asked",        Met = false, Explanation = "b" },
            new CriterionResult { Criterion = "1. Is accurate",   Met = true,  Explanation = "c" },
        };

        var realigned = CriterionText.RealignToDeclared(results, ["Is accurate", "Is concise"]);

        Assert.Equal(["Is concise", "who asked", "Is accurate"], realigned.Select(r => r.Criterion));
    }

    [Fact]
    public void RealignToDeclared_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => CriterionText.RealignToDeclared(null!, ["a"]));
        Assert.Throws<ArgumentNullException>(
            () => CriterionText.RealignToDeclared(Array.Empty<CriterionResult>(), null!));
    }

    // ── The round trip, through the evaluator that renders the ordinal ────────────────────────

    [Fact]
    public async Task ChatClientEvaluator_AJudgeThatEchoesOurOrdinalIsJoinedToTheDeclaredCriterion()
    {
        // The judge answers with EXACTLY the text ChatClientEvaluator rendered into the prompt.
        // Before the re-anchoring, both of these came back as criteria nobody declared.
        var judge = new FakeChatClient("""
            {
                "criteriaResults": [
                    {"criterion": "1. Is accurate", "met": true,  "explanation": "checked"},
                    {"criterion": "2. Is concise",  "met": false, "explanation": "rambles"}
                ],
                "overallScore": 70,
                "summary": "s",
                "improvements": []
            }
            """);

        var result = await new ChatClientEvaluator(judge)
            .EvaluateAsync("in", "out", ["Is accurate", "Is concise"]);

        Assert.False(result.EvaluationFailed);
        Assert.Equal(["Is accurate", "Is concise"], result.CriteriaResults.Select(c => c.Criterion));
        Assert.True(result.CriteriaResults[0].Met);
        Assert.False(result.CriteriaResults[1].Met);
    }

    [Fact]
    public async Task ChatClientEvaluator_StillRendersTheOrdinalIntoThePrompt()
    {
        // The prompt is what a judged run MEASURES. Un-rendering the echo on the way out must not
        // quietly change what the judge was shown — that would move every judged verdict.
        var judge = new FakeChatClient("""{"criteriaResults":[],"overallScore":1,"summary":"s"}""");

        await new ChatClientEvaluator(judge).EvaluateAsync("in", "out", ["Is accurate", "Is concise"]);

        var prompt = judge.ReceivedMessages.Single().Last().Text;
        Assert.Contains("1. Is accurate", prompt, StringComparison.Ordinal);
        Assert.Contains("2. Is concise", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClientEvaluator_ACriterionNobodyDeclaredSurvivesTheRoundTrip()
    {
        var judge = new FakeChatClient("""
            {
                "criteriaResults": [
                    {"criterion": "1. Is accurate", "met": true, "explanation": "checked"},
                    {"criterion": "9. Rhymes",      "met": true, "explanation": "invented"}
                ],
                "overallScore": 70,
                "summary": "s",
                "improvements": []
            }
            """);

        var result = await new ChatClientEvaluator(judge).EvaluateAsync("in", "out", ["Is accurate"]);

        Assert.Equal(["Is accurate", "9. Rhymes"], result.CriteriaResults.Select(c => c.Criterion));
    }

    [Fact]
    public async Task ChatClientEvaluator_EnumeratesTheCriteriaOnlyOnce()
    {
        // The re-anchoring needs the declared list a second time. A lazily-enumerated criteria
        // sequence must not be walked twice, and a single-pass one must not throw.
        int enumerations = 0;
        IEnumerable<string> Once()
        {
            enumerations++;
            yield return "Is accurate";
        }

        var judge = new FakeChatClient("""
            {"criteriaResults":[{"criterion":"1. Is accurate","met":true,"explanation":"e"}],
             "overallScore":70,"summary":"s"}
            """);

        var result = await new ChatClientEvaluator(judge).EvaluateAsync("in", "out", Once());

        Assert.Equal(1, enumerations);
        Assert.Equal("Is accurate", Assert.Single(result.CriteriaResults).Criterion);
    }

    // ── The SECOND consumer: CalibratedEvaluator's aggregation ────────────────────────────────

    /// <summary>
    /// An <see cref="IEvaluator"/> that is NOT a <see cref="ChatClientEvaluator"/> and answers with
    /// the ordinal in front — the shape a judge behind any other transport can hand back.
    /// </summary>
    /// <remarks>
    /// ⚠ It has to be a foreign evaluator to reach the defect. A <see cref="ChatClientEvaluator"/>
    /// re-anchors its own result before <c>CalibratedEvaluator</c> ever sees it, so a test built on
    /// the public constructor passes whether the aggregation joins correctly or not — measured: with
    /// the aggregation's ordinal <c>string.Equals</c> restored, all 62 existing CalibratedEvaluator
    /// tests still pass.
    /// </remarks>
    private sealed class OrdinalEchoingEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationResult
            {
                OverallScore = 80,
                Summary = "echo",
                CriteriaResults = [.. criteria.Select((c, i) => new CriterionResult
                {
                    Criterion = $"{i + 1}. {c}",
                    Met = true,
                    Explanation = "answered",
                })],
            });
    }

    [Fact]
    public async Task CalibratedEvaluator_AggregatesAnOrdinalEchoInsteadOfDiscardingIt()
    {
        // Before this rule the verdict matched nothing, the criterion was aggregated as
        // Met = false with "No judges returned a result for this criterion.", and a MET criterion
        // silently became an UNMET one.
        var evaluator = new CalibratedEvaluator(
        [
            ("A", (IEvaluator)new OrdinalEchoingEvaluator()),
            ("B", new OrdinalEchoingEvaluator()),
        ]);

        var result = await evaluator.EvaluateAsync("in", "out", ["Is accurate", "Is concise"]);

        Assert.Equal(2, result.CriteriaResults.Count);
        foreach (var criterion in result.CriteriaResults)
        {
            Assert.True(criterion.Met, $"'{criterion.Criterion}' came back unmet: {criterion.Explanation}");
            Assert.DoesNotContain("No judges returned a result", criterion.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CalibratedEvaluator_StillReportsACriterionNoJudgeAnswered()
    {
        // The negative half. Widening the join must not make an unanswered criterion look answered.
        var evaluator = new CalibratedEvaluator(
        [
            ("A", (IEvaluator)new OrdinalEchoingEvaluator()),
        ]);

        var result = await evaluator.EvaluateAsync("in", "out", ["Is accurate"]);
        Assert.True(Assert.Single(result.CriteriaResults).Met);

        // Now ask the aggregation for a criterion the judge was never given.
        var missing = new CalibratedEvaluator([("A", (IEvaluator)new SilentEvaluator())]);
        var absent = await missing.EvaluateAsync("in", "out", ["Is polite"]);
        Assert.False(Assert.Single(absent.CriteriaResults).Met);
        Assert.Contains("No judges returned a result", absent.CriteriaResults[0].Explanation, StringComparison.Ordinal);
    }

    /// <summary>An evaluator that returns a verdict with no criteria at all.</summary>
    private sealed class SilentEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = 50, Summary = "quiet" });
    }
}
