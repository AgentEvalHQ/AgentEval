// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Decisions;
using AgentEval.Memory.External;
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
    public async Task EmptyResponse_IsEmpty_NotWrong_AndSpendsNothing(string response)
    {
        // A judge that scores silence as "No" converts an abstention into an incorrect answer, which is the
        // distinction the typed outcome vector exists to preserve.
        var client = new FixedClient(0.99);
        var judge = new DecisionBenchmarkJudge(client, "jev-latest");

        var result = await judge.JudgeAsync(response, Question());

        Assert.Equal(JudgeOutcomeStatus.Empty, result.Status);
        Assert.Null(result.Correct);
        Assert.Null(result.RawScore);
        Assert.Equal(0, client.Calls);        // no provider call was made
        Assert.Equal(0, result.LlmCallCount);
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

}
