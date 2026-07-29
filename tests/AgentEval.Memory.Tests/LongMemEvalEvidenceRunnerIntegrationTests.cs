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

public sealed class LongMemEvalEvidenceRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_EvidenceCaptureNone_DoesNotInspectOrSerializeEvidence()
    {
        using var dataset = Dataset.WithQuestions(1);
        var judge = new SequenceChatClient("yes");
        var agent = new SequenceAgent(() => new AgentResponse
        {
            Text = "answer",
            AdditionalProperties = new ThrowOnAccessDictionary()
        });

        var result = await RunAsync(
            dataset.Path,
            judge,
            agent,
            Options(EvidenceCaptureMode.None));

        var question = Assert.Single(result.QuestionResults);
        Assert.True(question.Correct);
        Assert.Null(question.Evidence);
        Assert.Null(question.EvidenceDiagnostics);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("\"Evidence\":", serialized);
        Assert.DoesNotContain("\"EvidenceDiagnostics\":", serialized);
    }

    [Fact]
    public async Task RunAsync_ValidAndInvalidEvidence_PreservesJudgeScoreAndUsesSafeDiagnostics()
    {
        using var dataset = Dataset.WithQuestions(2);
        var judge = new SequenceChatClient("yes", "yes");
        var valid = new QuestionEvidenceEnvelope
        {
            SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
            Retrieved =
            [
                new EvidenceReference
                {
                    Id = "retrieval-1",
                    Rank = 1,
                    SourceSessionId = "session-1",
                    SourceTurnIndex = 0
                }
            ]
        };
        const string invalid =
            """{"SchemaVersion":"1.0","Retrieved":[],"AnswerContext":[],"Authorization":"secret"}""";
        var agent = new SequenceAgent(
            Reply("answer one", valid),
            Reply("answer two", invalid));

        var result = await RunAsync(
            dataset.Path,
            judge,
            agent,
            Options(EvidenceCaptureMode.References));

        Assert.Equal(100, result.OverallAccuracy);
        Assert.Equal(2, result.CorrectQuestions);
        Assert.Single(result.QuestionResults[0].Evidence!.Retrieved);
        Assert.Equal(
            EvidenceObservationStatus.Observed,
            result.QuestionResults[0].EvidenceDiagnostics!.Status);
        Assert.Null(result.QuestionResults[1].Evidence);
        Assert.Equal(
            EvidenceObservationStatus.Invalid,
            result.QuestionResults[1].EvidenceDiagnostics!.Status);
        Assert.Equal(
            "invalid_evidence_schema",
            result.QuestionResults[1].EvidenceDiagnostics!.SafeFailureCode);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    private static ExternalBenchmarkOptions Options(EvidenceCaptureMode mode) => new()
    {
        MaxJudgeRetries = 0,
        HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
        EvidenceCaptureMode = mode
    };

    private static Func<AgentResponse> Reply(string text, object evidence)
        => () => new AgentResponse
        {
            Text = text,
            AdditionalProperties = new Dictionary<string, object?>
            {
                [LongMemEvalEvidenceCapture.ReservedPropertyKey] = evidence
            }
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

    private sealed class SequenceChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public ChatClientMetadata Metadata { get; } =
            new("evidence-runner-test", new Uri("http://localhost"));

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

    private sealed class SequenceAgent(params Func<AgentResponse>[] responses)
        : IEvaluableAgent, IHistoryInjectableAgent
    {
        private readonly Queue<Func<AgentResponse>> _responses = new(responses);

        public string Name => "evidence-runner-test-agent";

        public Task<AgentResponse> InvokeAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("No agent response configured.");
            return Task.FromResult(_responses.Dequeue().Invoke());
        }

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
        {
        }
    }

    private sealed class ThrowOnAccessDictionary : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException("must not inspect");
        public IEnumerable<string> Keys => throw new InvalidOperationException("must not inspect");
        public IEnumerable<object?> Values => throw new InvalidOperationException("must not inspect");
        public int Count => throw new InvalidOperationException("must not inspect");
        public bool ContainsKey(string key) => throw new InvalidOperationException("must not inspect");
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => throw new InvalidOperationException("must not inspect");
        public bool TryGetValue(string key, out object? value)
            => throw new InvalidOperationException("must not inspect");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => throw new InvalidOperationException("must not inspect");
    }

    private sealed class Dataset : IDisposable
    {
        public string Path { get; }

        private Dataset(string path) => Path = path;

        public static Dataset WithQuestions(int count)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-evidence-{Guid.NewGuid():N}.json");
            var questions = Enumerable.Range(1, count).Select(i => new
            {
                question_id = $"q-{i}",
                question_type = "single-session-user",
                question = $"Question {i}?",
                answer = "gold answer",
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
