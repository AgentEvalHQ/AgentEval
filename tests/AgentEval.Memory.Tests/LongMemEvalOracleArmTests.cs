// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Covers the public oracle arm: the ceiling every other arm is read against, and the two controls
/// that move it off the ceiling deliberately.
/// </summary>
public sealed class LongMemEvalOracleArmTests
{
    [Fact]
    public async Task RunOracleAsync_GoldOnly_ReturnsTheUsualResultShapeAndSeesOnlyEvidence()
    {
        using var dataset = Dataset.Create();
        var answerClient = new RecordingChatClient("oracle answer");
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient("yes"), dataset.Path);

        var result = await runner.RunOracleAsync(answerClient, Options(dataset));

        var question = Assert.Single(result.QuestionResults);
        Assert.Equal("q-oracle", question.QuestionId);
        Assert.True(question.Correct);
        Assert.Equal(100, result.OverallAccuracy);
        Assert.NotNull(result.Composition);
        Assert.Equal(1, result.Composition.TotalQuestions);
        Assert.Contains("(oracle)", result.BenchmarkName, StringComparison.Ordinal);

        Assert.NotNull(result.OracleProjection);
        var projection = result.OracleProjection;
        Assert.Equal(2, projection.GoldSessionsAvailable);
        Assert.Equal(2, projection.GoldSessionsKept);
        Assert.Equal(3, projection.DistractorSessionsAvailable);
        Assert.Equal(0, projection.DistractorSessionsAdded);
        Assert.Equal(1.0, projection.RealisedGoldSessionFraction);
        Assert.Equal(2, Assert.Single(projection.ByQuestion).ProjectedSessions);

