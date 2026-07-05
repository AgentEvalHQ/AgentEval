// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Models;
using AgentEval.Tracing;
using CoreTokenUsage = AgentEval.Core.TokenUsage;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Glass Box Phase 3 (P3.4) — <c>result.Should().HaveTraceFidelity(chatTraces, minScore)</c>: passes on
/// per-executor agreement, throws on a seeded token divergence, and passes (all-NoTruth) when no chat truth.
/// </summary>
public class WorkflowTraceFidelityAssertionTests
{
    private static ExecutorStep Step(string executorId, int prompt, int completion, string? finish) =>
        new()
        {
            ExecutorId = executorId,
            Output = executorId,
            StepIndex = 0,
            TokenUsage = new CoreTokenUsage { PromptTokens = prompt, CompletionTokens = completion },
            FinishReason = finish,
        };

    private static WorkflowExecutionResult Result(params ExecutorStep[] steps) =>
        new() { FinalOutput = "done", Steps = steps };

    private static AgentTrace ChatTrace(int totalTokens, string? finish)
    {
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatResponse(0, null, "r", 1,
            new TraceTokenUsage { PromptTokens = totalTokens, CompletionTokens = 0 }, null, finish, null));
        return trace;
    }

    [Fact]
    public void Agreement_Passes()
    {
        var result = Result(Step("a", 10, 5, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(15, "stop") };

        // Should not throw.
        result.Should().HaveTraceFidelity(chat).Validate();
    }

    [Fact]
    public void TokenDivergence_Throws()
    {
        var result = Result(Step("a", 10, 5, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(99, "stop") };

        var ex = Assert.Throws<WorkflowAssertionException>(
            () => result.Should().HaveTraceFidelity(chat).Validate());
        Assert.Contains("trace fidelity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoChatTruth_Passes_AllNoTruth()
    {
        var result = Result(Step("a", 10, 5, "stop"));

        // chatTraces == null → every executor NoTruth → overall score 1.0 → passes.
        result.Should().HaveTraceFidelity(chatTraces: null).Validate();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void OutOfRangeMinScore_Throws(double minScore)
    {
        var result = Result(Step("a", 10, 5, "stop"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => result.Should().HaveTraceFidelity(chatTraces: null, minScore: minScore));
    }
}
