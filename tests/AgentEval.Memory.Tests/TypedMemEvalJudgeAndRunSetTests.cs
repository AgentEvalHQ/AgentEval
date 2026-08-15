// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The judge's contract with the provider, and the aggregator's refusal to band runs that are not
/// comparable.
/// </summary>
public sealed class TypedMemEvalJudgeAndRunSetTests
{
    [Theory]
    [InlineData("correct", TypedMemEvalOutcome.Correct)]
    [InlineData("wrong", TypedMemEvalOutcome.Wrong)]
    [InlineData("abstained", TypedMemEvalOutcome.Abstained)]
    [InlineData("missed", TypedMemEvalOutcome.Missed)]
    [InlineData("premature", TypedMemEvalOutcome.Premature)]
    [InlineData("CORRECT", TypedMemEvalOutcome.Correct)]
    public void Verdict_ParsesEveryDeclaredOutcome(string raw, TypedMemEvalOutcome expected)
    {
        var parsed = TypedMemEvalVerdict.Parse($$"""{"outcome":"{{raw}}","reasoning":"because"}""");

        Assert.Equal(expected, parsed.Outcome);
        Assert.Equal("because", parsed.Reasoning);
        Assert.Null(parsed.FailureCode);
    }

    [Theory]
    [InlineData("", "empty_response")]
    [InlineData("no json here", "structured_no_json")]
    [InlineData("{not json}", "structured_malformed_json")]
    [InlineData("""{"reasoning":"x"}""", "structured_missing_outcome")]
    [InlineData("""{"outcome":42,"reasoning":"x"}""", "structured_outcome_not_string")]
    [InlineData("""{"outcome":"mostly right","reasoning":"x"}""", "structured_outcome_out_of_enum")]
    public void Verdict_RefusesToGuessFromAnythingUnusable(string raw, string expectedCode)
    {
        // A judge that guesses is a judge whose numbers cannot be defended. Every unusable response
        // gets a named failure code rather than a silent Wrong.
        var parsed = TypedMemEvalVerdict.Parse(raw);

        Assert.Null(parsed.Outcome);
        Assert.Equal(expectedCode, parsed.FailureCode);
    }

    [Fact]
    public void Verdict_RejectsTruncatedAndFilteredResponsesBeforeParsing()
    {
        // A truncated response cannot be trusted even when it happens to parse: the outcome field
        // may have been cut mid-token.
        Assert.Equal(
            "invalid_finish_reason",
            TypedMemEvalVerdict.Parse("""{"outcome":"correct","reasoning":"x"}""", "length").FailureCode);
        Assert.Equal(
            "content_filtered",
            TypedMemEvalVerdict.Parse("""{"outcome":"correct","reasoning":"x"}""", "content_filter").FailureCode);
    }

    [Fact]
    public void Verdict_RecoversJsonFromFencedOrPaddedResponses()
    {
        var fenced = TypedMemEvalVerdict.Parse(
            "Here is my verdict:\n```json\n{\"outcome\":\"missed\",\"reasoning\":\"denied it\"}\n```\nHope that helps.");

        Assert.Equal(TypedMemEvalOutcome.Missed, fenced.Outcome);
        Assert.Equal("denied it", fenced.Reasoning);
    }

    [Fact]
    public void Verdict_ReadsTheShapeSpecificFields()
    {
        var forgetting = TypedMemEvalVerdict.Parse(
            """{"outcome":"wrong","reasoning":"x","stale_value_asserted":true}""");
        Assert.True(forgetting.StaleValueAsserted);

        var listOrder = TypedMemEvalVerdict.Parse(
            """{"outcome":"wrong","reasoning":"x","ordered_pairs_correct":2,"ordered_pairs_total":3}""");
        Assert.Equal(2, listOrder.OrderedPairsCorrect);
        Assert.Equal(3, listOrder.OrderedPairsTotal);
    }

