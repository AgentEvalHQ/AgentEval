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

/// <summary>
/// End-to-end tests that judge diagnostics survive the trip from
/// <see cref="ExternalJudgmentResult"/> onto <see cref="QuestionResult"/>.
/// </summary>
/// <remarks>
/// Retaining the raw response on the judgment is not enough on its own: a consumer diagnoses a stored
/// run, and the run is a list of <see cref="QuestionResult"/>. A field that stops at the judgment is a
/// field the consumer never sees.
/// </remarks>
public sealed class LongMemEvalJudgeRunnerPropagationTests
{
    [Fact]
    public async Task RetainRawJudgeResponse_ReachesTheStoredQuestionResult()
    {
        using var dataset = Dataset.WithQuestions(1);

        var result = await RunAsync(
            dataset.Path,
            new SequenceChatClient("Correct — the response matches."),
            new ExternalBenchmarkOptions
            {
                MaxJudgeRetries = 0,
                HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
                JudgeEvidenceMode = JudgeEvidenceMode.Outcome,
                RetainRawJudgeResponse = true
            });

        var question = Assert.Single(result.QuestionResults);

        // The diagnosis this option exists for: the verdict is unusable, and the raw text shows the
        // judge was answering sensibly while the wrapper could not read it.
        Assert.Equal(JudgeOutcomeStatus.Invalid, question.JudgeStatus);
        Assert.Equal("Correct — the response matches.", question.JudgeRawResponse);
        Assert.Contains("\"JudgeRawResponse\":", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredProtocol_ReasoningReachesTheStoredQuestionResult()
    {
        using var dataset = Dataset.WithQuestions(1);

        var result = await RunAsync(
            dataset.Path,
            new SequenceChatClient(
                """{"verdict": "yes", "reasoning": "The response states the gold answer verbatim."}"""),
            new ExternalBenchmarkOptions
            {
                MaxJudgeRetries = 0,
                HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
                JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson
            });

        var question = Assert.Single(result.QuestionResults);

        Assert.Equal(JudgeOutcomeStatus.Yes, question.JudgeStatus);
        Assert.True(question.Correct);
        Assert.Equal("The response states the gold answer verbatim.", question.JudgeReasoning);
    }

    [Fact]
    public async Task PerPredicate_ResultsAndRuleReachTheStoredQuestionResult()
    {
        using var dataset = Dataset.WithQuestions(
            1, goldAnswer: "The user adopted a terrier named Biscuit. The user moved to Lisbon in March.");

        var result = await RunAsync(
            dataset.Path,
            new SequenceChatClient("yes", "no"),
            new ExternalBenchmarkOptions
            {
                MaxJudgeRetries = 0,
                HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
                JudgeDecompositionMode = JudgeDecompositionMode.PerPredicate
            });

        var question = Assert.Single(result.QuestionResults);

        Assert.Equal(2, question.JudgePredicateResults!.Count);
        Assert.Equal(JudgeOutcomeStatus.Yes, question.JudgePredicateResults[0].Status);
        Assert.Equal(JudgeOutcomeStatus.No, question.JudgePredicateResults[1].Status);
        Assert.Equal("The user moved to Lisbon in March", question.JudgePredicateResults[1].Predicate);
        Assert.Equal(PredicateCombinationRule.AllMustHold, question.JudgePredicateCombinationRule);
        Assert.Equal(JudgeOutcomeStatus.No, question.JudgeStatus);

        // Which claim failed is visible in the stored run, not only in the aggregate verdict.
        var serialized = JsonSerializer.Serialize(result);
        Assert.Contains("Lisbon", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultOptions_AddNoNewFieldsToTheStoredRun()
    {
        using var dataset = Dataset.WithQuestions(1);

        var result = await RunAsync(
            dataset.Path,
            new SequenceChatClient("yes"),
            new ExternalBenchmarkOptions
            {
                MaxJudgeRetries = 0,
                HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory
            });

        var serialized = JsonSerializer.Serialize(result);

        // A sealed base recorded before this change stays byte-comparable: every new field is
        // WhenWritingNull and every new option defaults off.
        Assert.DoesNotContain("JudgeRawResponse", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("JudgeReasoning", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("JudgePredicateResults", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("JudgePredicateCombinationRule", serialized, StringComparison.Ordinal);
    }

    private static async Task<ExternalBenchmarkResult> RunAsync(
        string datasetPath,
        IChatClient judge,
        ExternalBenchmarkOptions options)
    {
        var runner = LongMemEvalBenchmarkRunner.Create(judge, datasetPath);
        var agent = new EchoAgent();
        return await runner.RunAsync(
            agent,
            new AgentBenchmarkConfig { AgentName = agent.Name },
            options);
    }

    private sealed class SequenceChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public ChatClientMetadata Metadata { get; } =
            new("propagation-test", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("No judge response configured.");
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _responses.Dequeue())));
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

    private sealed class EchoAgent : IEvaluableAgent, IHistoryInjectableAgent
    {
        public string Name => "propagation-test-agent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = "the agent answer" });

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
        {
        }
    }

    private sealed class Dataset : IDisposable
    {
        public string Path { get; }

        private Dataset(string path) => Path = path;

        public static Dataset WithQuestions(int count, string goldAnswer = "gold answer")
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-propagation-{Guid.NewGuid():N}.json");
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
                answer_session_ids = new[] { $"session-{i}" }
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
