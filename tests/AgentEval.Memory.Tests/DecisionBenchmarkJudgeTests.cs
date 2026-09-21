// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Decisions;
using AgentEval.Memory.External;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The decision-model adapter for the memory judge seam. Every behaviour here is one the memory benchmarks
/// already depend on: an abstention is not a wrong answer, a transport failure is not a verdict, and the
/// probability survives the verdict it was collapsed into.
/// </summary>
public class DecisionBenchmarkJudgeTests
{
    private static ExternalBenchmarkQuestion Question() => new()
    {
        QuestionId = "q1",
        QuestionType = "single-session-user",
        Question = "When did I book the dentist?",
        GoldAnswer = "3 May 2026",
    };

    private sealed class FixedClient(double probability) : IDecisionClient
    {
        public int Calls { get; private set; }
        public DecisionRequest? Last { get; private set; }

        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = request;
            return Task.FromResult(new DecisionResponse(
                "jev-1.13.0",
                new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal) { ["contains_gold"] = new BinaryAnswer(probability) },
                new DecisionUsage(100, 5)));
        }
    }

    private sealed class ThrowingClient : IDecisionClient
    {
        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            throw new DecisionClientException(DecisionFailureKind.ProviderUnavailable, "429 from the provider", statusCode: 429);
    }

    [Theory]
    [InlineData(0.97, true)]
    [InlineData(0.50, true)]    // the boundary is inclusive
    [InlineData(0.49, false)]
    [InlineData(0.01, false)]
    public async Task Probability_MapsToTheVerdict_AtTheThreshold(double probability, bool expectedCorrect)
    {
        var judge = new DecisionBenchmarkJudge(new FixedClient(probability), "jev-latest");

        var result = await judge.JudgeAsync("You booked it on 3 May 2026.", Question());

        Assert.Equal(expectedCorrect, result.Correct);
        Assert.Equal(expectedCorrect ? JudgeOutcomeStatus.Yes : JudgeOutcomeStatus.No, result.Status);
        Assert.Equal(probability * 100.0, result.RawScore);   // the number survives the verdict
        Assert.Equal("jev-1.13.0", judge.LastEchoedModel);    // provenance names the build, not the alias
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public async Task EmptyResponse_IsWrong_AndStillSpendsNothing(string response)
    {
        // An empty AGENT response is wrong under both rubrics: it cannot contain the gold answer, and it
        // does not RECOGNISE that it cannot answer. Returning Empty/null here put the case outside the
        // accuracy denominator — the scorers count Correct.HasValue — so an agent that said nothing was
        // excused rather than scored, which flatters exactly the failure it hides.
        var client = new FixedClient(0.99);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");

        var result = await judge.JudgeAsync(response, Question());

        Assert.Equal(JudgeOutcomeStatus.No, result.Status);
        Assert.False(result.Correct);
        Assert.Equal(0.0, result.RawScore);
        Assert.Equal(0, client.Calls);        // still no provider call
        Assert.Equal(0, result.LlmCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyResponse_ToAnAbstentionQuestion_IsAlsoWrong(string response)
    {
        // The reason this was got wrong the first time: it looks as though silence should count as an
        // abstention. It does not. The rubric asks whether the response RECOGNISES that it cannot answer,
        // and an empty string recognises nothing.
        var client = new FixedClient(0.99);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");
        var abstention = new ExternalBenchmarkQuestion
        {
            QuestionId = "q1_abs",
            QuestionType = "single-session-user",
            Question = "What did I say?",
            GoldAnswer = "The conversation does not contain this.",
            IsAbstention = true,
        };

        var result = await judge.JudgeAsync(response, abstention);

        Assert.Equal(JudgeOutcomeStatus.No, result.Status);
        Assert.False(result.Correct);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task TransportFailure_Propagates_AndIsNeverAVerdict()
    {
        var judge = new DecisionBenchmarkJudge(new ThrowingClient(), "jev-latest");

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() => judge.JudgeAsync("some answer", Question()));

        Assert.Equal(DecisionFailureKind.ProviderUnavailable, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task TheStateCarriesTheQuestion_TheGoldAnswer_AndTheResponse()
    {
        // A judge asked "does this contain the gold answer?" without the gold answer grades plausibility.
        var client = new FixedClient(0.9);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");

        await judge.JudgeAsync("You booked it on 3 May 2026.", Question());

        var state = Assert.IsType<string>(client.Last!.State);
        Assert.Contains("When did I book the dentist?", state, StringComparison.Ordinal);
        Assert.Contains("3 May 2026", state, StringComparison.Ordinal);
        Assert.Contains("You booked it on 3 May 2026.", state, StringComparison.Ordinal);
        Assert.Single(client.Last.Questions);   // one question per item, one request per judgement
    }

    [Fact]
    public void ThresholdOutsideTheUnitInterval_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionBenchmarkJudge(new FixedClient(0.5), "m", threshold: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionBenchmarkJudge(new FixedClient(0.5), "m", threshold: double.NaN));
    }

    [Fact]
    public async Task ACustomThreshold_MovesTheVerdict_WithoutMovingTheScore()
    {
        // X1 showed the bar is where the error preference is written; it has to be settable without the
        // recorded number changing underneath it.
        var strict = new DecisionBenchmarkJudge(new FixedClient(0.80), "jev-latest", threshold: 0.90);
        var lenient = new DecisionBenchmarkJudge(new FixedClient(0.80), "jev-latest", threshold: 0.70);

        var strictResult = await strict.JudgeAsync("answer", Question());
        var lenientResult = await lenient.JudgeAsync("answer", Question());

        Assert.False(strictResult.Correct);
        Assert.True(lenientResult.Correct);
        Assert.Equal(strictResult.RawScore, lenientResult.RawScore);
    }
    // ── An abstention question is a different question ───────────────────────────────────

    private static ExternalBenchmarkQuestion AbstentionQuestion() => new()
    {
        QuestionId = "q1_abs",
        QuestionType = "multi-session",
        Question = "What did my dentist say about the implant?",
        GoldAnswer = "The conversation does not contain this information.",
        IsAbstention = true,
    };

    [Fact]
    public async Task AbstentionQuestion_AsksWhetherTheResponseRecognisesItCannotAnswer()
    {
        // The ordinary rubric says in so many words that a refusal is not a match. Sending it at a question
        // whose correct behaviour IS to refuse would score every right answer wrong — which is why the
        // shipped LongMemEvalJudge branches on the same flag.
        var client = new FixedClient(0.95);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");

        var result = await judge.JudgeAsync("I don't have that in our conversations.", AbstentionQuestion());

        var asked = Assert.IsType<BinaryQuestion>(client.Last!.Questions["contains_gold"]);
        Assert.Equal(DecisionBenchmarkJudge.AbstentionInstructions, asked.Instructions);
        Assert.DoesNotContain("refusal to answer is not a match", asked.Instructions, StringComparison.Ordinal);
        Assert.True(result.Correct);
        Assert.Contains("recognises it cannot answer", result.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryQuestion_StillGetsTheGoldAnswerRubric()
    {
        var client = new FixedClient(0.95);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");

        await judge.JudgeAsync("You booked it on 3 May 2026.", Question());

        var asked = Assert.IsType<BinaryQuestion>(client.Last!.Questions["contains_gold"]);
        Assert.Equal(DecisionBenchmarkJudge.Instructions, asked.Instructions);
    }

    // ── Each question type gets the rubric the shipped judge uses ───────────────────────

    [Theory]
    [InlineData("single-session-user", false)]
    [InlineData("multi-session", false)]
    [InlineData("single-session-assistant", false)]
    public void OrdinaryTypes_GetTheStrictGoldRubric(string type, bool _)
    {
        var q = new ExternalBenchmarkQuestion { QuestionId = "q", QuestionType = type, Question = "?", GoldAnswer = "a" };
        Assert.Equal(DecisionBenchmarkJudge.Instructions, DecisionBenchmarkJudge.InstructionsFor(q));
    }

    [Fact]
    public void PreferenceQuestions_AreJudgedAgainstARubric_NotAnExactAnswer()
    {
        // GOLD is a description of a good personalised answer. Matching it literally would fail correct
        // responses that recall the user's preference in their own words.
        var q = new ExternalBenchmarkQuestion { QuestionId = "q", QuestionType = "single-session-preference", Question = "?", GoldAnswer = "rubric" };
        Assert.Equal(DecisionBenchmarkJudge.PreferenceInstructions, DecisionBenchmarkJudge.InstructionsFor(q));
        Assert.Contains("RUBRIC", DecisionBenchmarkJudge.PreferenceInstructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("temporal-reasoning")]
    // The real ids, taken from the corpus that defines them rather than guessed: a question of a type
    // spelled wrongly here falls through to the strict rubric and silently loses the off-by-one tolerance.
    [InlineData("temporal-as-of")]
    [InlineData("temporal-current")]
    [InlineData("prospective-memory")]
    public void TimeGroundedQuestions_ToleratetheOffByOneTheShippedJudgeTolerates(string type)
    {
        var q = new ExternalBenchmarkQuestion { QuestionId = "q", QuestionType = type, Question = "?", GoldAnswer = "18 days" };
        Assert.Equal(DecisionBenchmarkJudge.TemporalInstructions, DecisionBenchmarkJudge.InstructionsFor(q));
        Assert.Contains("off-by-one", DecisionBenchmarkJudge.TemporalInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeUpdateQuestions_AcceptTheOldValueAlongsideTheCurrentOne()
    {
        var q = new ExternalBenchmarkQuestion { QuestionId = "q", QuestionType = "knowledge-update", Question = "?", GoldAnswer = "now blue" };
        Assert.Equal(DecisionBenchmarkJudge.KnowledgeUpdateInstructions, DecisionBenchmarkJudge.InstructionsFor(q));
    }

    [Fact]
    public void Abstention_WinsOverTheQuestionType_BecauseItIsCarriedByTheId()
    {
        var q = new ExternalBenchmarkQuestion { QuestionId = "q_abs", QuestionType = "knowledge-update", Question = "?", GoldAnswer = "n/a", IsAbstention = true };
        Assert.Equal(DecisionBenchmarkJudge.AbstentionInstructions, DecisionBenchmarkJudge.InstructionsFor(q));
    }

    [Fact]
    public async Task TheRubricChosenForATypeIsTheOneActuallySent()
    {
        var client = new FixedClient(0.9);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");
        var q = new ExternalBenchmarkQuestion { QuestionId = "q", QuestionType = "temporal-reasoning", Question = "?", GoldAnswer = "18 days" };

        await judge.JudgeAsync("about 19 days", q);

        var asked = Assert.IsType<BinaryQuestion>(client.Last!.Questions["contains_gold"]);
        Assert.Equal(DecisionBenchmarkJudge.TemporalInstructions, asked.Instructions);
    }

    [Fact]
    public void TheTimeGroundedTypeNames_ComeFromTheCorpusThatDefinesThem()
    {
        // This pins the names against their source, so a rename in the corpus cannot leave the adapter
        // quietly judging those questions with the strict rubric.
        foreach (var type in new[]
                 {
                     LongMemEvalTimeGroundedCorpus.AsOfQuestionType,
                     LongMemEvalTimeGroundedCorpus.CurrentQuestionType,
                     LongMemEvalTimeGroundedCorpus.ProspectiveQuestionType,
                 })
        {
            var q = new ExternalBenchmarkQuestion { QuestionId = "q", QuestionType = type, Question = "?", GoldAnswer = "18 days" };
            Assert.Equal(DecisionBenchmarkJudge.TemporalInstructions, DecisionBenchmarkJudge.InstructionsFor(q));
        }
    }

    private sealed class HugeUsageClient : IDecisionClient
    {
        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DecisionResponse(
                "jev-1.13.0",
                new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal) { ["contains_gold"] = new BinaryAnswer(0.9) },
                new DecisionUsage(long.MaxValue - 5, 10)));
    }

    [Fact]
    public async Task TokenSumThatWouldOverflow_SaturatesAtIntMax_NeverNegative()
    {
        // DecisionUsage allows each count up to long.MaxValue, so a plain addition wraps negative before any
        // clamp sees it — the same defect DecisionEval already carries a saturating sum for.
        var judge = new DecisionBenchmarkJudge(new HugeUsageClient(), "jev-latest");

        var result = await judge.JudgeAsync("an answer", Question());

        Assert.Equal(int.MaxValue, result.TokensUsed);
    }

}
