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

public sealed class LongMemEvalBenchmarkRunnerTests
{
    [Fact]
    public async Task RunAsync_MixedBinaryAndEmptyJudgments_UsesOnlyBinaryDenominator()
    {
        using var dataset = Dataset.WithQuestions(3);
        var judge = new SequenceChatClient(Response("yes"), Response("no"), Response(" "));
        var agent = new SpyAgent(
            AgentReply("answer one"),
            AgentReply("answer two"),
            AgentReply("answer three"));

        var result = await RunAsync(dataset.Path, judge, agent, Options());

        Assert.Equal(3, result.SelectedQuestions);
        Assert.Equal(3, result.AgentCompletedQuestions);
        Assert.Equal(2, result.ScoredQuestions);
        Assert.Equal(1, result.CorrectQuestions);
        Assert.Equal(1, result.IncorrectQuestions);
        Assert.Equal(1, result.InconclusiveQuestions);
        Assert.Equal(0, result.AgentFailureQuestions);
        Assert.Equal(50, result.OverallAccuracy);
        Assert.Equal(50, result.TaskAveragedAccuracy);
        Assert.Equal(6, result.TotalLlmCalls);
        Assert.Equal(JudgeOutcomeStatus.Empty, result.QuestionResults[2].JudgeStatus);
        Assert.Null(result.QuestionResults[2].Correct);
    }

    [Fact]
    public async Task RunAsync_JudgeRetry_CountsAgentAndEveryJudgeAttempt()
    {
        using var dataset = Dataset.WithQuestions(1);
        var judge = new SequenceChatClient(Response(""), Response("yes"));
        var agent = new SpyAgent(AgentReply("answer"));

        var result = await RunAsync(dataset.Path, judge, agent, Options(maxRetries: 1));

        Assert.Equal(3, result.TotalLlmCalls);
        var question = Assert.Single(result.QuestionResults);
        Assert.Equal(1, question.AgentLlmCallCount);
        Assert.Equal(2, question.JudgeLlmCallCount);
        Assert.Equal(JudgeOutcomeStatus.Yes, question.JudgeStatus);
    }

