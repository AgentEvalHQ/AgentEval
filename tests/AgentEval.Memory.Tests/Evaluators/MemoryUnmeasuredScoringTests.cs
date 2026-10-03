// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using AgentEval.Memory.Tests.Engine;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

/// <summary>
/// A question the judge produced no score for is not a score. Through 0.42 a failed judge call scored 0 and a reply
/// with no score scored 50, and both flowed into every average and the overall grade.
/// </summary>
public sealed class MemoryUnmeasuredScoringTests
{
    private static MemoryQueryResult Query(double score, bool measured) => new()
    {
        Query = MemoryQuery.Create("q?", MemoryFact.Create("fact")),
        Response = "r",
        Score = score,
        Measured = measured,
        FoundFacts = [],
        MissingFacts = [],
        ForbiddenFound = [],
    };

    [Fact]
    public void AScenario_LeavesUnmeasuredQuestionsOutOfItsRates_AndCountsThem()
    {
        var result = new MemoryEvaluationResult
        {
            OverallScore = 90,
            QueryResults = [Query(90, measured: true), Query(0, measured: false)],
            FoundFacts = [],
            MissingFacts = [],
            ForbiddenFound = [],
            Duration = TimeSpan.Zero,
            ScenarioName = "s",
        };

        Assert.Equal(1, result.UnmeasuredQueries);
        Assert.Equal(90, result.RetentionRate);
        Assert.Equal(100, result.SuccessRate);
        Assert.True(result.IsMeasured);
        Assert.False(result.QueryResults[1].Passed);
    }

    [Fact]
    public void ABenchmark_WithUnscoredQuestionsOrACrash_IsNotComplete()
    {
        static BenchmarkCategoryResult Category(bool errored, int unmeasured) => new()
        {
            CategoryName = "c",
            Score = 80,
            Weight = 1,
            ScenarioType = BenchmarkScenarioType.BasicRetention,
            Duration = TimeSpan.Zero,
            Skipped = errored,
            Errored = errored,
            UnmeasuredQueries = unmeasured,
        };

        static MemoryBenchmarkResult Result(params BenchmarkCategoryResult[] categories) => new()
        {
            BenchmarkName = "b",
            CategoryResults = categories,
            Duration = TimeSpan.Zero,
        };

        Assert.True(Result(Category(false, 0)).IsComplete);
        Assert.False(Result(Category(false, 2)).IsComplete);
        Assert.False(Result(Category(true, 0)).IsComplete);

        // An incomplete run never passes, however high the part that was measured.
        Assert.True(Result(Category(false, 0)).Passed);
        Assert.False(Result(Category(false, 2)).Passed);
    }

    private static MemoryBenchmarkRunner RunnerJudgedBy(IChatClient judgeClient)
    {
        var judge = new MemoryJudge(judgeClient, NullLogger<MemoryJudge>.Instance);
        var testRunner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        return new MemoryBenchmarkRunner(
            testRunner, judge,
            new ReachBackEvaluator(testRunner, judge, NullLogger<ReachBackEvaluator>.Instance),
            new ReducerEvaluator(testRunner, NullLogger<ReducerEvaluator>.Instance),
            new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance),
            new MemoryScenarios(), new ChattyConversationScenarios(), new TemporalMemoryScenarios(),
            NullLogger<MemoryBenchmarkRunner>.Instance);
    }

    [Fact]
    public async Task AJudgeThatNeverScores_LeavesEveryCategoryUnmeasured_NotFiftyPercent()
    {
        // Through 0.42 this run reported every category at 50%: the default for a reply with no score.
        var runner = RunnerJudgedBy(new CustomResponseChatClient("The response was adequate."));

        var result = await runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Quick);

        Assert.False(result.IsComplete);
        Assert.True(result.UnmeasuredQueries > 0);
        Assert.All(result.CategoryResults.Where(c => !c.Skipped || c.Errored), c =>
        {
            Assert.True(c.Errored, $"{c.CategoryName} should be not-measured.");
            Assert.Contains("Not measured", c.SkipReason);
        });
    }

    [Fact]
    public async Task AJudgeThatFails_IsNotScoredAsZero_ButReportedAsUnmeasured()
    {
        var runner = RunnerJudgedBy(new ThrowingChatClient());

        var result = await runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Quick);

        Assert.False(result.IsComplete);
        Assert.False(result.Passed);
        Assert.True(result.UnmeasuredQueries > 0);
        Assert.All(result.CategoryResults.Where(c => !c.Skipped || c.Errored), c => Assert.True(c.Errored));
    }

    [Fact]
    public async Task OnTheFullPreset_EveryJudgedCategory_IncludingTheReducer_IsUnmeasured()
    {
        // The reducer and reach-back categories run their questions through the same judge; through 0.42 the reducer
        // recorded its judged score as if it were computed in code, so its unscored questions counted as lost facts.
        var runner = RunnerJudgedBy(new CustomResponseChatClient("The response was adequate."));

        var result = await runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Full);

        var ran = result.CategoryResults.Where(c => !c.Skipped || c.Errored).ToList();
        Assert.Contains(ran, c => c.ScenarioType == BenchmarkScenarioType.ReducerFidelity);
        Assert.Contains(ran, c => c.ScenarioType == BenchmarkScenarioType.ReachBackDepth);
        Assert.All(ran, c => Assert.True(c.Errored, $"{c.CategoryName} should be not-measured."));
        Assert.False(result.IsComplete);
    }

    /// <summary>Scores every second judge call and gives no score on the others.</summary>
    private sealed class HalfScoringJudge : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var scored = Interlocked.Increment(ref _calls) % 2 == 1;
            var text = scored
                ? "{\"found_facts\":[],\"missing_facts\":[],\"forbidden_found\":[],\"score\":90,\"explanation\":\"ok\"}"
                : "No verdict.";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task AScenarioWithSomeUnscoredQuestions_AveragesOnlyTheScoredOnes()
    {
        var judge = new MemoryJudge(new HalfScoringJudge(), NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        MemoryFact[] facts = [MemoryFact.Create("My name is Ada"), MemoryFact.Create("I live in Basel")];
        var scenario = new MemoryScenarios().CreateBasicMemoryTest(facts,
            [MemoryQuery.Create("What is my name?", facts[0]), MemoryQuery.Create("Where do I live?", facts[1])]);

        var result = await runner.RunAsync(new TestMemoryAgent(), scenario);

        Assert.Equal(1, result.UnmeasuredQueries);
        Assert.Equal(90, result.OverallScore);   // the unscored question is not averaged in as a 0
    }
}