    [Fact]
    public void JudgeContract_IsChosenByShapeNotJustVertical()
    {
        // Asking for an ordering tally on a question with nothing to order invites the model to
        // invent one.
        var listOrder = Extension("episodic", "list-order");
        var assistantStated = Extension("episodic", "assistant-stated");
        var invalidated = Extension("forgetting", "invalidated");

        Assert.Equal(
            TypedMemEvalVerdict.Kind.ListOrder,
            TypedMemEvalJudge.SelectKind(TypedMemEvalVertical.Episodic, listOrder));
        Assert.Equal(
            TypedMemEvalVerdict.Kind.Base,
            TypedMemEvalJudge.SelectKind(TypedMemEvalVertical.Episodic, assistantStated));
        Assert.Equal(
            TypedMemEvalVerdict.Kind.Forgetting,
            TypedMemEvalJudge.SelectKind(TypedMemEvalVertical.Forgetting, invalidated));
    }

    [Fact]
    public void JudgeFingerprint_IsStableAndDisjointFromLongMemEvals()
    {
        // Two fingerprints that could collide would make judge drift in one benchmark invisible in
        // the other. The value is also stable across calls, or every run would look like a change.
        var first = TypedMemEvalJudge.PromptFingerprint;
        var second = TypedMemEvalJudgePrompts.Fingerprint;

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);

