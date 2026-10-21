// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// <c>(TestCase, TestResult, agent instructions) → EvalInput</c>. Neither <see cref="TestCase"/> nor
/// <see cref="TestResult"/> records the system message, so the projection set <see cref="EvalInput.SystemMessage"/> to
/// null on every run and no judge that grades the response against the agent's instructions was ever shown them. A
/// caller that built the agent knows them and now passes them.
/// </summary>
public class TestRunEvalProjectionInstructionsTests
{
    private static TestCase Case() => new()
    {
        Id = "case-1",
        Name = "Price question",
        Input = "How much is the premium plan?",
        ExpectedTools = new[] { "lookup_plan" },
    };

    private static TestResult Result() => new() { TestName = "Price question", ActualOutput = "Please ask sales." };

    [Fact]
    public void TheAgentsInstructions_AreCarriedAsTheSystemMessage()
    {
        var input = Case().ToEvalInput(Result(), "SYS-4410 never quote prices");

        Assert.Equal("SYS-4410 never quote prices", input.SystemMessage);
    }

    [Fact]
    public void EveryOtherField_IsTheTwoArgumentProjections()
    {
        var plain = Case().ToEvalInput(Result());
        var withInstructions = Case().ToEvalInput(Result(), "SYS-4410");

        Assert.Equal(plain.Query, withInstructions.Query);
        Assert.Equal(plain.Response, withInstructions.Response);
        Assert.Equal(plain.CaseId, withInstructions.CaseId);
        Assert.Null(withInstructions.ToolCalls);   // no recorder ran: unknown, as in the two-argument form
        Assert.Equal(plain.ExpectedActions!.Select(a => a.Description), withInstructions.ExpectedActions!.Select(a => a.Description));
        Assert.Null(plain.SystemMessage);   // the two-argument form still has nothing to carry
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoInstructions_LeaveTheSystemMessageNull_NeverAnEmptyPrompt(string? instructions)
    {
        var input = Case().ToEvalInput(Result(), instructions);

        Assert.Null(input.SystemMessage);
    }
}