    [Fact]
    public async Task RunAsync_AgentFailure_DoesNotInvokeOrMasqueradeAsJudgeFailure()
    {
        using var dataset = Dataset.WithQuestions(1);
        var judge = new SequenceChatClient(Response("yes"));
        var agent = new SpyAgent(() => throw new InvalidOperationException("sensitive agent detail"));

        var result = await RunAsync(dataset.Path, judge, agent, Options());

        Assert.Null(result.OverallAccuracy);
        Assert.Null(result.TaskAveragedAccuracy);
        Assert.Equal(1, result.AgentFailureQuestions);
        Assert.Equal(0, result.AgentCompletedQuestions);
        Assert.Equal(0, result.InconclusiveQuestions);
        Assert.Equal(1, result.TotalLlmCalls);
        Assert.Equal(0, judge.CallCount);
        var question = Assert.Single(result.QuestionResults);
        Assert.Equal(QuestionExecutionStatus.AgentError, question.ExecutionStatus);
        Assert.Null(question.JudgeStatus);
        Assert.DoesNotContain("sensitive agent detail", question.AgentResponse);
        Assert.DoesNotContain("sensitive agent detail", question.JudgeExplanation ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_RetryThenIncorrect_IsExplicitDenominatorPolicyWithoutRewritingTruth()
    {
        using var dataset = Dataset.WithQuestions(1);
        var judge = new SequenceChatClient(Response(""));
        var agent = new SpyAgent(AgentReply("answer"));
        var options = WithPolicy(Options(), JudgeFailurePolicy.RetryThenIncorrect);

        var result = await RunAsync(dataset.Path, judge, agent, options);

        Assert.Equal(1, result.ScoredQuestions);
        Assert.Equal(1, result.InconclusiveQuestions);
        Assert.Equal(0, result.OverallAccuracy);
        var question = Assert.Single(result.QuestionResults);
        Assert.Null(question.Correct);
        Assert.Equal(JudgeOutcomeStatus.Empty, question.JudgeStatus);
    }

    [Fact]
    public async Task RunAsync_FailRun_PropagatesTypedJudgeFailure()
    {
        using var dataset = Dataset.WithQuestions(1);
        var judge = new SequenceChatClient(Response(""));
        var agent = new SpyAgent(AgentReply("answer"));
        var options = WithPolicy(Options(), JudgeFailurePolicy.FailRun);

        var error = await Assert.ThrowsAsync<LongMemEvalJudgeException>(
            () => RunAsync(dataset.Path, judge, agent, options));

        Assert.Equal("q-1", error.QuestionId);
        Assert.Equal(JudgeOutcomeStatus.Empty, error.Status);
    }

    [Fact]
    public async Task RunAsync_GoldLabelsNeverEnterAgentPromptOrInjectedHistory()
    {
        const string gold = "TOP_SECRET_GOLD_ANSWER";
        const string answerSession = "TOP_SECRET_ANSWER_SESSION";
        using var dataset = Dataset.WithQuestions(1, gold, answerSession);
        var judge = new SequenceChatClient(Response("yes"));
        var agent = new SpyAgent(AgentReply("answer"));
        var options = new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            MaxJudgeRetries = 0,
            HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
        };

        await RunAsync(dataset.Path, judge, agent, options);

        var agentPayload = string.Join(
            "\n",
            agent.Prompts.Concat(agent.InjectedTurns.SelectMany(t => new[] { t.UserMessage, t.AssistantResponse })));
        Assert.DoesNotContain(gold, agentPayload);
        Assert.DoesNotContain(answerSession, agentPayload);
        Assert.DoesNotContain("has_answer", agentPayload, StringComparison.OrdinalIgnoreCase);
    }

    private static ExternalBenchmarkOptions Options(int maxRetries = 0) => new()
    {
        MaxJudgeRetries = maxRetries,
        JudgeFailurePolicy = JudgeFailurePolicy.RetryThenInconclusive,
        HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
    };

    private static ExternalBenchmarkOptions WithPolicy(
        ExternalBenchmarkOptions options,
        JudgeFailurePolicy policy) => new()
    {
        DatasetPath = options.DatasetPath,
        MaxQuestions = options.MaxQuestions,
        StratifiedSampling = options.StratifiedSampling,
        PreserveSessionBoundaries = options.PreserveSessionBoundaries,
        IncludeTimestamps = options.IncludeTimestamps,
        RandomSeed = options.RandomSeed,
        DatasetMode = options.DatasetMode,
        HistoryInjectionMode = options.HistoryInjectionMode,
        JudgeFailurePolicy = policy,
        MaxJudgeRetries = options.MaxJudgeRetries,
        JudgeEvidenceMode = options.JudgeEvidenceMode
    };

    private static async Task<ExternalBenchmarkResult> RunAsync(
        string datasetPath,
        IChatClient judge,
        IEvaluableAgent agent,
        ExternalBenchmarkOptions options)
    {
        var runner = LongMemEvalBenchmarkRunner.Create(judge, datasetPath);
        return await runner.RunAsync(
            agent,
            new AgentBenchmarkConfig { AgentName = agent.Name },
            options);
    }

    private static Func<ChatResponse> Response(string text)
        => () => new ChatResponse(new ChatMessage(ChatRole.Assistant, text));

    private static Func<AgentResponse> AgentReply(string text)
        => () => new AgentResponse { Text = text };

    private sealed class SequenceChatClient(params Func<ChatResponse>[] sequence) : IChatClient
    {
        private readonly Queue<Func<ChatResponse>> _sequence = new(sequence);

        public int CallCount { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("sequence", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_sequence.Count == 0)
                throw new InvalidOperationException("No response configured.");
            return Task.FromResult(_sequence.Dequeue().Invoke());
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

    private sealed class SpyAgent(params Func<AgentResponse>[] sequence)
        : IEvaluableAgent, IHistoryInjectableAgent
    {
        private readonly Queue<Func<AgentResponse>> _sequence = new(sequence);

        public string Name => "spy";

        public List<string> Prompts { get; } = [];

        public List<(string UserMessage, string AssistantResponse)> InjectedTurns { get; } = [];

        public Task<AgentResponse> InvokeAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            if (_sequence.Count == 0)
                throw new InvalidOperationException("No agent response configured.");
            return Task.FromResult(_sequence.Dequeue().Invoke());
        }

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
            => InjectedTurns.AddRange(conversationTurns);
    }

    private sealed class Dataset : IDisposable
    {
        public string Path { get; }

        private Dataset(string path) => Path = path;

        public static Dataset WithQuestions(
            int count,
            string goldAnswer = "gold answer",
            string answerSessionId = "answer-session")
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-{Guid.NewGuid():N}.json");
            var questions = Enumerable.Range(1, count).Select(i => new
            {
                question_id = $"q-{i}",
                question_type = "single-session-user",
                question = $"Question {i}?",
                answer = goldAnswer,
                question_date = "2026/01/02 (Fri) 00:00",
                haystack_sessions = new[]
                {
                    new object[]
                    {
                        new { role = "user", content = $"safe history {i}", has_answer = true },
                        new { role = "assistant", content = $"safe reply {i}", has_answer = false }
                    }
                },
                haystack_dates = new[] { "2026/01/01 (Thu) 00:00" },
                haystack_session_ids = new[] { $"session-{i}" },
                answer_session_ids = new[] { answerSessionId }
            });
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