        var payload = answerClient.LastPayload;
        Assert.Contains("gold one", payload, StringComparison.Ordinal);
        Assert.Contains("gold two", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("distractor", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("gold answer", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("answer-session", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("has_answer", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunOracleAsync_WithDistractors_AddsThemFromTheSameHaystackInDatasetOrder()
    {
        using var dataset = Dataset.Create();
        var answerClient = new RecordingChatClient("oracle answer");
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient("yes"), dataset.Path);

        var result = await runner.RunOracleAsync(
            answerClient,
            Options(dataset),
            new LongMemEvalOracleOptions { DistractorSessions = 2 });

        var projection = result.OracleProjection!;
        Assert.Equal(2, projection.DistractorSessionsAdded);
        Assert.True(projection.DistractorRequestFullyMet);
        Assert.Equal(4, Assert.Single(projection.ByQuestion).ProjectedSessions);

        // Emitted in haystack order, not evidence-first: a layout that always front-loads the gold
        // measures position rather than retrieval.
        var payload = answerClient.LastPayload;
        var positions = new[] { "gold one", "gold two" }
            .Select(marker => payload.IndexOf(marker, StringComparison.Ordinal))
            .ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.True(payload.IndexOf("distractor A", StringComparison.Ordinal) < positions[0]);
    }

    [Fact]
    public async Task RunOracleAsync_MoreDistractorsThanTheHaystackHolds_ReportsTheShortfall()
    {
        using var dataset = Dataset.Create();
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient("yes"), dataset.Path);

        var result = await runner.RunOracleAsync(
            new RecordingChatClient("oracle answer"),
            Options(dataset),
            new LongMemEvalOracleOptions { DistractorSessions = 25 });

        var projection = result.OracleProjection!;
        Assert.Equal(25, projection.RequestedDistractorSessions);
        Assert.Equal(3, projection.DistractorSessionsAdded);
        Assert.False(projection.DistractorRequestFullyMet);
    }

    [Fact]
    public async Task RunOracleAsync_HalfTheEvidence_KeepsHalfAndSaysSo()
    {
        using var dataset = Dataset.Create();
        var answerClient = new RecordingChatClient("oracle answer");
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient("yes"), dataset.Path);

        var result = await runner.RunOracleAsync(
            answerClient,
            Options(dataset),
            new LongMemEvalOracleOptions { GoldSessionFraction = 0.5 });

        var projection = result.OracleProjection!;
        Assert.Equal(2, projection.GoldSessionsAvailable);
        Assert.Equal(1, projection.GoldSessionsKept);
        Assert.Equal(0.5, projection.RealisedGoldSessionFraction);
        Assert.Single(
            new[] { "gold one", "gold two" },
            marker => answerClient.LastPayload.Contains(marker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(0.01, 1)]
    [InlineData(1.0, 1)]
    public void GoldSessionsToKeep_SingleEvidenceSession_NeverRoundsToZero(double fraction, int expected)
    {
        var kept = LongMemEvalOracleProjector.GoldSessionsToKeep(
            available: 1,
            new LongMemEvalOracleOptions { GoldSessionFraction = fraction });

        Assert.Equal(expected, kept);
    }

    [Theory]
    [InlineData(4, 0.5, 2)]
    [InlineData(3, 0.5, 2)]
    [InlineData(4, 0.25, 1)]
    [InlineData(0, 0.5, 0)]
    public void GoldSessionsToKeep_RoundsUpNeverDown(int available, double fraction, int expected)
    {
        var kept = LongMemEvalOracleProjector.GoldSessionsToKeep(
            available,
            new LongMemEvalOracleOptions { GoldSessionFraction = fraction });

        Assert.Equal(expected, kept);
    }

    [Fact]
    public async Task RunOracleAsync_ZeroGoldFraction_FailsBeforeAnyProviderCall()
    {
        using var dataset = Dataset.Create();
        var answerClient = new RecordingChatClient("unused");
        var judge = new RecordingChatClient("unused");
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunOracleAsync(
                answerClient,
                Options(dataset),
                new LongMemEvalOracleOptions { GoldSessionFraction = 0 }));

        Assert.Equal(nameof(LongMemEvalOracleOptions.GoldSessionFraction), error.ParamName);
        Assert.Empty(answerClient.Payloads);
        Assert.Empty(judge.Payloads);
    }

    [Fact]
    public void Project_SameSeed_DrawsTheSameSessionsAndDoesNotDependOnOtherQuestions()
    {
        var entry = Dataset.Entry("q-deterministic");
        var options = new LongMemEvalOracleOptions { DistractorSessions = 2, GoldSessionFraction = 0.5 };

        var first = LongMemEvalOracleProjector.Project(entry, options, randomSeed: 42);
        var second = LongMemEvalOracleProjector.Project(entry, options, randomSeed: 42);
        var otherSeed = LongMemEvalOracleProjector.Project(entry, options, randomSeed: 43);

        Assert.Equal(Contents(first), Contents(second));
        Assert.Equal(3, first.Realised.ProjectedSessions);
        // A different id draws from its own derived stream, so one question's sample cannot shift
        // another's — the property that keeps two overlapping runs comparable question by question.
        Assert.NotEqual(
            LongMemEvalOracleProjector.DeriveSeed(42, "q-deterministic"),
            LongMemEvalOracleProjector.DeriveSeed(42, "q-other"));
        Assert.NotEqual(
            LongMemEvalOracleProjector.DeriveSeed(42, "q-deterministic"),
            LongMemEvalOracleProjector.DeriveSeed(43, "q-deterministic"));
        Assert.Equal(3, otherSeed.Realised.ProjectedSessions);
    }

    [Fact]
    public void Project_LoweringTheEvidenceFraction_KeepsTheSameDistractors()
    {
        var entry = Dataset.Entry("q-controls");

        var full = LongMemEvalOracleProjector.Project(
            entry,
            new LongMemEvalOracleOptions { DistractorSessions = 2 },
            randomSeed: 7);
        var halved = LongMemEvalOracleProjector.Project(
            entry,
            new LongMemEvalOracleOptions { DistractorSessions = 2, GoldSessionFraction = 0.5 },
            randomSeed: 7);

        var fullDistractors = Contents(full).Where(c => c.StartsWith("distractor", StringComparison.Ordinal));
        var halvedDistractors = Contents(halved).Where(c => c.StartsWith("distractor", StringComparison.Ordinal));
        Assert.Equal(fullDistractors, halvedDistractors);
    }

    [Fact]
    public async Task RunOracleAsync_WithAnswerSampling_PinsTheCeilingArmToo()
    {
        using var dataset = Dataset.Create();
        var answerClient = new RecordingChatClient("oracle answer");
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient("yes"), dataset.Path);
        var options = new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            MaxJudgeRetries = 0,
            RandomSeed = 42,
            HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
            AnswerTemperature = 0.1,
            AnswerSeed = 99
        };

        var result = await runner.RunOracleAsync(answerClient, options);

        var sent = Assert.Single(answerClient.Options);
        Assert.Equal(0.1f, sent!.Temperature);
        Assert.Equal(99, sent.Seed);
        Assert.True(result.AnswerSampling!.Temperature.CarriedByEveryQuestion);
        Assert.True(result.AnswerSampling.Seed.CarriedByEveryQuestion);
    }

    private static IEnumerable<string> Contents(LongMemEvalOracleProjector.Projection projection)
        => projection.Entry.HaystackSessions!.Select(session => session[0].Content);

    private static ExternalBenchmarkOptions Options(Dataset dataset) => new()
    {
        DatasetPath = dataset.Path,
        MaxJudgeRetries = 0,
        RandomSeed = 42,
        HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
    };

    private sealed class RecordingChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Payloads { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public string LastPayload => Payloads[^1];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Payloads.Add(string.Join("\n", chatMessages.Select(message => message.Text)));
            Options.Add(options);
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, _responses.Count > 0 ? _responses.Dequeue() : "yes")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Dataset : IDisposable
    {
        public string Path { get; }

        private Dataset(string path) => Path = path;

        /// <summary>Five sessions: three distractors around two evidence sessions at indices 1 and 3.</summary>
        public static Dataset Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-oracle-{Guid.NewGuid():N}.json");
            var questions = new[]
            {
                new
                {
                    question_id = "q-oracle",
                    question_type = "multi-session",
                    question = "What should be recalled?",
                    answer = "gold answer",
                    question_date = "2026/07/29 (Wed) 00:00",
                    haystack_sessions = new[]
                    {
                        Session("distractor A"),
                        Session("gold one"),
                        Session("distractor B"),
                        Session("gold two"),
                        Session("distractor C")
                    },
                    haystack_dates = new[]
                    {
                        "2026/01/01 (Thu) 09:00",
                        "2026/02/01 (Sun) 09:00",
                        "2026/03/01 (Sun) 09:00",
                        "2026/04/01 (Wed) 09:00",
                        "2026/05/01 (Fri) 09:00"
                    },
                    haystack_session_ids = new[] { "d-1", "answer-session-1", "d-2", "answer-session-2", "d-3" },
                    answer_session_ids = new[] { "answer-session-1", "answer-session-2" }
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(questions));
            return new Dataset(path);
        }

        /// <summary>The same shape as <see cref="Create"/>, in memory, for projector-level tests.</summary>
        public static LongMemEvalEntry Entry(string questionId) => new()
        {
            QuestionId = questionId,
            QuestionType = "multi-session",
            Question = "What should be recalled?",
            AnswerRaw = JsonSerializer.SerializeToElement("gold answer"),
            HaystackSessions =
            [
                Turns("distractor A"), Turns("gold one"), Turns("distractor B"),
                Turns("gold two"), Turns("distractor C")
            ],
            HaystackDates = ["d1", "d2", "d3", "d4", "d5"],
            HaystackSessionIds = ["d-1", "answer-session-1", "d-2", "answer-session-2", "d-3"],
            AnswerSessionIds = ["answer-session-1", "answer-session-2"]
        };

        private static object[] Session(string content) =>
        [
            new { role = "user", content, has_answer = content.StartsWith("gold", StringComparison.Ordinal) },
            new { role = "assistant", content = $"{content} ack", has_answer = false }
        ];

        private static List<LongMemEvalTurn> Turns(string content) =>
        [
            new LongMemEvalTurn { Role = "user", Content = content, HasAnswer = true },
            new LongMemEvalTurn { Role = "assistant", Content = $"{content} ack", HasAnswer = false }
        ];

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
