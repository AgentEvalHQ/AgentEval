// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalPairedRunnerTests
{
    [Fact]
    public async Task RunPairedAsync_NormalThenOracle_KeepsIdsScoresCountersAndEvidenceSeparate()
    {
        using var dataset = Dataset.Create();
        var normalCompleted = false;
        var normalEvidence = new QuestionEvidenceEnvelope
        {
            SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
            Retrieved =
            [
                new EvidenceReference
                {
                    Id = "normal-reference",
                    Rank = 1,
                    SourceSessionId = "answer-session",
                    SourceTurnIndex = 0
                }
            ]
        };
        var normalAgent = new RecordingAgent(() =>
        {
            normalCompleted = true;
            return new AgentResponse
            {
                Text = "normal answer",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    [LongMemEvalEvidenceCapture.ReservedPropertyKey] = normalEvidence
                }
            };
        });
        var oracleClient = new RecordingChatClient(
            "same-model",
            () =>
            {
                Assert.True(normalCompleted);
                return "oracle answer";
            });
        var judge = new RecordingChatClient("judge-model", () => "no", () => "yes");
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);

        var paired = await runner.RunPairedAsync(
            normalAgent,
            Config("normal-memory-agent", "same-model"),
            oracleClient,
            Config("retrieval-disabled-oracle", "same-model"),
            Options(dataset.Path));

        Assert.Equal(["q-paired"], paired.SelectedQuestionIds);
        Assert.Equal(
            paired.SelectedQuestionIds,
            paired.Normal.QuestionResults.Select(question => question.QuestionId));
        Assert.Equal(
            paired.SelectedQuestionIds,
            paired.Oracle.QuestionResults.Select(question => question.QuestionId));
        Assert.Equal(0, paired.Normal.OverallAccuracy);
        Assert.Equal(100, paired.Oracle.OverallAccuracy);
        Assert.Equal(100, paired.OracleGapPercentagePoints);
        Assert.Equal(2, paired.Normal.TotalLlmCalls);
        Assert.Equal(2, paired.Oracle.TotalLlmCalls);
        Assert.Equal("same-model", paired.NormalAnswerModel.ModelId);
        Assert.Equal("same-model", paired.OracleAnswerModel.ModelId);
        Assert.Equal("same-model", paired.OracleAnswerModel.ClientReportedModelId);
        Assert.Equal("judge-model", paired.JudgeModelId);
        var selectedAsList = Assert.IsAssignableFrom<IList<string>>(paired.SelectedQuestionIds);
        Assert.Throws<NotSupportedException>(() => selectedAsList.Add("mutation"));

        var roundTripped = JsonSerializer.Deserialize<LongMemEvalPairedResult>(
            JsonSerializer.Serialize(paired));
        Assert.NotNull(roundTripped);
        Assert.Equal(paired.Plan!.PairId, roundTripped.Plan!.PairId);
        Assert.Equal(paired.Plan.Source.SelectedInputsDigest, roundTripped.Plan.Source.SelectedInputsDigest);
        Assert.Equal(paired.Plan.Source.QuestionIds, roundTripped.Plan.Source.QuestionIds);
        var configuration = roundTripped.Plan.Source.Configuration;
        configuration["MaxQuestions"] = 999;
        Assert.NotEqual(999, (int?)roundTripped.Plan.Source.Configuration["MaxQuestions"]);
        Assert.Equal(paired.SelectedQuestionIds, roundTripped.SelectedQuestionIds);
        Assert.Equal(100, roundTripped.OracleGapPercentagePoints);

        var normalQuestion = Assert.Single(paired.Normal.QuestionResults);
        Assert.Single(normalQuestion.Evidence!.Retrieved);
        var oracleQuestion = Assert.Single(paired.Oracle.QuestionResults);
        Assert.Null(oracleQuestion.Evidence);
        Assert.Equal(EvidenceObservationStatus.NotObserved, oracleQuestion.EvidenceDiagnostics!.Status);

        var oraclePayload = string.Join(
            "\n",
            Assert.Single(oracleClient.Calls).Select(message => message.Text));
        Assert.Contains("oracle evidence", oraclePayload);
        Assert.DoesNotContain("distractor evidence", oraclePayload);
        Assert.DoesNotContain("gold answer", oraclePayload);
        Assert.DoesNotContain("answer-session", oraclePayload);
        Assert.DoesNotContain("has_answer", oraclePayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer_session_ids", oraclePayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunPairedAsync_MismatchedDeclaredAnswerModels_FailsBeforeAnyExecution()
    {
        var normal = new RecordingAgent(() => new AgentResponse { Text = "unused" });
        var oracle = new RecordingChatClient("oracle-model", () => "unused");
        var judge = new RecordingChatClient("judge-model", () => "unused");
        var runner = LongMemEvalBenchmarkRunner.Create(judge, "missing-dataset.json");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunPairedAsync(
                normal,
                Config("normal", "model-a"),
                oracle,
                Config("oracle", "model-b"),
                new ExternalBenchmarkOptions { MaxJudgeRetries = 0 }));

        Assert.Equal("oracleConfig", error.ParamName);
        Assert.Equal(0, normal.CallCount);
        Assert.Empty(oracle.Calls);
        Assert.Empty(judge.Calls);
    }

    [Fact]
    public async Task RunPairedAsync_NormalRunDoesNotComplete_DoesNotCreateOracleAnswerPath()
    {
        using var dataset = Dataset.Create();
        var normal = new RecordingAgent(() => new AgentResponse { Text = "normal answer" });
        var oracle = new RecordingChatClient("same-model", () => "must not run");
        var judge = new RecordingChatClient("judge-model", () => string.Empty);
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);
        var options = new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            MaxJudgeRetries = 0,
            JudgeFailurePolicy = JudgeFailurePolicy.FailRun,
            HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
        };

        await Assert.ThrowsAsync<LongMemEvalJudgeException>(() =>
            runner.RunPairedAsync(
                normal,
                Config("normal", "same-model"),
                oracle,
                Config("oracle", "same-model"),
                options));

        Assert.Equal(1, normal.CallCount);
        Assert.Empty(oracle.Calls);
        Assert.Single(judge.Calls);
    }

    [Fact]
    public async Task RunPairedWithProgressAsync_CapturesOneSelectionBeforeCallsAndDistinctOracleAfterNormal()
    {
        using var dataset = Dataset.Create();
        var events = new List<string>();
        var normal = new RecordingAgent(() => { events.Add("normal-call"); return new AgentResponse { Text = "normal answer" }; });
        var oracle = new RecordingChatClient("same-model", () => { events.Add("oracle-call"); return "oracle answer"; });
        var judge = new RecordingChatClient("judge-model", () => "no", () => "yes");
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);
        LongMemEvalPairedPlan? captured = null;
        var arms = new Dictionary<LongMemEvalArm, ExternalBenchmarkPlan>();
        var result = await runner.RunPairedWithProgressAsync(normal, Config("normal", "same-model"), oracle,
            Config("oracle", "same-model"), Options(dataset.Path), new()
            {
                OnPrepared = plan =>
                {
                    captured = plan;
                    events.Add("prepared");
                    Assert.Equal(0, normal.CallCount);
                    Assert.Empty(oracle.Calls);
                    Assert.Empty(judge.Calls);
                    Assert.DoesNotContain("gold answer", JsonSerializer.Serialize(plan));
                    Assert.DoesNotContain("oracle evidence", JsonSerializer.Serialize(plan));
                    // Both arms must use the already selected entries, never re-open the source.
                    File.WriteAllText(dataset.Path, "[]");
                    plan.Source.Configuration["MaxQuestions"] = 999;
                },
                OnArmPrepared = (arm, plan) => { arms.Add(arm, plan); events.Add(arm + "-prepared"); },
                OnQuestionCompleted = (arm, question) => { Assert.Equal("q-paired", question.QuestionId); events.Add(arm + "-question"); },
                OnArmCompleted = (arm, _) => events.Add(arm + "-complete"),
            });
        Assert.Same(captured, result.Plan);
        Assert.Same(captured!.Source, arms[LongMemEvalArm.Normal]);
        Assert.Equal(new[] { "q-paired" }, result.SelectedQuestionIds);
        Assert.Equal(arms[LongMemEvalArm.Normal].QuestionIds, arms[LongMemEvalArm.Oracle].QuestionIds);
        Assert.NotEqual(arms[LongMemEvalArm.Normal].SelectedInputsDigest, arms[LongMemEvalArm.Oracle].SelectedInputsDigest);
        Assert.Equal("same-model", captured.NormalAnswerModel.ModelId);
        Assert.Equal("judge-model", captured.JudgeModelId);
        Assert.Equal(new[] { "prepared", "Normal-prepared", "normal-call", "Normal-question", "Normal-complete",
            "Oracle-prepared", "oracle-call", "Oracle-question", "Oracle-complete" }, events);
    }

    [Theory]
    [InlineData("prepared", 0, 0)]
    [InlineData("normal-question", 1, 0)]
    [InlineData("normal-complete", 1, 0)]
    [InlineData("oracle-prepared", 1, 0)]
    [InlineData("oracle-question", 1, 1)]
    public async Task RunPairedWithProgressAsync_ObserverStopsExecution_PreservesOnlyObservedArmProgress(string stop, int normalCalls, int oracleCalls)
    {
        using var dataset = Dataset.Create();
        var normal = new RecordingAgent(() => new AgentResponse { Text = "normal answer" });
        var oracle = new RecordingChatClient("same-model", () => "oracle answer");
        var judge = new RecordingChatClient("judge-model", () => "yes", () => "yes");
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);
        var questions = new List<LongMemEvalArm>();
        void Stop(string stage) { if (stage == stop) throw new InvalidOperationException("observer stopped"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunPairedWithProgressAsync(normal,
            Config("normal", "same-model"), oracle, Config("oracle", "same-model"), Options(dataset.Path), new()
            {
                OnPrepared = _ => Stop("prepared"),
                OnArmPrepared = (arm, _) => Stop(arm.ToString().ToLowerInvariant() + "-prepared"),
                OnQuestionCompleted = (arm, _) => { questions.Add(arm); Stop(arm.ToString().ToLowerInvariant() + "-question"); },
                OnArmCompleted = (arm, _) => Stop(arm.ToString().ToLowerInvariant() + "-complete"),
            }));
        Assert.Equal(normalCalls, normal.CallCount);
        Assert.Equal(oracleCalls, oracle.Calls.Count);
        Assert.Equal(normalCalls + oracleCalls, questions.Count);
    }

    [Fact]
    public async Task RunPairedWithProgressAsync_CancelAfterNormal_PreventsOraclePreparation()
    {
        using var dataset = Dataset.Create();
        using var cancel = new CancellationTokenSource();
        var normal = new RecordingAgent(() => new AgentResponse { Text = "normal answer" });
        var oracle = new RecordingChatClient("same-model", () => "must not run");
        var judge = new RecordingChatClient("judge-model", () => "yes");
        var prepared = new List<LongMemEvalArm>();
        ExternalBenchmarkResult? completedNormal = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LongMemEvalBenchmarkRunner.Create(judge, dataset.Path)
            .RunPairedWithProgressAsync(normal, Config("normal", "same-model"), oracle, Config("oracle", "same-model"),
                Options(dataset.Path), new()
                {
                    OnArmPrepared = (arm, _) => prepared.Add(arm),
                    OnArmCompleted = (_, result) => { completedNormal = result; cancel.Cancel(); },
                }, cancel.Token));
        Assert.Equal(new[] { LongMemEvalArm.Normal }, prepared);
        Assert.NotNull(completedNormal);
        Assert.Empty(oracle.Calls);
    }

    private static AgentBenchmarkConfig Config(string name, string modelId) => new()
    {
        AgentName = name,
        ModelId = modelId,
        ModelVersion = "2026-07-01",
        Temperature = 0,
        MaxTokens = 512
    };

    private static ExternalBenchmarkOptions Options(string datasetPath) => new()
    {
        DatasetPath = datasetPath,
        MaxJudgeRetries = 0,
        HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
        EvidenceCaptureMode = EvidenceCaptureMode.References
    };

    private sealed class RecordingAgent(Func<AgentResponse> response)
        : IEvaluableAgent, IHistoryInjectableAgent
    {
        public string Name => "normal";

        public int CallCount { get; private set; }

        public Task<AgentResponse> InvokeAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(response());
        }

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
        {
        }
    }

    private sealed class RecordingChatClient(
        string modelId,
        params Func<string>[] responses) : IChatClient
    {
        private readonly Queue<Func<string>> _responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public ChatClientMetadata Metadata { get; } =
            new("paired-test", new Uri("http://localhost"), modelId);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(chatMessages.Select(message =>
                new ChatMessage(message.Role, message.Text)).ToArray());
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _responses.Dequeue()())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    private sealed class Dataset : IDisposable
    {
        public string Path { get; }

        private Dataset(string path) => Path = path;

        public static Dataset Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-paired-{Guid.NewGuid():N}.json");
            var questions = new[]
            {
                new
                {
                    question_id = "q-paired",
                    question_type = "single-session-user",
                    question = "What should be recalled?",
                    answer = "gold answer",
                    question_date = "2026/07/29 (Wed) 00:00",
                    haystack_sessions = new[]
                    {
                        new object[]
                        {
                            new { role = "user", content = "distractor evidence", has_answer = false },
                            new { role = "assistant", content = "distractor ack", has_answer = false }
                        },
                        new object[]
                        {
                            new { role = "user", content = "oracle evidence", has_answer = true },
                            new { role = "assistant", content = "oracle ack", has_answer = false }
                        }
                    },
                    haystack_dates = new[] { "2026/01/01", "2026/07/01" },
                    haystack_session_ids = new[] { "distractor-session", "answer-session" },
                    answer_session_ids = new[] { "answer-session" }
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(questions));
            return new Dataset(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
