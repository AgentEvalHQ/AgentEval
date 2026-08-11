// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

using static AgentEval.Memory.Tests.LongMemEvalStructuredJudgeTests;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Tests for <see cref="JudgeDecompositionMode.PerPredicate"/>: per-predicate outcomes are reported, the
/// combination rule is explicit and recorded, and the provider-call multiplier is measured rather than
/// assumed.
/// </summary>
public class LongMemEvalPredicateJudgeTests(ITestOutputHelper output)
{
    private const string TwoFactGoldAnswer =
        "The user adopted a rescue terrier named Biscuit. The user moved to Lisbon in March.";

    [Fact]
    public async Task PerPredicate_ReportsEachPredicateSeparately_NotOnlyTheCombinedVerdict()
    {
        var judge = CreateJudge(Response("yes"), Response("no"));

        var result = await judge.JudgeAsync(
            "The user adopted a terrier called Biscuit.",
            Question(goldAnswer: TwoFactGoldAnswer),
            PerPredicateOptions());

        Assert.NotNull(result.PredicateResults);
        Assert.Equal(2, result.PredicateResults!.Count);

        Assert.Equal(0, result.PredicateResults[0].Index);
        Assert.Equal("The user adopted a rescue terrier named Biscuit", result.PredicateResults[0].Predicate);
        Assert.Equal(JudgeOutcomeStatus.Yes, result.PredicateResults[0].Status);

        Assert.Equal(1, result.PredicateResults[1].Index);
        Assert.Equal("The user moved to Lisbon in March", result.PredicateResults[1].Predicate);
        Assert.Equal(JudgeOutcomeStatus.No, result.PredicateResults[1].Status);

        // The value of decomposition is knowing WHICH claim failed; the combined verdict alone is what a
        // single-judge run already reported.
        Assert.Equal(JudgeOutcomeStatus.No, result.Status);
    }

    [Fact]
    public async Task PerPredicate_RecordsTheCombinationRuleOnTheResult()
    {
        var judge = CreateJudge(Response("yes"), Response("yes"));

        var result = await judge.JudgeAsync(
            "response", Question(goldAnswer: TwoFactGoldAnswer), PerPredicateOptions());

        // Visible on the result, not implied by the caller remembering how it was configured.
        Assert.Equal(PredicateCombinationRule.AllMustHold, result.PredicateCombinationRule);
        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
    }

    [Fact]
    public async Task PerPredicate_AllMustHold_OneUnknownIsInconclusiveNotCorrect()
    {
        var judge = CreateJudge(Response("yes"), Response("gibberish"));

        var result = await judge.JudgeAsync(
            "response", Question(goldAnswer: TwoFactGoldAnswer), PerPredicateOptions());

        // "All held except the one we could not read" is not "all held".
        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
        Assert.Equal("predicate_inconclusive", result.SafeFailureCode);
    }

    [Fact]
    public async Task PerPredicate_AllMustHold_DefiniteNoBeatsAnUnknown()
    {
        var judge = CreateJudge(Response("no"), Response("gibberish"));

        var result = await judge.JudgeAsync(
            "response", Question(goldAnswer: TwoFactGoldAnswer), PerPredicateOptions());

        // A definite failure already violates all-must-hold; the unknown cannot rescue it.
        Assert.Equal(JudgeOutcomeStatus.No, result.Status);
        Assert.False(result.Correct);
    }

    [Theory]
    // yes, no, no under Majority: yes can never reach 2 of 3, so the verdict is decidable as No.
    [InlineData("yes", "no", "no", JudgeOutcomeStatus.No)]
    [InlineData("yes", "yes", "no", JudgeOutcomeStatus.Yes)]
    public async Task PerPredicate_MajorityRule_DecidesOnReachability(
        string first, string second, string third, JudgeOutcomeStatus expected)
    {
        var judge = CreateJudge(Response(first), Response(second), Response(third));

        var result = await judge.JudgeAsync(
            "response",
            Question(goldAnswer:
                "The user adopted a rescue terrier named Biscuit. "
                + "The user moved to Lisbon in March. "
                + "The user started learning Portuguese."),
            new ExternalBenchmarkOptions
            {
                JudgeDecompositionMode = JudgeDecompositionMode.PerPredicate,
                PredicateCombinationRule = PredicateCombinationRule.Majority,
                MaxJudgeRetries = 0
            });

        Assert.Equal(expected, result.Status);
        Assert.Equal(PredicateCombinationRule.Majority, result.PredicateCombinationRule);
    }

