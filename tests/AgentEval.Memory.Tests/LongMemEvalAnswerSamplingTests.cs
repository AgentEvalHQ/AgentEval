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
/// Covers pinning the answer model: what reaches the wire, what the provider said about it, and the
/// cases where AgentEval must refuse to claim a run was pinned.
/// </summary>
public sealed class LongMemEvalAnswerSamplingTests
{
    [Fact]
    public async Task RunAsync_NoAnswerSamplingRequested_ReportsNothingAndNeverReadsThePropertyBag()
    {
        using var dataset = Dataset.Create();
        var agent = new PlainAgent(() => new AgentResponse
        {
            Text = "answer",
            AdditionalProperties = new ThrowOnAccessDictionary()
        });

        var result = await RunAsync(dataset, agent, new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            MaxJudgeRetries = 0
        });

        Assert.Null(result.AnswerSampling);
        Assert.Null(Assert.Single(result.QuestionResults).AnswerSampling);
        Assert.DoesNotContain("AnswerSampling", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AgentCannotCarrySampling_ReportsNotSupportedRatherThanPinned()
    {
        using var dataset = Dataset.Create();
        var agent = new PlainAgent(() => new AgentResponse
        {
            Text = "answer",
            // Still never read: nothing was sent, so there is nothing an echo could confirm.
            AdditionalProperties = new ThrowOnAccessDictionary()
        });

        var result = await RunAsync(dataset, agent, Options(dataset, temperature: 0.2, seed: 7));

        Assert.NotNull(result.AnswerSampling);
        var report = result.AnswerSampling;
        Assert.Equal(0.2, report.RequestedTemperature);
        Assert.Equal(7, report.RequestedSeed);
        Assert.Equal(1, report.Temperature.NotSupportedByAgentQuestions);
        Assert.Equal(1, report.Seed.NotSupportedByAgentQuestions);
        Assert.False(report.Temperature.CarriedByEveryQuestion);
        Assert.False(report.Seed.CarriedByEveryQuestion);
        Assert.Equal(
            AnswerSamplingDisposition.NotSupportedByAgent,
            Assert.Single(result.QuestionResults).AnswerSampling!.TemperatureDisposition);
    }

    [Fact]
    public async Task RunAsync_ProviderEchoesTheSeed_SeparatesEchoedFromMerelySent()
    {
        using var dataset = Dataset.Create();
        var agent = new SamplingAgent(request => new AgentResponse
        {
            Text = "answer",
            AdditionalProperties = new Dictionary<string, object?> { ["seed"] = request.Seed }
        });

        var result = await RunAsync(dataset, agent, Options(dataset, temperature: 0.0, seed: 4242));

        Assert.NotNull(result.AnswerSampling);
        var report = result.AnswerSampling;
        Assert.Equal(1, report.Seed.SentAndEchoedQuestions);
        Assert.True(report.Seed.ConfirmedByEveryQuestion);
        // The temperature was sent and nothing came back about it. That is weaker evidence, and it
        // stays weaker rather than being rounded up to the seed's confirmation.
        Assert.Equal(1, report.Temperature.SentUnverifiedQuestions);
        Assert.True(report.Temperature.CarriedByEveryQuestion);
        Assert.False(report.Temperature.ConfirmedByEveryQuestion);

        var outcome = Assert.Single(result.QuestionResults).AnswerSampling!;
        Assert.Equal(AnswerSamplingDisposition.SentAndEchoed, outcome.SeedDisposition);
        Assert.Equal(4242, outcome.EchoedSeed);
        Assert.Null(outcome.EchoedTemperature);
    }

    [Fact]
    public async Task RunAsync_ProviderEchoesADifferentSeed_ReportsTheDisagreement()
    {
        using var dataset = Dataset.Create();
        var agent = new SamplingAgent(_ => new AgentResponse
        {
            Text = "answer",
            AdditionalProperties = new Dictionary<string, object?> { ["seed"] = 99 }
        });

        var result = await RunAsync(dataset, agent, Options(dataset, temperature: null, seed: 7));

        var outcome = Assert.Single(result.QuestionResults).AnswerSampling!;
        Assert.Equal(AnswerSamplingDisposition.EchoedDifferentValue, outcome.SeedDisposition);
        Assert.Equal(99, outcome.EchoedSeed);
        Assert.Equal(AnswerSamplingDisposition.NotRequested, outcome.TemperatureDisposition);
        Assert.False(result.AnswerSampling!.Seed.ConfirmedByEveryQuestion);
        Assert.Equal(1, result.AnswerSampling.Seed.EchoedDifferentValueQuestions);
    }

    [Fact]
    public async Task RunAsync_AgentAppliesTemperatureButDeclinesSeed_RecordsEachParameterSeparately()
    {
        using var dataset = Dataset.Create();
        var agent = new SamplingAgent(
            _ => new AgentResponse { Text = "answer" },
            request => new AnswerSamplingAcknowledgement
            {
                TemperatureApplied = request.Temperature.HasValue,
                SeedApplied = false
            });

        var result = await RunAsync(dataset, agent, Options(dataset, temperature: 0.3, seed: 11));

        var outcome = Assert.Single(result.QuestionResults).AnswerSampling!;
        Assert.Equal(AnswerSamplingDisposition.SentUnverified, outcome.TemperatureDisposition);
        Assert.Equal(AnswerSamplingDisposition.DeclinedByAgent, outcome.SeedDisposition);
        Assert.True(result.AnswerSampling!.Temperature.CarriedByEveryQuestion);
        Assert.False(result.AnswerSampling.Seed.CarriedByEveryQuestion);
    }

    [Fact]
    public async Task RunAsync_ProviderRejectsTheTemperature_FailsTheQuestionInsteadOfDowngrading()
    {
        using var dataset = Dataset.Create();
        var agent = new SamplingAgent(_ => throw new InvalidOperationException(
            "HTTP 400 (invalid_request_error) Unsupported value: 'temperature' does not support 0 " +
            "with this model."));

        var result = await RunAsync(dataset, agent, Options(dataset, temperature: 0, seed: null));

        var question = Assert.Single(result.QuestionResults);
        Assert.Equal(QuestionExecutionStatus.AgentError, question.ExecutionStatus);
        Assert.Equal("answer_sampling_rejected", question.SafeFailureCode);
        Assert.Equal(
            AnswerSamplingDisposition.RejectedByProvider,
            question.AnswerSampling!.TemperatureDisposition);
        // One attempt: retrying without the parameter would produce a run that looks pinned and is not.
        Assert.Equal(1, agent.CallCount);
        Assert.Equal(1, result.AnswerSampling!.Temperature.RejectedByProviderQuestions);
        Assert.False(result.AnswerSampling.Temperature.CarriedByEveryQuestion);
    }

    [Fact]
    public async Task RunAsync_UnrelatedAgentFailure_IsNotBlamedOnSampling()
    {
        using var dataset = Dataset.Create();
        var agent = new SamplingAgent(_ => throw new HttpRequestException("connection reset"));

        var result = await RunAsync(dataset, agent, Options(dataset, temperature: 0.5, seed: 3));

        var question = Assert.Single(result.QuestionResults);
        Assert.Equal("agent_error", question.SafeFailureCode);
        Assert.Equal(
            AnswerSamplingDisposition.SentUnverified,
            question.AnswerSampling!.TemperatureDisposition);
        Assert.Equal(AnswerSamplingDisposition.SentUnverified, question.AnswerSampling.SeedDisposition);
    }

    [Fact]
    public void Validate_AnswerTemperatureOutOfRange_Throws()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExternalBenchmarkOptions { AnswerTemperature = 2.5 }.Validate());

