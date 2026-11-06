// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Tests.Microsoft365.Agent;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="Microsoft365Evidence.IsAnswerMeasured(WorkIqTurnResult)"/> and its <see cref="AgentResponse"/> overload: the
/// one rule every host uses. Only a turn whose several answer candidates were left unchosen (<c>Ambiguous</c>) is not
/// measured; a completed turn with no answer at all (<c>None</c>) is measured and fails an answer check.
/// </summary>
public sealed class IsAnswerMeasuredTests
{
    private static WorkIqTurnResult Completed(OutputCompleteness completeness) =>
        TurnResults.Completed(new WorkIqTurnRequest("x", "case-1", "turn-1")) with
        {
            AnswerCompleteness = completeness,
            AnswerText = completeness is OutputCompleteness.Complete or OutputCompleteness.Empty ? "answer" : null,
        };

    [Theory]
    [InlineData(OutputCompleteness.Complete, true)]
    [InlineData(OutputCompleteness.Empty, true)]
    [InlineData(OutputCompleteness.Partial, true)]
    [InlineData(OutputCompleteness.None, true)]
    [InlineData(OutputCompleteness.Ambiguous, false)]
    public void TurnResult_OnlyAmbiguousIsNotMeasured(OutputCompleteness completeness, bool expected) =>
        Assert.Equal(expected, Microsoft365Evidence.IsAnswerMeasured(Completed(completeness)));

    [Fact]
    public void TurnResult_EveryCompletenessButAmbiguous_IsMeasured()
    {
        foreach (var completeness in Enum.GetValues<OutputCompleteness>())
        {
            Assert.Equal(completeness != OutputCompleteness.Ambiguous, Microsoft365Evidence.IsAnswerMeasured(Completed(completeness)));
        }
    }

    [Theory]
    [InlineData(OutputCompleteness.Ambiguous, false)]
    [InlineData(OutputCompleteness.None, true)]
    [InlineData(OutputCompleteness.Complete, true)]
    [InlineData(OutputCompleteness.Empty, true)]
    public async Task Response_OfTheRealAgent_FollowsTheSameRule(OutputCompleteness completeness, bool expected)
    {
        var conversation = new FakeWorkIqConversation
        {
            OnRun = (request, _) => Task.FromResult(TurnResults.Completed(request) with
            {
                AnswerCompleteness = completeness,
                AnswerText = completeness is OutputCompleteness.Complete or OutputCompleteness.Empty ? "answer" : null,
            }),
        };
        await using var agent = new WorkIqEvaluableAgent(conversation, "Fake Work IQ", diagnostics: WorkIqDiagnostics.None);

        var response = await agent.InvokeAsync("x");

        Assert.Equal(expected, Microsoft365Evidence.IsAnswerMeasured(response));
    }

    [Fact]
    public void Response_CompletenessAsEnumMemberNameOrJsonString_AmbiguousIsNotMeasured()
    {
        Assert.False(Microsoft365Evidence.IsAnswerMeasured(Response(OutputCompleteness.Ambiguous)));
        Assert.False(Microsoft365Evidence.IsAnswerMeasured(Response("Ambiguous")));
        Assert.False(Microsoft365Evidence.IsAnswerMeasured(Response(JsonSerializer.SerializeToElement("Ambiguous"))));
        Assert.True(Microsoft365Evidence.IsAnswerMeasured(Response(OutputCompleteness.None)));
        Assert.True(Microsoft365Evidence.IsAnswerMeasured(Response("None")));
        Assert.True(Microsoft365Evidence.IsAnswerMeasured(Response(JsonSerializer.SerializeToElement("None"))));
    }

    [Fact]
    public void Response_FromAnotherProvider_IsMeasured()
    {
        Assert.True(Microsoft365Evidence.IsAnswerMeasured(new AgentResponse { Text = "hi" }));
        Assert.True(Microsoft365Evidence.IsAnswerMeasured(new AgentResponse
        {
            Text = "",
            AdditionalProperties = new Dictionary<string, object?> { ["other"] = "Ambiguous" },
        }));
        Assert.True(Microsoft365Evidence.IsAnswerMeasured(Response(null)));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Microsoft365Evidence.IsAnswerMeasured((WorkIqTurnResult)null!));
        Assert.Throws<ArgumentNullException>(() => Microsoft365Evidence.IsAnswerMeasured((AgentResponse)null!));
    }

    private static AgentResponse Response(object? completeness) => new()
    {
        Text = "",
        AdditionalProperties = new Dictionary<string, object?>
        {
            [Microsoft365Evidence.NoAnswerSelectedKey] = true,
            [Microsoft365Evidence.AnswerCompletenessKey] = completeness,
        },
    };
}
