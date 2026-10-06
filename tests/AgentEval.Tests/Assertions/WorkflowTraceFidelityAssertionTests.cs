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
/// per-executor agreement, throws on a seeded token divergence, and fails when no executor (or not every executor,
/// unless allowed) could be checked.
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
    public void NoChatTruth_Fails_NothingWasChecked()
    {
        var result = Result(Step("a", 10, 5, "stop"));

        // chatTraces == null → every executor NoTruth → nothing was checked. It passed at score 1.0 (review round 6, B10y).
        var ex = Assert.Throws<WorkflowAssertionException>(
            () => result.Should().HaveTraceFidelity(chatTraces: null).Validate());
        Assert.Contains("nothing could be checked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unchecked_CarriesNoScore100_AndAPartlyCheckedFailSaysSo()
    {
        // Review round 7 L2 + L4 (B10ah): skipped results carried score100 = 100 beside Value 0, and a partly checked warn
        // or fail had no coverage note.
        var nothing = new AgentEval.Benchmarks.WorkflowTraceFidelityReconciler()
            .ReconcileToEvalResult(Result(Step("a", 10, 5, "stop")), chatTraces: null);
        Assert.Equal("skipped", nothing.Score.Label);
        Assert.False(nothing.Details.Dimensions!.ContainsKey("score100"));
        Assert.All(nothing.Details.SubResults!, s => Assert.False(s.Details.Dimensions!.ContainsKey("score100")));

        var partlyFailing = new AgentEval.Benchmarks.WorkflowTraceFidelityReconciler().ReconcileToEvalResult(
            Result(Step("a", 10, 5, "stop"), Step("b", 10, 5, "stop")),
            new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(99, "stop") });   // a diverges, b unchecked
        Assert.Equal("fail", partlyFailing.Score.Label);
        Assert.Contains("1 of 2 executor(s) had no chat-boundary truth", partlyFailing.Details.Summary);

        var agentSide = new AgentEval.Benchmarks.TraceFidelityRunner().ReconcileToEvalResult(new AgentTrace(), new AgentTrace());
        Assert.Null(agentSide.Details.Dimensions);
        Assert.All(agentSide.Details.SubResults!, s => Assert.False(s.Details.Dimensions!.ContainsKey("score100")));
    }

    [Fact]
    public void TheOriginalSignature_IsKept_ForCompiledCallers()
    {
        // Review round 8 L7 (B10am): adding a 4th optional parameter changed the method's signature, a binary break for
        // callers compiled against (chatTraces, minScore, because).
        var original = typeof(AgentEval.Assertions.WorkflowAssertionBuilder).GetMethod("HaveTraceFidelity",
            [typeof(IReadOnlyDictionary<string, AgentTrace>), typeof(double), typeof(string)]);

        Assert.NotNull(original);
    }

    [Fact]
    public void PartlyChecked_Fails_UnlessUncheckedExecutorsAreAllowed()
    {
        // Review round 7 M-B (B10af): the bench verdict withholds a pass that rests on some executors (warn, exit 10);
        // the assertion passed it.
        var result = Result(Step("a", 10, 5, "stop"), Step("router", 0, 0, null));
        var traces = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(15, "stop") };

        var ex = Assert.Throws<WorkflowAssertionException>(() => result.Should().HaveTraceFidelity(traces).Validate());
        Assert.Contains("router", ex.Message, StringComparison.Ordinal);

        result.Should().HaveTraceFidelity(traces, allowUncheckedExecutors: true).Validate();   // judged on "a" alone
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
