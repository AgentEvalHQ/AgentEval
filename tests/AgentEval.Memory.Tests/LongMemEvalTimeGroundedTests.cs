// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Covers time-grounding: dates delivered as instants instead of as text, the corpus that makes
/// that difference measurable, and the refusal to pretend when it cannot be delivered.
/// </summary>
public sealed class LongMemEvalTimeGroundedTests
{
    [Fact]
    public void Corpus_HasTheDocumentedShape()
    {
        var entries = LongMemEvalTimeGroundedCorpus.Load();

        Assert.Equal(LongMemEvalTimeGroundedCorpus.QuestionCount, entries.Count);
        Assert.Equal(
            entries.Count,
            entries.Select(entry => entry.QuestionId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [LongMemEvalTimeGroundedCorpus.AsOfQuestionType] = 4,
                [LongMemEvalTimeGroundedCorpus.CurrentQuestionType] = 4,
                [LongMemEvalTimeGroundedCorpus.ProspectiveQuestionType] = 4
            },
            LongMemEvalDataLoader.GetTypeDistribution(entries));
        Assert.Equal(64, LongMemEvalTimeGroundedCorpus.Sha256().Length);
    }

    [Fact]
    public void Corpus_Sha256_DoesNotDependOnTheCheckoutsLineEndings()
    {
        // The corpus is embedded from a git checkout. A hash that changed when a run moved from a
        // Windows machine to a Linux runner would report "different corpus" for the same corpus.
        var json = LongMemEvalTimeGroundedCorpus.ReadJson();
        var lf = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.NotEqual(lf, crlf);
        Assert.Equal(
            LongMemEvalTimeGroundedCorpus.ComputeSha256(lf),
            LongMemEvalTimeGroundedCorpus.ComputeSha256(crlf));
        Assert.Equal(LongMemEvalTimeGroundedCorpus.ComputeSha256(lf), LongMemEvalTimeGroundedCorpus.Sha256());
    }

    [Fact]
    public void Corpus_ContainsNoAbsoluteDateInAnyMessage()
    {
        // The property the whole probe rests on. A single "March 2026" in a conversation would let a
        // system that stores no time at all answer the question from the text, which is exactly the
        // hole this corpus exists to close.
        foreach (var entry in LongMemEvalTimeGroundedCorpus.Load())
        {
            Assert.False(
                LongMemEvalTimestamps.LooksDated(entry.Question),
                $"{entry.QuestionId}: the question text carries an absolute date.");
            foreach (var turn in entry.HaystackSessions!.SelectMany(session => session))
            {
                Assert.False(
                    LongMemEvalTimestamps.LooksDated(turn.Content),
                    $"{entry.QuestionId}: message content carries an absolute date: {turn.Content}");
            }
        }
    }

    [Fact]
    public void Corpus_EveryDateParsesAndPrecedesTheQuestion()
    {
        foreach (var entry in LongMemEvalTimeGroundedCorpus.Load())
        {
            var asked = LongMemEvalTimestamps.TryParse(entry.QuestionDate);
            Assert.True(asked.HasValue, $"{entry.QuestionId}: question_date does not parse.");

            var dates = entry.HaystackDates!
                .Select(date => LongMemEvalTimestamps.TryParse(date))
                .ToList();
            Assert.All(dates, date => Assert.True(date.HasValue));
            Assert.All(dates, date => Assert.True(date!.Value < asked!.Value));
            Assert.Equal(dates.Select(d => d!.Value).OrderBy(d => d), dates.Select(d => d!.Value));
            Assert.Equal(entry.HaystackSessions!.Count, entry.HaystackDates!.Count);
            Assert.Equal(entry.HaystackSessions.Count, entry.HaystackSessionIds!.Count);
        }
    }

    [Fact]
    public void Corpus_EveryQuestionHasLabelledEvidence()
    {
        foreach (var entry in LongMemEvalTimeGroundedCorpus.Load())
        {
            Assert.NotEmpty(entry.AnswerSessionIds!);
            Assert.All(
                entry.AnswerSessionIds!,
                gold => Assert.Contains(gold, entry.HaystackSessionIds!));

            var goldTurns = entry.AnswerSessionIds!
                .Select(gold => entry.HaystackSessionIds!.IndexOf(gold))
                .SelectMany(index => entry.HaystackSessions![index]);
            Assert.Contains(goldTurns, turn => turn.HasAnswer is true);
        }
    }