    [Fact]
    public async Task PerPredicate_SumsProviderCallsAndTokensAcrossPredicates()
    {
        var judge = new LongMemEvalJudge(
            new RecordingChatClient(
                WithUsage("yes", 40),
                WithUsage("yes", 60)),
            NullLogger<LongMemEvalJudge>.Instance);

        var result = await judge.JudgeAsync(
            "response", Question(goldAnswer: TwoFactGoldAnswer), PerPredicateOptions());

        Assert.Equal(2, result.LlmCallCount);
        Assert.Equal(100, result.TokensUsed);
        Assert.Equal(40, result.PredicateResults![0].TokensUsed);
        Assert.Equal(60, result.PredicateResults[1].TokensUsed);
    }

    [Fact]
    public async Task PerPredicate_SingleFactGoldAnswer_CostsExactlyOneCall()
    {
        var client = new RecordingChatClient(Response("yes"));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        var result = await judge.JudgeAsync(
            "response", Question(goldAnswer: "The user prefers window seats"), PerPredicateOptions());

        // No multiplier at all when the gold answer carries one claim — the common case in LongMemEval.
        Assert.Equal(1, client.CallCount);
        Assert.Single(result.PredicateResults!);
    }

    /// <summary>
    /// Real LongMemEval gold answers, verbatim, that MUST NOT decompose. Each is a shape that a naive
    /// sentence split mangles; every one was found by running the extractor over the real datasets
    /// rather than imagined.
    /// </summary>
    public static TheoryData<string, string> RealAnswersThatMustNotDecompose() => new()
    {
        {
            "7 days. 8 days (including the last day) is also acceptable.",
            "alternatives, not conjoined facts — requiring both fails a response that gave the primary answer"
        },
        {
            "One week. Answers ranging from 7 days to 10 days are also acceptable.",
            "alternatives expressed as a range"
        },
        {
            "The order of the concerts I attended is: 1. Billie Eilish concert at the Wells Fargo Center "
            + "in Philly, 2. Free outdoor concert series in the park, 3. Music festival in Brooklyn, "
            + "4. Jazz night at a local bar, 5. Queen + Adam Lambert concert at the Prudential Center in "
            + "Newark, NJ.",
            "enumerated list — splitting on the ordinals leaves fragments with a dangling number"
        },
        { "5.5 weeks", "decimal point is not a sentence terminator" },
        { "bike", "single token" },
        { "Samsung Galaxy S22", "single noun phrase ending in a digit" },
        { "GPS system not functioning correctly", "single clause" },
        { "4 days.", "terse primary answer" },
    };

    [Theory]
    [MemberData(nameof(RealAnswersThatMustNotDecompose))]
    public void PredicateExtractor_RealAnswersWithUnsafeSplits_AreJudgedWhole(string goldAnswer, string why)
    {
        var predicates = LongMemEvalPredicateExtractor.Extract(goldAnswer);

        Assert.True(
            predicates.Count == 1,
            $"expected a single predicate ({why}) but got {predicates.Count}: "
            + string.Join(" | ", predicates));
        Assert.Equal(goldAnswer.Trim(), predicates[0]);
    }

    /// <summary>
    /// Real LongMemEval gold answers, verbatim, that genuinely carry multiple conjoined facts. These are
    /// the entire population that decomposes: 6 of the 500 answers in each shipped dataset.
    /// </summary>
    public static TheoryData<string, int> RealAnswersThatDecompose() => new()
    {
        { "I attended three weddings. The couples were Rachel and Mike, Emily and Sarah, and Jen and Tom.", 2 },
        {
            "When you just started your new role as Senior Software Engineer, you led 4 engineers. "
            + "Now, you lead 5 engineers",
            2
        },
        {
            "Previously, you play tennis with your friends at the local park every week (on Sunday). "
            + "Currently, you play tennis every other week (on Sunday).",
            2
        },
        {
            "First, I used a Buy One Get One Free coupon on Luvs diapers at Walmart. Then, I redeemed "
            + "$12 cashback for a $10 Amazon gift card from Ibotta. Finally, I signed up for the rewards "
            + "program at ShopRite.",
            3
        },
    };