        var longMemEval = LongMemEvalProvenance.Capture(
            RunProvenanceMode.PromptsOnly, null, null, null)!.JudgePromptFingerprint;
        Assert.NotEqual(longMemEval, first);
    }

    [Fact]
    public void JudgePreamble_StatesEveryPrecedenceRuleTheReviewRatified()
    {
        // These are definitions, not style guidance. If one disappears from the template the
        // benchmark quietly starts measuring something else, so their presence is asserted.
        var preamble = TypedMemEvalJudgePrompts.Preamble;

        Assert.Contains("stated value outranks hedging", preamble, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gold itself is a negative", preamble, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("premature", preamble, StringComparison.Ordinal);
        Assert.Contains("abstained", preamble, StringComparison.Ordinal);
        Assert.Contains("missed", preamble, StringComparison.Ordinal);

        var forgetting = TypedMemEvalJudgePrompts.Forgetting("q", "g", "a");
        Assert.Contains("it was a Honda", forgetting, StringComparison.Ordinal);
        Assert.Contains("stale_value_asserted", forgetting, StringComparison.Ordinal);
        Assert.Contains("never recorded", forgetting, StringComparison.Ordinal);

        var arithmetic = TypedMemEvalJudgePrompts.Arithmetic("q", "g", "a", "sum", "1, 2", "3", "USD");
        Assert.Contains("rounded value is correct if and only if", arithmetic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSet_BandsAcrossComparableRunsAndCountsFlips()
    {
        var first = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 5);
        var second = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 5);

        var summary = TypedMemEvalRunSet.Summarize([first, second]);

        Assert.Equal(2, summary.Runs);
        Assert.Equal(5, summary.QuestionsCompared);
        Assert.Equal(0, summary.QuestionsWithFlips);
        Assert.Equal(5, summary.Outcomes[TypedMemEvalOutcome.Correct].Minimum);
        Assert.Equal(0, summary.Outcomes[TypedMemEvalOutcome.Correct].Width);

        // Two runs can agree by coincidence and band to zero width, which reads as perfect
        // stability and is not. The caller is told which it has.
        Assert.True(summary.AtMinimumRunCount);
    }

    [Fact]
    public async Task RunSet_SeesAFlipWhenTheOutcomeChanges()
    {
        var first = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 4);
        var second = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 4, "wrong");

        var summary = TypedMemEvalRunSet.Summarize([first, second]);

        Assert.Equal(4, summary.QuestionsWithFlips);
        Assert.Equal(4, summary.FlippedQuestionIds.Count);
        Assert.Equal(0, summary.Outcomes[TypedMemEvalOutcome.Correct].Minimum);
        Assert.Equal(4, summary.Outcomes[TypedMemEvalOutcome.Correct].Maximum);
    }

    [Fact]
    public async Task RunSet_RefusesRunsOfDifferentVerticals()
    {
        // Refusing is the feature. Banding non-comparable runs manufactures stability out of a
        // configuration change.
        var episodic = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);
        var forgetting = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Forgetting, 3);

        var error = Assert.Throws<TypedMemEvalRunSetMismatchException>(
            () => TypedMemEvalRunSet.Summarize([episodic, forgetting]));

        Assert.Equal("vertical", error.Dimension);
    }

    [Fact]
    public async Task RunSet_RefusesRunsWithDifferentConfigurations()
    {
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var seeded = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 4, RandomSeed = 7 });
        var reseeded = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 4, RandomSeed = 8 });

        var error = Assert.Throws<TypedMemEvalRunSetMismatchException>(
            () => TypedMemEvalRunSet.Summarize([seeded, reseeded]));

        Assert.Equal("options fingerprint", error.Dimension);
    }

    [Fact]
    public async Task RunSet_RefusesASingleRun()
    {
        var only = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);

        var error = Assert.Throws<ArgumentException>(() => TypedMemEvalRunSet.Summarize([only]));
        Assert.Contains("at least two runs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSet_RefusesResultsThatAreNotFamilyResults()
    {
        var typed = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);
        var foreign = new ExternalBenchmarkResult
        {
            BenchmarkId = "something-else",
            BenchmarkName = "Something Else",
            OverallAccuracy = 50,
            TaskAveragedAccuracy = 50,
            PerTypeResults = [],
            QuestionResults = [],
            Duration = TimeSpan.Zero,
            Options = new ExternalBenchmarkOptions()
        };

        var error = Assert.Throws<ArgumentException>(
            () => TypedMemEvalRunSet.Summarize([typed, foreign]));
        Assert.Contains("TypedOutcomes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSet_WarnsWhenAResultSetWouldDoubleCountTheSeededQuestions()
    {
        // The citation rule says the twelve carried questions must not be counted twice. A rule in
        // prose is one nobody's build enforces, so this is the runtime half.
        var prospective = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Prospective, 3);
        var episodic = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);

        Assert.Null(TypedMemEvalRunSet.DetectSeedOverlap([prospective, episodic]));

        var timeGrounded = new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = "time-grounded probe",
            OverallAccuracy = null,
            TaskAveragedAccuracy = null,
            PerTypeResults = [],
            QuestionResults = [],
            Duration = TimeSpan.Zero,
            Provenance = new BenchmarkRunProvenance
            {
                Mode = RunProvenanceMode.Full,
                DatasetIdentifier = TypedMemEvalRunSet.TimeGroundedCorpusId
            },
            Options = new ExternalBenchmarkOptions()
        };

        var warning = TypedMemEvalRunSet.DetectSeedOverlap([prospective, timeGrounded]);
        Assert.NotNull(warning);
        Assert.Contains("double-counts", warning!, StringComparison.Ordinal);
        Assert.Contains("SeededFrom", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_ProjectsTheTypedVectorAndCarriesTheCitationRule()
    {
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Forgetting, 6);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result, judgeModel: "test-judge");

        Assert.Equal("typedmemeval.forgetting", eval.Metric.Key);
        Assert.Contains("outcome.correct", eval.Details.Dimensions!.Keys);
        Assert.Contains("outcome.missed", eval.Details.Dimensions.Keys);
        Assert.Contains("attribution.observedShare", eval.Details.Dimensions.Keys);
        Assert.Contains(
            TypedMemEvalEvalResultAdapter.CitationRule,
            eval.Details.Recommendations!);

        // The projection must not smuggle the other benchmark's name into a report — except inside
        // the citation rule itself, which has to name LongMemEval in order to disclaim it. Asserted
        // as "only there" rather than "nowhere", so the exception cannot widen unnoticed.
        var json = System.Text.Json.JsonSerializer.Serialize(eval);
        var withoutRule = json.Replace(
            System.Text.Json.JsonSerializer.Serialize(TypedMemEvalEvalResultAdapter.CitationRule).Trim('"'),
            "",
            StringComparison.Ordinal);
        Assert.DoesNotContain("longmemeval", withoutRule, StringComparison.OrdinalIgnoreCase);
    }

    private static TypedMemEvalExtension Extension(string vertical, string shape) => new()
    {
        Vertical = vertical,
        Shape = shape,
        GoldSessionIndices = [0],
        SessionIds = ["s000"]
    };
}