    [Theory]
    [InlineData("2023/05/20 (Sat) 02:21", "2023-05-20T02:21:00+00:00")]
    [InlineData("2023/05/20 (Mon) 02:21", "2023-05-20T02:21:00+00:00")] // wrong day name, real date
    [InlineData("2026/01/12", "2026-01-12T00:00:00+00:00")]
    [InlineData("2026-01-12 08:30", "2026-01-12T08:30:00+00:00")]
    public void TryParse_CorpusDateShapes_ReadAsUtcInstants(string value, string expected)
    {
        var parsed = LongMemEvalTimestamps.TryParse(value);

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("last Tuesday")]
    public void TryParse_NotADate_ReturnsNullRatherThanADefault(string? value)
        => Assert.Null(LongMemEvalTimestamps.TryParse(value));

    [Fact]
    public async Task RunTimeGroundedAsync_AgentCannotReceiveTimestamps_FailsBeforeAnyProviderCall()
    {
        var judge = new RecordingChatClient();
        var agent = new PlainAgent();
        var runner = LongMemEvalBenchmarkRunner.Create(judge);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunTimeGroundedAsync(agent));

        Assert.Equal("agent", error.ParamName);
        Assert.Contains("ITimestampedHistoryInjectableAgent", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, agent.CallCount);
        Assert.Empty(judge.Payloads);
    }

    [Fact]
    public async Task RunTimeGroundedAsync_TimestampsOnly_DeliversInstantsAndRemovesInTextDates()
    {
        var agent = new TimestampedAgent();
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient());

        var result = await runner.RunTimeGroundedAsync(agent);

        Assert.Equal(LongMemEvalTimeGroundedCorpus.QuestionCount, result.QuestionResults.Count);
        Assert.NotNull(result.TemporalGrounding);
        var grounding = result.TemporalGrounding;
        Assert.Equal(TemporalGroundingMode.TimestampsOnly, grounding.Mode);
        Assert.Equal(LongMemEvalTimeGroundedCorpus.QuestionCount, grounding.Questions);
        Assert.True(grounding.SessionsTimestamped >= grounding.Questions);
        Assert.True(grounding.TurnsTimestamped > grounding.SessionsTimestamped);
        Assert.True(grounding.InTextDatesRemoved);
        Assert.Equal(0, grounding.SessionsWithDateLikeContent);
        Assert.True(grounding.EarliestSessionTimestamp < grounding.LatestSessionTimestamp);

        // Every turn carries an instant, and none of the injected text carries a date.
        var history = agent.Histories[0];
        Assert.All(history.Turns, turn => Assert.NotEqual(default, turn.Timestamp));
        Assert.All(
            history.Turns.SelectMany(turn => new[] { turn.UserMessage, turn.AssistantResponse }),
            text => Assert.False(LongMemEvalTimestamps.LooksDated(text)));
        Assert.All(agent.Prompts, prompt => Assert.DoesNotContain("Current Date", prompt, StringComparison.Ordinal));
        Assert.Contains(history.Turns, turn => turn.UserMessage.StartsWith("--- Session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTimeGroundedAsync_TimestampsAndText_KeepsTheScaffoldingAsAControl()
    {
        var agent = new TimestampedAgent();
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient());

        var result = await runner.RunTimeGroundedAsync(
            agent,
            LongMemEvalTimeGroundedCorpus.ControlOptions);

        Assert.Equal(TemporalGroundingMode.TimestampsAndText, result.TemporalGrounding!.Mode);
        Assert.False(result.TemporalGrounding.InTextDatesRemoved);
        Assert.All(agent.Prompts, prompt => Assert.Contains("Current Date", prompt, StringComparison.Ordinal));
        Assert.Contains(
            agent.Histories[0].Turns,
            turn => turn.UserMessage.StartsWith("--- Session", StringComparison.Ordinal) &&
                    LongMemEvalTimestamps.LooksDated(turn.UserMessage));
    }

    [Fact]
    public async Task RunAsync_WithoutGrounding_ReportsNothingAndKeepsHistoricalInjection()
    {
        using var dataset = TempDataset.Create(LongMemEvalTimeGroundedCorpus.ReadJson());
        var agent = new TimestampedAgent();
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient(), dataset.Path);

        var result = await runner.RunAsync(
            agent,
            new AgentBenchmarkConfig { AgentName = "subject", ModelId = "model" },
            new ExternalBenchmarkOptions
            {
                DatasetPath = dataset.Path,
                MaxJudgeRetries = 0,
                HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
            });

        Assert.Null(result.TemporalGrounding);
        // The timestamped channel is not used unless grounding asked for it, so an agent that
        // implements both interfaces still gets exactly the history it always got.
        Assert.Empty(agent.Histories);
        Assert.NotEmpty(agent.PlainHistories);
    }

    [Fact]
    public async Task RunAsync_UnparseableSessionDate_NamesTheQuestionAndStopsBeforeAnyProviderCall()
    {
        using var dataset = TempDataset.Create(JsonSerializer.Serialize(new[]
        {
            new
            {
                question_id = "q-broken",
                question_type = "temporal-as-of",
                question = "When did it happen?",
                answer = "irrelevant",
                question_date = "2026/07/29 (Wed) 00:00",
                haystack_sessions = new[]
                {
                    new object[]
                    {
                        new { role = "user", content = "something happened", has_answer = true },
                        new { role = "assistant", content = "noted", has_answer = false }
                    }
                },
                haystack_dates = new[] { "some time last spring" },
                haystack_session_ids = new[] { "s1" },
                answer_session_ids = new[] { "s1" }
            }
        }));
        var judge = new RecordingChatClient();
        var agent = new TimestampedAgent();
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);

        var error = await Assert.ThrowsAsync<LongMemEvalTemporalGroundingException>(() =>
            runner.RunAsync(
                agent,
                new AgentBenchmarkConfig { AgentName = "subject", ModelId = "model" },
                new ExternalBenchmarkOptions
                {
                    DatasetPath = dataset.Path,
                    TemporalGrounding = TemporalGroundingMode.TimestampsOnly,
                    HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
                }));

        Assert.Equal("q-broken", error.QuestionId);
        Assert.Equal("haystack_dates[0]", error.Field);
        Assert.Equal(0, agent.CallCount);
        Assert.Empty(judge.Payloads);
    }

    [Fact]
    public void Validate_GroundingWithTextBlobInjection_SaysSoInsteadOfSilentlyOverriding()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ExternalBenchmarkOptions
            {
                TemporalGrounding = TemporalGroundingMode.TimestampsOnly,
                HistoryInjectionMode = HistoryInjectionMode.TextBlob
            }.Validate());

        Assert.Equal(nameof(ExternalBenchmarkOptions.HistoryInjectionMode), error.ParamName);
    }

    [Fact]
    public async Task RunTimeGroundedAsync_FullProvenance_PinsTheEmbeddedCorpusWithoutAPath()
    {
        var agent = new TimestampedAgent();
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient());
        var options = new ExternalBenchmarkOptions
        {
            TemporalGrounding = TemporalGroundingMode.TimestampsOnly,
            HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
            RunProvenanceMode = RunProvenanceMode.Full,
            MaxJudgeRetries = 0
        };

        var result = await runner.RunTimeGroundedAsync(agent, options);

        Assert.NotNull(result.Provenance);
        var provenance = result.Provenance;
        // A corpus with no file still gets pinned: an identifier and a hash over the shipped text.
        Assert.Null(provenance.DatasetPath);
        Assert.Equal(LongMemEvalTimeGroundedCorpus.CorpusId, provenance.DatasetIdentifier);
        Assert.Equal(LongMemEvalTimeGroundedCorpus.Sha256(), provenance.DatasetSha256);
        Assert.Equal(LongMemEvalTimeGroundedCorpus.QuestionCount, provenance.DatasetQuestionCount);
        Assert.NotNull(provenance.SelectedQuestionIdFingerprint);
    }

    [Fact]
    public async Task RunTimeGroundedOracleAsync_GivesTheCeilingForATimestampOnlyCorpus()
    {
        var answerClient = new RecordingChatClient();
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient());

        var result = await runner.RunTimeGroundedOracleAsync(answerClient);

        Assert.Equal(LongMemEvalTimeGroundedCorpus.QuestionCount, result.QuestionResults.Count);
        Assert.NotNull(result.OracleProjection);
        Assert.Equal(1.0, result.OracleProjection.RealisedGoldSessionFraction);
        Assert.Equal(TemporalGroundingMode.TimestampsOnly, result.TemporalGrounding!.Mode);

        // The reader is a system that places messages in time by construction: it writes the
        // instants it was handed into its own prompt, and states the query time explicitly.
        var payload = answerClient.Payloads[0];
        Assert.Contains("Current date and time: 2026/", payload, StringComparison.Ordinal);
        Assert.Contains("] Signed up at Riverside Fitness", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_WithAllThreeNewReports_RoundTripsThroughJsonAndProjectsToEvalResult()
    {
        var runner = LongMemEvalBenchmarkRunner.Create(new RecordingChatClient());
        var options = new ExternalBenchmarkOptions
        {
            TemporalGrounding = TemporalGroundingMode.TimestampsOnly,
            HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
            MaxJudgeRetries = 0,
            RandomSeed = 42,
            AnswerTemperature = 0.1,
            AnswerSeed = 99
        };

        var result = await runner.RunTimeGroundedOracleAsync(
            new RecordingChatClient(),
            options,
            new LongMemEvalOracleOptions { DistractorSessions = 1, GoldSessionFraction = 0.5 });

        var roundTripped = JsonSerializer.Deserialize<ExternalBenchmarkResult>(
            JsonSerializer.Serialize(result));

        Assert.NotNull(roundTripped);
        Assert.Equal(
            result.AnswerSampling!.Temperature.SentUnverifiedQuestions,
            roundTripped.AnswerSampling!.Temperature.SentUnverifiedQuestions);
        Assert.Equal(
            result.OracleProjection!.GoldSessionsKept,
            roundTripped.OracleProjection!.GoldSessionsKept);
        Assert.Equal(result.TemporalGrounding!.Mode, roundTripped.TemporalGrounding!.Mode);
        Assert.Equal(
            result.TemporalGrounding.SessionsTimestamped,
            roundTripped.TemporalGrounding.SessionsTimestamped);

        var projected = LongMemEvalEvalResultAdapter.ToEvalResult(
            result, presetName: "time-grounded", judgeModel: "judge");
        var dimensions = projected.Details!.Dimensions!;
        Assert.Equal(0.1, dimensions["answerTemperature"]);
        Assert.Equal(99, dimensions["answerSeed"]);
        Assert.Equal(
            (double)TemporalGroundingMode.TimestampsOnly, dimensions["temporalGroundingMode"]);
        Assert.Equal(0, dimensions["temporalGroundingSessionsWithDateLikeContent"]);
        Assert.Equal(
            result.OracleProjection.GoldSessionsKept, dimensions["oracleGoldSessionsKept"]);
    }

    [Fact]
    public async Task OracleReader_TimestampedHistory_StampsEveryTurnAndClearsAfterEachQuestion()
    {
        var client = new RecordingChatClient();
        var reader = new LongMemEvalOracleReader(client);
        reader.InjectTimestampedConversationHistory(new TimestampedConversationHistory
        {
            QueryTime = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
            Turns =
            [
                new TimestampedConversationTurn(
                    "I joined the club",
                    "Noted",
                    new DateTimeOffset(2026, 2, 24, 19, 45, 0, TimeSpan.Zero),
                    0)
            ]
        });

        await reader.InvokeAsync("Can I renew yet?");
        await reader.InvokeAsync("And now?");

        Assert.Contains("[2026/02/24 19:45] I joined the club", client.Payloads[0], StringComparison.Ordinal);
        Assert.Contains(
            "Current date and time: 2026/06/01 09:00 UTC.", client.Payloads[0], StringComparison.Ordinal);
        // State is per question: the second call carries neither the history nor the query time.
        Assert.DoesNotContain("I joined the club", client.Payloads[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Current date and time", client.Payloads[1], StringComparison.Ordinal);
    }

    private sealed class PlainAgent : IEvaluableAgent
    {
        public string Name => "plain";

        public int CallCount { get; private set; }

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AgentResponse { Text = "answer" });
        }
    }

    private sealed class TimestampedAgent
        : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent
    {
        public string Name => "timestamped";

        public int CallCount { get; private set; }

        public List<TimestampedConversationHistory> Histories { get; } = [];

        public List<IReadOnlyList<(string UserMessage, string AssistantResponse)>> PlainHistories { get; } = [];

        public List<string> Prompts { get; } = [];

        public void InjectTimestampedConversationHistory(TimestampedConversationHistory history)
            => Histories.Add(history);

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
            => PlainHistories.Add(conversationTurns.ToList());

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Prompts.Add(prompt);
            return Task.FromResult(new AgentResponse { Text = "answer" });
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<string> Payloads { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Payloads.Add(string.Join("\n", chatMessages.Select(message => message.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));
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

    private sealed class TempDataset : IDisposable
    {
        public string Path { get; }

        private TempDataset(string path) => Path = path;

        public static TempDataset Create(string json)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-timegrounded-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new TempDataset(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
