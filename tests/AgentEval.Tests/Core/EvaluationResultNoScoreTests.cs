// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Core;

public class EvaluationResultNoScoreTests
{
    [Theory]
    [InlineData("no json")]
    [InlineData("{}")]
    [InlineData("{\"overallScore\":101}")]
    public void ParseEvaluationResponse_InvalidVerdict_HasNoScore(string reply)
    {
        var result = ChatClientEvaluator.ParseEvaluationResponse(reply);
        Assert.True(result.EvaluationFailed);
        Assert.Null(result.OverallScore);
    }

    [Fact]
    public void OverallScore_FailedProducerWithPlaceholder_IsNullInMemoryAndJson()
    {
        var result = new EvaluationResult { OverallScore = 50, EvaluationFailed = true };
        Assert.Null(result.OverallScore);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        Assert.Equal(JsonValueKind.Null, json.GetProperty("OverallScore").ValueKind);
        Assert.Null(JsonSerializer.Deserialize<EvaluationResult>("{\"OverallScore\":50,\"EvaluationFailed\":true}")!.OverallScore);
    }

    [Fact]
    public void OverallScore_Unset_IsAbsent()
    {
        Assert.Null(new EvaluationResult().OverallScore);
    }

    [Fact]
    public void OverallScore_MeasuredZero_IsPreserved()
    {
        Assert.Equal(0, ChatClientEvaluator.ParseEvaluationResponse("{\"overallScore\":0}").OverallScore);
    }

    [Fact]
    public async Task AtomicLlmEval_UnsetScore_IsErrorEvenAtZeroThreshold()
    {
        var eval = new AtomicLlmEval(new Stub(new EvaluationResult()), "test", "test", "quality", "1",
            ["accuracy"], passThreshold: 0);
        var result = await eval.EvaluateAsync(new EvalInput(Query: "q", Response: "a"));
        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Null(result.Score.MeasuredValue());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CalibratedEvaluator_UnsetScore_IsNotAVote(int required)
    {
        var eval = new CalibratedEvaluator(
            [("real", new Stub(new EvaluationResult { OverallScore = 80 })), ("missing", new Stub(new EvaluationResult()))],
            new CalibratedJudgeOptions { MinimumJudgesRequired = required, Strategy = VotingStrategy.Mean });
        if (required == 2)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => eval.EvaluateAsync("q", "a", ["accuracy"]));
            Assert.Contains("1 of 2 judges succeeded", error.Message);
        }
        else
            Assert.Equal(80, (await eval.EvaluateAsync("q", "a", ["accuracy"])).OverallScore);
    }

    private sealed class Stub(EvaluationResult result) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
