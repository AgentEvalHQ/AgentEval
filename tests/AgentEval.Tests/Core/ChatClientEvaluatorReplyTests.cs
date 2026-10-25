// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Tests.Evals;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// T04: how <see cref="ChatClientEvaluator"/> reads a judge's reply. It took the first fence, or the first '{' to the last
/// '}', so a reasoning block or a trailing note hid the verdict; it clamped a score off 0-100 into a confident grade; and
/// its corrective retry never showed the judge the reply it could not parse.
/// </summary>
public sealed class ChatClientEvaluatorReplyTests
{
    [Fact]
    public void AScoreOff0To100_IsOutOfContract_NeverClamped()
    {
        // It was clamped: 150 read as a confident 100.
        var parsed = ChatClientEvaluator.ParseEvaluationResponse("""{"overallScore": 150, "summary": "s"}""");

        Assert.True(parsed.EvaluationFailed);
        Assert.Contains("Out of contract", parsed.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<think>{x}</think>{\"overallScore\":80}")]   // the first brace is in the reasoning
    [InlineData("```JSON\n{\"overallScore\":80}\n```")]        // an uppercase language tag
    [InlineData("{\"overallScore\":80} Note: {see above}")]    // first '{' to last '}' spans both
    [InlineData("{\"overallScore\":80,}")]                     // a trailing comma
    public void TheVerdict_IsFoundWhereverTheReplyPutsIt(string reply)
    {
        var parsed = ChatClientEvaluator.ParseEvaluationResponse(reply);

        Assert.False(parsed.EvaluationFailed, parsed.Summary);
        Assert.Equal(80, parsed.OverallScore);
    }

    [Fact]
    public void VerdictsWithDifferentScores_AreRefused_NotGuessed()
    {
        var parsed = ChatClientEvaluator.ParseEvaluationResponse("""{"overallScore": 20} and then {"overallScore": 90}""");

        Assert.True(parsed.EvaluationFailed);
    }

    [Fact]
    public async Task TheCorrectiveRetry_ShowsTheJudgeTheReplyItCouldNotParse()
    {
        // The retry said "your previous response could not be parsed" to a judge that had never been shown it.
        const string Unreadable = "The answer looks good, I would say 80 out of 100.";
        var calls = 0;
        var client = new RubricJudgeTests.RecordingChatClient(_ =>
            Interlocked.Increment(ref calls) == 1 ? Unreadable : """{"overallScore": 80}""");

        var result = await new ChatClientEvaluator(client).EvaluateAsync("q", "a", ["Is good"]);

        Assert.False(result.EvaluationFailed, result.Summary);
        Assert.Equal(2, client.Calls.Count);
        var retry = client.Calls[1];
        Assert.Contains(retry, m => m.Role == ChatRole.Assistant && m.Text == Unreadable);
        Assert.Equal(ChatRole.User, retry[retry.Count - 1].Role);   // the nudge follows the reply it is about
    }
}
