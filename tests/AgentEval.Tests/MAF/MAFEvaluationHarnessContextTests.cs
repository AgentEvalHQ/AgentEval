// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Tests.Evals;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF;

public sealed class MAFEvaluationHarnessContextTests
{
    private const string Passage = "Project Aurora's approved launch date is 15 May.";
    private const string Query = "What is Project Aurora's approved launch date?";
    private const string Criterion = "Every answer claim is supported by the retrieved context.";
    private const string Verdict = """{"overallScore":100,"summary":"Supported"}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_DatasetContext_ReachesJudgeWithoutChangingAgentInput(bool streaming)
    {
        var client = new RubricJudgeTests.RecordingChatClient(_ => Verdict);
        var harness = new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance);
        var agent = new TextAgent();
        var testCase = new DatasetTestCase
        {
            Input = Query,
            Context = [Passage],
            EvaluationCriteria = [Criterion]
        }.ToTestCase();

        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(agent, testCase)
            : await harness.RunEvaluationAsync(agent, testCase);

        Assert.Equal(Query, agent.Input);
        var prompt = Assert.Single(Assert.Single(client.Calls), m => m.Role == ChatRole.User).Text;
        Assert.Contains(Passage, prompt);
        Assert.True(result.Passed);
        Assert.Equal(EvaluationContextSource.Fixture, result.JudgeContextSource);
        Assert.Equal(1, result.JudgeContextDocumentCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunEvaluation_CapturedRetrieval_TakesPrecedenceEvenWhenEmpty(bool streaming, bool empty)
    {
        var judge = new RecordingEvaluator();
        var harness = new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance);
        var context = empty ? Array.Empty<string>() : new[] { "runtime passage one", "runtime passage two" };
        var agent = new TextAgent(context);
        var testCase = Case([Passage], requiresContext: true);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(agent, testCase)
            : await harness.RunEvaluationAsync(agent, testCase);

        var request = Assert.Single(judge.Requests);
        Assert.Equal(context, request.Context);
        Assert.Equal(EvaluationContextSource.Retrieved, request.ContextSource);
        Assert.Equal(context.Length, result.JudgeContextDocumentCount);
        Assert.True(result.Passed);
        Assert.Equal(new[] { Passage }, testCase.Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_RequiredContextMissing_IsUnmeasuredWithoutCallingJudge(bool streaming)
    {
        var judge = new RecordingEvaluator();
        var harness = new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(new TextAgent(), Case(requiresContext: true))
            : await harness.RunEvaluationAsync(new TextAgent(), Case(requiresContext: true));

        Assert.Empty(judge.Requests);
        Assert.True(result.JudgeFailed);
        Assert.False(result.Passed);
        Assert.False(StochasticResult.IsMeasured(result));
        Assert.Equal(EvaluationContextSource.None, result.JudgeContextSource);
        Assert.Null(result.JudgeContextDocumentCount);
        Assert.Contains("context is unavailable", result.Details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_LegacyEvaluatorWithContext_RefusesToDiscardEvidence(bool streaming)
    {
        var judge = new LegacyEvaluator();
        var harness = new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(new TextAgent(), Case([Passage]))
            : await harness.RunEvaluationAsync(new TextAgent(), Case([Passage]));

        Assert.Equal(0, judge.Calls);
        Assert.True(result.JudgeFailed);
        Assert.Contains("context-capable", result.Details);
    }

    [Fact]
    public async Task RunEvaluation_ContextFreeLegacyEvaluator_PreservesCompatibility()
    {
        var judge = new LegacyEvaluator();
        var result = await new MAFEvaluationHarness(new ContextFreeDecorator(judge), NullAgentEvalLogger.Instance)
            .RunEvaluationAsync(new TextAgent(), Case());

        Assert.Equal(1, judge.Calls);
        Assert.True(result.Passed);
        Assert.Null(result.JudgeContextSource);
    }

    [Fact]
    public async Task RunEvaluationStreaming_ContextOnIncompleteChunk_DoesNotReplaceFixture()
    {
        var judge = new RecordingEvaluator();
        await new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance).RunEvaluationStreamingAsync(
            new TextAgent(incompleteContext: ["not a completed retrieval capture"]), Case([Passage], true));
        var request = Assert.Single(judge.Requests);
        Assert.Equal(Passage, Assert.Single(request.Context!));
        Assert.Equal(EvaluationContextSource.Fixture, request.ContextSource);
    }

    [Fact]
    public async Task RunEvaluation_ResponseEvaluationDisabled_DoesNotInvokeContextJudge()
    {
        var judge = new RecordingEvaluator();
        var result = await new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance).RunEvaluationAsync(
            new TextAgent(), Case(requiresContext: true), new EvaluationOptions { EvaluateResponse = false });
        Assert.Empty(judge.Requests);
        Assert.Null(result.JudgeContextSource);
        Assert.False(result.JudgeFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_RequiredContextWithNoEvaluator_IsUnmeasured(bool streaming)
    {
        var harness = new MAFEvaluationHarness(verbose: false);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(new TextAgent(), Case([Passage], true))
            : await harness.RunEvaluationAsync(new TextAgent(), Case([Passage], true));
        Assert.True(result.JudgeFailed);
        Assert.False(result.Passed);
        Assert.Contains("no evaluator", result.Details);
    }

    [Fact]
    public async Task RunEvaluation_FixtureChangesDuringAgentInvocation_UsesStartOfRunSnapshot()
    {
        var fixture = new List<string> { Passage };
        var judge = new RecordingEvaluator();
        var agent = new TextAgent(onInvoke: () => fixture[0] = "changed after execution started");
        await new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance)
            .RunEvaluationAsync(agent, Case(fixture));
        Assert.Equal(Passage, Assert.Single(Assert.Single(judge.Requests).Context!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RunStochasticTest_PerInvocationRetrieval_RemainsIsolated(int parallelism)
    {
        var judge = new RecordingEvaluator();
        var runner = new StochasticRunner(new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance));
        var result = await runner.RunStochasticTestAsync(new TrialAgent(), Case(["fixture"], true),
            new StochasticOptions(Runs: 8, MaxParallelism: parallelism));

        Assert.Equal(8, judge.Requests.Count);
        Assert.Equal(8, judge.Requests.Select(r => r.Output).Distinct().Count());
        Assert.All(judge.Requests, request =>
        {
            Assert.Equal("evidence for " + request.Output, Assert.Single(request.Context!));
            Assert.Equal(EvaluationContextSource.Retrieved, request.ContextSource);
        });
        Assert.Equal(0, result.NotMeasuredCount);
    }

    [Fact]
    public async Task RunStochasticTest_RequiredContextMissing_DoesNotEnterStatistics()
    {
        var judge = new RecordingEvaluator();
        var runner = new StochasticRunner(new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance));
        var result = await runner.RunStochasticTestAsync(new TextAgent(), Case(requiresContext: true),
            new StochasticOptions(Runs: 3));
        Assert.Equal(3, result.NotMeasuredCount);
        Assert.Null(result.Statistics);
        Assert.Empty(judge.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapCase_ContextJudging_RecordsOnlySourceAndCount(bool streaming)
    {
        var harness = new MAFEvaluationHarness(new RecordingEvaluator(), NullAgentEvalLogger.Instance);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(new TextAgent([Passage]), Case())
            : await harness.RunEvaluationAsync(new TextAgent([Passage]), Case());
        var mapped = AefEvalWriter.MapCase(result, "case-1");
        var metadata = mapped.Root.Ext!["agenteval.judgeContext"]!;
        Assert.Equal("retrieved", metadata["source"]!.GetValue<string>());
        Assert.Equal(1, metadata["documentCount"]!.GetValue<int>());
        Assert.DoesNotContain(Passage, System.Text.Json.JsonSerializer.Serialize(mapped));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatasetToSealedAef_WithOrWithoutRequiredEvidence_PreservesVerdictAndPrivacy(bool missing)
    {
        var root = Path.Combine(Path.GetTempPath(), "agenteval-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string secret = "PRIVATE_RETRIEVAL Aurora launches on 15 May.";
            var datasetBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                id = "aurora", input = "PRIVATE_QUERY When does Aurora launch?",
                context = missing ? null : new[] { secret }, evaluation_criteria = new[] { Criterion }
            }) + "\n");
            var path = Path.Combine(root, "cases.jsonl");
            await File.WriteAllBytesAsync(path, datasetBytes);
            var dataset = Assert.Single(await new JsonlDatasetLoader().LoadAsync(path));
            var testCase = dataset.ToTestCase(requiresContext: true);
            var client = new RubricJudgeTests.RecordingChatClient(messages =>
            {
                Assert.Contains(secret, messages[1].Text!);
                return Verdict;
            });
            var started = DateTimeOffset.UtcNow;
            var run = await new StochasticRunner(new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance))
                .RunStochasticTestAsync(new TextAgent(), testCase, new StochasticOptions(Runs: 3, MaxParallelism: 3));
            var (directory, hash) = AefEvalWriter.WriteTrials(Path.Combine(root, "runs"),
                [run.IndividualResults], [dataset.Id], 3,
                new AefEvalRunOptions
                {
                    SubjectRef = "agent:context-fixture", TargetMode = AefTargetMode.Scripted,
                    DatasetName = "context-cases", DatasetBytes = datasetBytes, JudgeModel = "scripted-judge",
                    ContentCapture = AefContentCapture.Off
                }, started, DateTimeOffset.UtcNow);

            Assert.NotNull(hash);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            var trials = File.ReadLines(Path.Combine(directory, "results.ndjson"))
                .Select(line => JsonNode.Parse(line)!).Where(line => line["trial"] is not null).ToArray();
            Assert.Equal(3, trials.Length);
            Assert.All(trials, trial =>
            {
                var evidence = trial["ext"]!["agenteval.judgeContext"]!;
                Assert.Equal(missing ? "none" : "fixture", (string?)evidence["source"]);
                Assert.Equal(missing ? null : (int?)1, (int?)evidence["documentCount"]);
                if (missing) Assert.Null(trial["scores"]);
                else Assert.Equal(100, (int)trial["scores"]![0]!["value"]!);
            });
            Assert.Equal(missing ? 3 : 0, run.NotMeasuredCount);
            Assert.Equal(missing ? 0 : 3, client.Calls.Count);
            var captured = string.Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.DoesNotContain(secret, captured);
            Assert.DoesNotContain("PRIVATE_QUERY", captured);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static TestCase Case(IReadOnlyList<string>? context = null, bool requiresContext = false) => new()
    {
        Name = "Aurora", Input = Query, EvaluationCriteria = [Criterion],
        Context = context, RequiresContext = requiresContext, PassingScore = 0
    };

    private sealed class RecordingEvaluator : IEvaluator
    {
        public System.Collections.Concurrent.ConcurrentBag<EvaluationRequest> Requests { get; } = [];
        public async Task<EvaluationResult> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return new EvaluationResult { OverallScore = 100 };
        }
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Expected the request overload.");
    }

    private sealed class LegacyEvaluator : IEvaluator
    {
        public int Calls { get; private set; }
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationResult { OverallScore = 100 });
        }
    }

    private sealed class TrialAgent : IEvaluableAgent
    {
        private int _trial;
        public string Name => "trial";
        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var trial = Interlocked.Increment(ref _trial).ToString();
            await Task.Yield();
            return new AgentResponse { Text = trial, RetrievedContext = ["evidence for " + trial] };
        }
    }

    private sealed class ContextFreeDecorator(LegacyEvaluator inner) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) => inner.EvaluateAsync(input, output, criteria, cancellationToken);

        public Task<EvaluationResult> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A context-free decorator must retain the original call path.");
    }

    private sealed class TextAgent(IReadOnlyList<string>? context = null, Action? onInvoke = null,
        IReadOnlyList<string>? incompleteContext = null) : IStreamableAgent
    {
        public string Name => "Aurora";
        public string? Input { get; private set; }

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Input = prompt;
            onInvoke?.Invoke();
            return Task.FromResult(new AgentResponse { Text = "15 May.", RetrievedContext = context });
        }

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Input = prompt;
            onInvoke?.Invoke();
            if (incompleteContext is not null)
                yield return new AgentResponseChunk { RetrievedContext = incompleteContext };
            yield return new AgentResponseChunk { Text = "15 May.", IsComplete = true, RetrievedContext = context };
            await Task.CompletedTask;
        }
    }
}
