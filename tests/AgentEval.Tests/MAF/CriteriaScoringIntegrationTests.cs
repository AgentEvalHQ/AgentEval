// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Comparison;
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using AgentEval.Tests.Evals;
using Xunit;

namespace AgentEval.Tests.MAF;

public sealed class CriteriaScoringIntegrationTests
{
    private const string Reply = """
        {"overallScore":90,"criteriaResults":[
          {"id":"support","criterion":"PRIVATE_CRITERION Stöds av källan","met":true},
          {"id":"concise","met":true},{"id":"citation","met":false}]}
        """;
    private static readonly JudgeCriterion[] Criteria =
        [new("support", "PRIVATE_CRITERION Stöds av källan"), new("concise", "Kort svar"), new("citation", "Har källhänvisning")];
    private static EvaluationOptions Options(JudgeScorePolicy policy = JudgeScorePolicy.CriteriaMetFraction) =>
        new() { ScorePolicy = policy, Verbose = false };
    private static TestCase Case(int threshold = 67) => new()
    {
        Name = "Swedish RAG", Input = "PRIVATE_QUERY När?", JudgeCriteria = Criteria,
        Context = ["PRIVATE_FIXTURE fallback"], RequiresContext = true, PassingScore = threshold
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluate_TwoOfThree_DoesNotRoundAcrossThreshold(bool streaming)
    {
        using var client = new RubricJudgeTests.RecordingChatClient(messages =>
        {
            Assert.Contains("PRIVATE_RETRIEVAL", messages[1].Text!);
            Assert.DoesNotContain("PRIVATE_FIXTURE", messages[1].Text!);
            Assert.Contains("\"id\":\"support\"", messages[1].Text!);
            return Reply;
        });
        var harness = new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(new Agent(), Case(), options: Options())
            : await harness.RunEvaluationAsync(new Agent(), Case(), Options());
        Assert.False(result.JudgeFailed);
        Assert.False(result.Passed);
        Assert.Equal(67, result.Score);
        Assert.Equal(200.0 / 3, result.MeasuredScore);
        Assert.Equal(90, result.ScoreSelection!.RawOverallScore);
        Assert.Equal(EvaluationContextSource.Retrieved, result.JudgeContextSource);
        Assert.Contains("66.67", result.Failure!.WhyItFailed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stochastic_ConcurrentCriteriaScoring_ExactStatisticsAndSealedArtifact(bool analyze)
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => Reply);
        var harness = new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance);
        var runner = new StochasticRunner(harness, evaluationOptions: Options());
        var started = DateTimeOffset.UtcNow;
        var result = await runner.RunStochasticTestAsync(new Agent(), Case(),
            new StochasticOptions(Runs: 3, MaxParallelism: 3, EnableStatisticalAnalysis: analyze));
        Assert.Equal(0, result.NotMeasuredCount);
        Assert.False(result.Passed);
        Assert.Equal(200.0 / 3, result.Statistics!.MeanScore, 10);
        Assert.Equal(200.0 / 3, result.Statistics.MedianScore, 10);
        Assert.Equal(200.0 / 3, result.Statistics.ExactMinScore, 10);
        Assert.Equal(200.0 / 3, result.Statistics.ExactMaxScore, 10);
        Assert.Throws<StochasticAssertionException>(() => result.Should().HaveMinScoreAtLeast(67));
        result.Should().HaveMinScoreAtLeast(66);
        if (analyze)
        {
            Assert.Equal(200.0 / 3, result.Statistics.Percentile95!.Value, 10);
            Assert.Equal(200.0 / 3, result.Statistics.ConfidenceInterval!.Lower, 10);
        }
        else Assert.Null(result.Statistics.ConfidenceInterval);
        Assert.Equal(200.0 / 3, new TestSummary("Swedish", result.IndividualResults).AverageScore!.Value, 10);
        var report = new TestSummary("Swedish", result.IndividualResults).ToEvaluationReport();
        Assert.All(report.TestResults, r => Assert.Equal(200.0 / 3, r.Score!.Value, 10));

        var root = Path.Combine(Path.GetTempPath(), "agenteval-score-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (directory, hash) = AefEvalWriter.WriteTrials(root, [result.IndividualResults], ["swedish"], 3,
                new AefEvalRunOptions
                {
                    SubjectRef = "agent:score-fixture", TargetMode = AefTargetMode.Scripted,
                    DatasetName = "scoring", DatasetBytes = Encoding.UTF8.GetBytes("fixture"),
                    JudgeModel = "scripted", ContentCapture = AefContentCapture.Off
                }, started, DateTimeOffset.UtcNow);
            Assert.NotNull(hash);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            var lines = File.ReadLines(Path.Combine(directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
            Assert.Equal(4, lines.Length);
            Assert.All(lines, line => Assert.Equal(200.0 / 3, (double)line["scores"]![0]!["value"]!, 10));
            foreach (var line in lines.Where(l => l["trial"] is not null))
            {
                var selection = line["ext"]!["agenteval.scoreSelection"]!;
                Assert.Equal(JudgeScoreSelection.FractionPolicyId, (string?)selection["policyId"]);
                Assert.Equal(90, (int)selection["rawOverallScore"]!);
                Assert.Equal(3, (int)selection["applicableCount"]!);
                Assert.Equal("retrieved", (string?)line["ext"]!["agenteval.judgeContext"]!["source"]);
            }
            var bytes = string.Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.DoesNotContain("PRIVATE_", bytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Evaluate_ConcurrentDifferentPolicies_DoNotMutateSharedJudgeOrCriteria()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => Reply);
        var harness = new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance);
        var results = await Task.WhenAll(
            harness.RunEvaluationAsync(new Agent(), Case(), Options()),
            harness.RunEvaluationAsync(new Agent(), Case(), Options(JudgeScorePolicy.JudgeOverall)));
        Assert.False(results[0].Passed);
        Assert.True(results[1].Passed);
        Assert.Equal(90, results[1].MeasuredScore);
        Assert.NotEqual(results[0].JudgeRubricDigest, results[1].JudgeRubricDigest);
        Assert.Equal(3, Criteria.Length);
    }

    [Theory]
    [InlineData("no-judge")]
    [InlineData("no-criteria")]
    [InlineData("disabled")]
    [InlineData("missing-verdict")]
    public async Task Stochastic_MissingPrerequisite_IsUnmeasuredNotOutputPresenceSuccess(string failure)
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => failure == "missing-verdict" ? """{"overallScore":90}""" : Reply);
        var harness = new MAFEvaluationHarness(failure == "no-judge" ? null : new ChatClientEvaluator(client), NullAgentEvalLogger.Instance);
        var runner = new StochasticRunner(harness, evaluationOptions: new()
        {
            ScorePolicy = JudgeScorePolicy.CriteriaMetFraction, EvaluateResponse = failure != "disabled", Verbose = false
        });
        var testCase = failure == "no-criteria" ? new TestCase { Name = "empty", Input = "q" } : Case();
        var result = await runner.RunStochasticTestAsync(new Agent(), testCase, new StochasticOptions(Runs: 3, MaxParallelism: 3));
        Assert.Equal(3, result.NotMeasuredCount);
        Assert.Null(result.Statistics);
        Assert.Equal(StochasticVerdict.NotMeasured, result.Verdict);
        Assert.All(result.IndividualResults, r => { Assert.True(r.JudgeFailed); Assert.Null(r.MeasuredScore); });
        if (failure is "no-judge" or "disabled") Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Evaluate_CallerMutatesCriteriaDuringAgentCall_UsesOriginalSnapshot()
    {
        var mutable = Criteria.ToList();
        using var client = new RubricJudgeTests.RecordingChatClient(_ => Reply);
        var result = await new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance)
            .RunEvaluationAsync(new Agent(mutable.Clear), new TestCase { Name = "snapshot", Input = "q", JudgeCriteria = mutable }, Options());
        Assert.Equal(200.0 / 3, result.MeasuredScore);
        Assert.Equal(3, result.ScoreSelection!.ExpectedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Harness_ToGroundedness_UsesCapturedContextEvenWhenResponseJudgeDisabled(bool streaming)
    {
        var harness = new MAFEvaluationHarness(evaluator: null, logger: NullAgentEvalLogger.Instance);
        var testCase = Case();
        var options = new EvaluationOptions { EvaluateResponse = false, Verbose = false };
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(new Agent(), testCase, options: options)
            : await harness.RunEvaluationAsync(new Agent(), testCase, options);
        using var client = new RubricJudgeTests.RecordingChatClient(messages =>
        {
            Assert.Contains("PRIVATE_RETRIEVAL", messages[1].Text!);
            Assert.DoesNotContain("PRIVATE_FIXTURE", messages[1].Text!);
            return """{"score":1}""";
        });
        var verdict = await new GroundednessEval(new ChatClientEvaluator(client), "scripted", 0.75, GroundednessOptions.SupportFocused)
            .EvaluateAsync(testCase.ToEvalInput(result));
        Assert.True(verdict.Score.Passed);
        Assert.Equal(3, client.Calls.Count);
        Assert.DoesNotContain("PRIVATE_RETRIEVAL", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public void ToEvalInput_CapturedEmptyContext_DoesNotFallBackToFixture()
    {
        var result = new TestResult { TestName = "empty", JudgeContext = [] };
        Assert.Equal("", Case().ToEvalInput(result).Context);
        Assert.Equal("PRIVATE_FIXTURE fallback", Case().ToEvalInput(new TestResult { TestName = "fixture" }).Context);
    }

    [Fact]
    public async Task Stochastic_MixedScoresAndOneMissingVerdict_ExcludesMissingWithoutRoundingSamples()
    {
        int call = -1;
        using var client = new RubricJudgeTests.RecordingChatClient(_ => Interlocked.Increment(ref call) switch
        {
            0 => Reply,
            1 => Reply.Replace("\"id\":\"concise\",\"met\":true", "\"id\":\"concise\",\"met\":false", StringComparison.Ordinal),
            _ => """{"overallScore":90}"""
        });
        var result = await new StochasticRunner(new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance),
            evaluationOptions: Options()).RunStochasticTestAsync(new Agent(), Case(), new StochasticOptions(Runs: 3, MaxParallelism: 3));
        Assert.Equal(1, result.NotMeasuredCount);
        Assert.Equal(2, result.Statistics!.SampleSize);
        Assert.Equal(50, result.Statistics.MeanScore, 10);
        Assert.Equal(Math.Sqrt(2) * 50 / 3, result.Statistics.StandardDeviation!.Value, 10);
        Assert.Equal(100.0 / 3, result.Statistics.ExactMinScore, 10);
        Assert.Equal(200.0 / 3, result.Statistics.ExactMaxScore, 10);
    }

    [Fact]
    public async Task Export_ExplicitInapplicableCriterion_PreservesAbsenceAndValidAggregation()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => Reply.Replace(
            "\"id\":\"citation\",\"met\":false", "\"id\":\"citation\",\"applicable\":false", StringComparison.Ordinal));
        var result = await new MAFEvaluationHarness(new ChatClientEvaluator(client), NullAgentEvalLogger.Instance)
            .RunEvaluationAsync(new Agent(), Case(), Options());
        Assert.Equal(100, result.MeasuredScore);
        var root = Path.Combine(Path.GetTempPath(), "agenteval-score-na-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (directory, _) = AefEvalWriter.Write(root, new TestSummary("score", [result]), ["swedish"],
                new AefEvalRunOptions { SubjectRef = "agent:fixture", TargetMode = AefTargetMode.Scripted,
                    DatasetName = "criteria", DatasetBytes = Encoding.UTF8.GetBytes("fixture"), JudgeModel = "scripted" },
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [67]);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            var lines = File.ReadLines(Path.Combine(directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
            Assert.Contains(lines, l => (string?)l["state"] == "not_applicable");
            Assert.Contains(lines, l => (double?)l["aggregation"]?["score"] == 100);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Stochastic_IncompatiblePolicies_RefusesToPoolScores()
    {
        var runner = new StochasticRunner(new MixedPolicyHarness());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunStochasticTestAsync(
            new Agent(), Case(), new StochasticOptions(Runs: 3)));
        Assert.Contains("incompatible score policies", error.Message);
    }

    private sealed class MixedPolicyHarness : IEvaluationHarness
    {
        private int _call;
        public Task<TestResult> RunEvaluationAsync(IEvaluableAgent agent, TestCase testCase, EvaluationOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new TestResult
            {
                TestName = testCase.Name, Score = 50,
                ScoreSelection = new()
                {
                    PolicyId = Interlocked.Increment(ref _call) == 1 ? JudgeScoreSelection.FractionPolicyId : JudgeScoreSelection.OverallPolicyId,
                    SelectedScore = 50
                }
            });
    }

    private sealed class Agent(Action? onInvoke = null) : IStreamableAgent
    {
        public string Name => "RAG fixture";
        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            onInvoke?.Invoke();
            await Task.Yield();
            return new() { Text = "15 maj.", RetrievedContext = ["PRIVATE_RETRIEVAL 15 maj."] };
        }
        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await InvokeAsync(prompt, cancellationToken);
            yield return new() { Text = response.Text, RetrievedContext = response.RetrievedContext, IsComplete = true };
        }
    }
}
