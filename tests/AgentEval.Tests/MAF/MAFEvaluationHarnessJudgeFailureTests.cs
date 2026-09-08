// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.MAF;

/// <summary>
/// ADR-030 Slice 0.2 (defect D-b). A judge that failed to produce a verdict returns the
/// conventional fallback <c>OverallScore = 50</c> with <c>EvaluationFailed = true</c>
/// (<c>ChatClientEvaluator.ParseEvaluationResponse</c>). <c>MAFEvaluationHarness</c> compared that
/// fallback against <c>PassingScore</c> and never read <c>EvaluationFailed</c>, so any test case
/// with <c>PassingScore &lt;= 50</c> passed on a judge parse failure. Non-optional, no opt-out.
/// </summary>
public class MAFEvaluationHarnessJudgeFailureTests
{
    /// <summary>The exact shape <c>ChatClientEvaluator</c> returns when the judge does not speak.</summary>
    private sealed class ParseFailingJudge : IEvaluator
    {
        public int Calls { get; private set; }

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = EvaluationDefaults.DefaultFailureScore,
                Summary = "Failed to parse evaluation - no JSON found",
                EvaluationFailed = true,
            });
        }
    }

    private sealed class RealVerdictJudge(int score) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = score, Summary = "real verdict" });
    }

    private sealed class TextAgent(string text) : IStreamableAgent
    {
        public string Name => "TextAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = text });

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseChunk { Text = text };
            yield return new AgentResponseChunk { IsComplete = true };
            await Task.CompletedTask;
        }
    }

    private static TestCase Case(int passingScore) => new()
    {
        Name = "judge-failure",
        Input = "What is 2+2?",
        EvaluationCriteria = new[] { "Answers the question" },
        PassingScore = passingScore,
    };

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(70)]
    public async Task JudgeParseFailure_NeverPasses(int passingScore)
    {
        // The §8 acceptance test. Pre-fix: PassingScore 0/40/50 all PASS on a fallback 50.
        var judge = new ParseFailingJudge();
        var harness = new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance);

        var result = await harness.RunEvaluationAsync(new TextAgent("Four."), Case(passingScore));

        Assert.Equal(1, judge.Calls);
        Assert.False(result.Passed, $"a judge that produced no verdict must not pass at PassingScore={passingScore}");
        Assert.NotEqual(EvaluationDefaults.DefaultFailureScore, result.Score);
        Assert.Contains("no verdict", result.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Failed to parse evaluation", result.Details, StringComparison.Ordinal);
        Assert.NotNull(result.Failure);
        Assert.Contains(result.Failure!.Reasons, r => r.Category == "Judge");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(50)]
    public async Task JudgeParseFailure_StreamingPath_NeverPasses(int passingScore)
    {
        // The streaming branch is a verbatim copy of the non-streaming one and had the same hole.
        var judge = new ParseFailingJudge();
        var harness = new MAFEvaluationHarness(judge, NullAgentEvalLogger.Instance);

        var result = await harness.RunEvaluationStreamingAsync(new TextAgent("Four."), Case(passingScore));

        Assert.Equal(1, judge.Calls);
        Assert.False(result.Passed);
        Assert.NotEqual(EvaluationDefaults.DefaultFailureScore, result.Score);
        Assert.Contains("no verdict", result.Details, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Failure);
        Assert.Contains(result.Failure!.Reasons, r => r.Category == "Judge");
    }

    [Fact]
    public async Task JudgeParseFailure_IsNotAnAgentError()
    {
        // A judge that did not speak is an evaluation-infrastructure failure, not an exception thrown
        // by the agent: TestResult.Error stays null, the agent's output is still recorded.
        var harness = new MAFEvaluationHarness(new ParseFailingJudge(), NullAgentEvalLogger.Instance);

        var result = await harness.RunEvaluationAsync(new TextAgent("Four."), Case(40));

        Assert.Null(result.Error);
        Assert.Equal("Four.", result.ActualOutput);
    }

    [Theory]
    [InlineData(50, 40, true)]
    [InlineData(50, 70, false)]
    [InlineData(100, 70, true)]
    public async Task RealJudgeVerdict_StillComparedAgainstPassingScore(int judgeScore, int passingScore, bool expectedPassed)
    {
        // Guard: a REAL 50 from a judge that did speak is still a real 50. The fix keys on
        // EvaluationFailed, not on the score value.
        var harness = new MAFEvaluationHarness(new RealVerdictJudge(judgeScore), NullAgentEvalLogger.Instance);

        var result = await harness.RunEvaluationAsync(new TextAgent("Four."), Case(passingScore));

        Assert.Equal(expectedPassed, result.Passed);
        Assert.Equal(judgeScore, result.Score);
    }
}