    [Theory]
    [MemberData(nameof(RealAnswersThatDecompose))]
    public void PredicateExtractor_RealMultiFactAnswers_DecomposeToTheExpectedCount(
        string goldAnswer,
        int expected)
    {
        Assert.Equal(expected, LongMemEvalPredicateExtractor.Extract(goldAnswer).Count);
    }

    /// <summary>
    /// Pins the provider-call multiplier over the real corpus above, so a change to the extractor cannot
    /// quietly multiply a consumer's judge bill.
    /// </summary>
    /// <remarks>
    /// Measured over both shipped 500-question datasets on 2026-08-11 with the production extractor:
    /// 507 calls against 500 questions, a <b>1.0140x</b> multiplier, with 6 answers (1.20%) decomposing
    /// and at most 3 predicates on any one. LongMemEval gold answers are overwhelmingly single facts, so
    /// decomposition barely engages on this benchmark — see the report accompanying this change.
    /// </remarks>
    [Fact]
    public void PerPredicate_CallMultiplier_OverTheRealCorpus_StaysNearOne()
    {
        var corpus = RealAnswersThatMustNotDecompose().Select(row => (string)row[0])
            .Concat(RealAnswersThatDecompose().Select(row => (string)row[0]))
            .ToList();

        var counts = corpus.Select(a => LongMemEvalPredicateExtractor.Extract(a).Count).ToList();
        var multiplier = (double)counts.Sum() / counts.Count;

        output.WriteLine($"corpus answers        : {counts.Count}");
        output.WriteLine($"judge calls, None     : {counts.Count}");
        output.WriteLine($"judge calls, PerPred  : {counts.Sum()}");
        output.WriteLine($"multiplier            : {multiplier:F4}x");
        output.WriteLine($"max predicates on one : {counts.Max()}");

        Assert.True(counts.Max() <= LongMemEvalPredicateExtractor.MaximumPredicates);
        // 8 whole + (2+2+2+3) = 17 calls over 12 answers.
        Assert.Equal(17, counts.Sum());
    }

    [Fact]
    public void PredicateExtractor_BlankGoldAnswer_YieldsNothingRatherThanAVacuousPass()
    {
        Assert.Empty(LongMemEvalPredicateExtractor.Extract("   "));
    }

    [Fact]
    public async Task PerPredicate_AbstentionQuestion_IsJudgedWholeNotDecomposed()
    {
        var client = new RecordingChatClient(Response("yes"));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        var result = await judge.JudgeAsync(
            "I don't have that information.",
            new ExternalBenchmarkQuestion
            {
                QuestionId = "q-1_abs",
                QuestionType = "single-session-user",
                Question = "When did I mention my hamster?",
                // A real abstention gold answer: two sentences, so it WOULD decompose on shape alone.
                GoldAnswer = "You did not mention this information. You mentioned your cat Luna but not your hamster.",
                IsAbstention = true
            },
            PerPredicateOptions());

        // The abstention judge asks whether the model RECOGNISED the question as unanswerable, which is
        // a different question from "does the response support this fact?". Decomposing it would score
        // something the benchmark never asked.
        Assert.Null(result.PredicateResults);
        Assert.Null(result.PredicateCombinationRule);
        Assert.Equal(1, client.CallCount);
        Assert.Contains("unanswerable question, an explanation", client.LastPrompt!, StringComparison.Ordinal);
    }

    [Fact]
    public void PredicateExtractor_IsCappedSoOneQuestionCannotFanOutWithoutBound()
    {
        var verbose = string.Join(" ", Enumerable.Range(0, 20)
            .Select(i => $"The user completed milestone number {i} on schedule."));

        var predicates = LongMemEvalPredicateExtractor.Extract(verbose);

        Assert.Equal(LongMemEvalPredicateExtractor.MaximumPredicates, predicates.Count);
    }

    private static Func<ChatResponse> WithUsage(string text, int tokens)
        => () => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { TotalTokenCount = tokens }
        };

    private static ExternalBenchmarkOptions PerPredicateOptions() => new()
    {
        JudgeDecompositionMode = JudgeDecompositionMode.PerPredicate,
        MaxJudgeRetries = 0
    };
}