        Assert.Equal(nameof(ExternalBenchmarkOptions.AnswerTemperature), error.ParamName);
    }

    [Fact]
    public async Task OracleReader_SamplingRequested_SendsItAndReportsOnlyWhatTheProviderReturned()
    {
        var client = new RecordingChatClient("oracle answer");
        var reader = new LongMemEvalOracleReader(client);

        reader.ConfigureAnswerSampling(new AnswerSamplingRequest { Temperature = 0.4, Seed = 123 });
        var response = await reader.InvokeAsync("Question?");

        var sent = Assert.Single(client.Options);
        Assert.Equal(0.4f, sent!.Temperature);
        Assert.Equal(123, sent.Seed);
        Assert.Equal("oracle answer", response.Text);
        // The provider echoed nothing, so nothing is echoed back — re-reporting the requested value
        // would manufacture the confirmation the request exists to establish.
        Assert.Null(response.AdditionalProperties);
    }

    [Fact]
    public async Task OracleReader_NoSamplingRequested_SendsNoChatOptions()
    {
        var client = new RecordingChatClient("oracle answer");
        var reader = new LongMemEvalOracleReader(client);

        await reader.InvokeAsync("Question?");

        Assert.Null(Assert.Single(client.Options));
    }

    private static ExternalBenchmarkOptions Options(Dataset dataset, double? temperature, int? seed) => new()
    {
        DatasetPath = dataset.Path,
        MaxJudgeRetries = 0,
        AnswerTemperature = temperature,
        AnswerSeed = seed
    };

    private static Task<ExternalBenchmarkResult> RunAsync(
        Dataset dataset,
        IEvaluableAgent agent,
        ExternalBenchmarkOptions options)
    {
        var judge = new RecordingChatClient("yes");
        var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset.Path);
        return runner.RunAsync(
            agent,
            new AgentBenchmarkConfig { AgentName = "subject", ModelId = "model" },
            options);
    }

    private sealed class PlainAgent(Func<AgentResponse> response) : IEvaluableAgent
    {
        public string Name => "plain";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(response());
    }

    private sealed class SamplingAgent(
        Func<AnswerSamplingRequest, AgentResponse> response,
        Func<AnswerSamplingRequest, AnswerSamplingAcknowledgement>? acknowledge = null)
        : IEvaluableAgent, IAnswerSamplingConfigurableAgent
    {
        private AnswerSamplingRequest _request = new();

        public string Name => "sampling";

        public int CallCount { get; private set; }

        public AnswerSamplingAcknowledgement ConfigureAnswerSampling(AnswerSamplingRequest request)
        {
            _request = request;
            return acknowledge is null
                ? AnswerSamplingAcknowledgement.AppliedFrom(request)
                : acknowledge(request);
        }

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(response(_request));
        }
    }

    private sealed class RecordingChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
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

    /// <summary>Fails the test if AgentEval reads the agent's property bag when it must not.</summary>
    private sealed class ThrowOnAccessDictionary : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException("Property bag was read.");

        public IEnumerable<string> Keys => throw new InvalidOperationException("Property bag was read.");

        public IEnumerable<object?> Values => throw new InvalidOperationException("Property bag was read.");

        public int Count => throw new InvalidOperationException("Property bag was read.");

        public bool ContainsKey(string key) => throw new InvalidOperationException("Property bag was read.");

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => throw new InvalidOperationException("Property bag was read.");

        public bool TryGetValue(string key, out object? value)
            => throw new InvalidOperationException("Property bag was read.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => throw new InvalidOperationException("Property bag was read.");
    }

    private sealed class Dataset : IDisposable
    {
        public string Path { get; }

        private Dataset(string path) => Path = path;

        public static Dataset Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-sampling-{Guid.NewGuid():N}.json");
            var questions = new[]
            {
                new
                {
                    question_id = "q-sampling",
                    question_type = "single-session-user",
                    question = "What should be recalled?",
                    answer = "gold answer",
                    question_date = "2026/07/29 (Wed) 00:00",
                    haystack_sessions = new[]
                    {
                        new object[]
                        {
                            new { role = "user", content = "evidence", has_answer = true },
                            new { role = "assistant", content = "ack", has_answer = false }
                        }
                    },
                    haystack_dates = new[] { "2026/07/01 (Wed) 10:00" },
                    haystack_session_ids = new[] { "answer-session" },
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
