// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.MAF;
using CoreTokenUsage = AgentEval.Core.TokenUsage;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Glass Box Part 2 — WF-SAMPLE-T3: the per-executor ledger (P2.B1) + the additive FinishReason carrier
/// (P2.B2). Drives <see cref="MAFWorkflowAdapter"/> with scripted event streams and asserts that each
/// <c>ExecutorStep</c> is populated from its executor's <c>ExecutorAgentResponseEvent</c>, including the
/// dedup-SUM rule, consume-on-flush across a loop, the WorkflowCompleteEvent terminal-flush path, and
/// back-compat when no response event is emitted.
/// </summary>
public class MAFWorkflowLedgerTests
{
    [Fact]
    public async Task LinearRun_PopulatesTokenUsageAndFinishReason_ViaCompleteFlush()
    {
        var adapter = new MAFWorkflowAdapter("ledger-linear", LinearWithResponse);

        var result = await adapter.ExecuteWorkflowAsync("go");

        var step = Assert.Single(result.Steps);
        Assert.Equal("a", step.ExecutorId);
        Assert.NotNull(step.TokenUsage);
        Assert.Equal(10, step.TokenUsage!.PromptTokens);
        Assert.Equal(5, step.TokenUsage.CompletionTokens);
        Assert.Equal("stop", step.FinishReason);
    }

    private static async IAsyncEnumerable<WorkflowEvent> LinearWithResponse(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorAgentResponseEvent("a", "output A", new CoreTokenUsage { PromptTokens = 10, CompletionTokens = 5 }, "stop");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MultiExecutor_PopulatesEachStep_ViaEdgeAndCompleteFlush()
    {
        var adapter = new MAFWorkflowAdapter("ledger-multi", TwoExecutorsWithResponses);

        var result = await adapter.ExecuteWorkflowAsync("go");

        Assert.Equal(2, result.Steps.Count);
        var a = result.Steps.Single(s => s.ExecutorId == "a");
        var b = result.Steps.Single(s => s.ExecutorId == "b");
        Assert.Equal(10, a.TokenUsage!.PromptTokens);
        Assert.Equal("stop", a.FinishReason);
        Assert.Equal(20, b.TokenUsage!.PromptTokens);
        Assert.Equal("length", b.FinishReason);
    }

    private static async IAsyncEnumerable<WorkflowEvent> TwoExecutorsWithResponses(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorAgentResponseEvent("a", "output A", new CoreTokenUsage { PromptTokens = 10, CompletionTokens = 5 }, "stop");
        yield return new EdgeTraversedEvent("a", "b");
        yield return new ExecutorAgentResponseEvent("b", "output B", new CoreTokenUsage { PromptTokens = 20, CompletionTokens = 8 }, "length");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RepeatResponseForSameOpenStep_SumsTokens_KeepsFirstFinishReason()
    {
        var adapter = new MAFWorkflowAdapter("ledger-dedup", DoubleResponse);

        var result = await adapter.ExecuteWorkflowAsync("go");

        var step = Assert.Single(result.Steps);
        Assert.Equal(13, step.TokenUsage!.PromptTokens);   // 10 + 3
        Assert.Equal(7, step.TokenUsage.CompletionTokens); // 5 + 2
        Assert.Equal("stop", step.FinishReason);           // first non-null wins
    }

    private static async IAsyncEnumerable<WorkflowEvent> DoubleResponse(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorAgentResponseEvent("a", "part 1", new CoreTokenUsage { PromptTokens = 10, CompletionTokens = 5 }, "stop");
        yield return new ExecutorAgentResponseEvent("a", "part 2", new CoreTokenUsage { PromptTokens = 3, CompletionTokens = 2 }, "length");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task LoopWorkflow_ConsumesLedgerPerFlush_DoesNotReReportEarlierUsage()
    {
        var adapter = new MAFWorkflowAdapter("ledger-loop", LoopExecutor);

        var result = await adapter.ExecuteWorkflowAsync("go");

        // A runs twice (A -> B -> A). Each A step must carry ONLY its own iteration's usage.
        var aSteps = result.Steps.Where(s => s.ExecutorId == "a").OrderBy(s => s.StepIndex).ToList();
        Assert.Equal(2, aSteps.Count);
        Assert.Equal(10, aSteps[0].TokenUsage!.PromptTokens); // first A
        Assert.Equal(1, aSteps[1].TokenUsage!.PromptTokens);  // second A — NOT 11 (consume-on-flush)
    }

    private static async IAsyncEnumerable<WorkflowEvent> LoopExecutor(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorAgentResponseEvent("a", "A#1", new CoreTokenUsage { PromptTokens = 10, CompletionTokens = 5 }, "stop");
        yield return new EdgeTraversedEvent("a", "b");
        yield return new ExecutorAgentResponseEvent("b", "B", new CoreTokenUsage { PromptTokens = 20, CompletionTokens = 8 }, "stop");
        yield return new EdgeTraversedEvent("b", "a");
        yield return new ExecutorAgentResponseEvent("a", "A#2", new CoreTokenUsage { PromptTokens = 1, CompletionTokens = 1 }, "stop");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DoubleResponse_WithNullThenNonNullUsage_SumsViaIdentity()
    {
        // Exercises SumUsage's null-identity branches: a null-usage response followed by a real one must
        // yield the real usage (null treated as identity), not throw or zero it out.
        var adapter = new MAFWorkflowAdapter("ledger-nullusage", NullThenRealUsage);

        var result = await adapter.ExecuteWorkflowAsync("go");

        var step = Assert.Single(result.Steps);
        Assert.Equal(7, step.TokenUsage!.PromptTokens);
        Assert.Equal(3, step.TokenUsage.CompletionTokens);
        Assert.Equal("stop", step.FinishReason); // first non-null finish (the null-usage event carried it)
    }

    private static async IAsyncEnumerable<WorkflowEvent> NullThenRealUsage(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorAgentResponseEvent("a", "part 1", Usage: null, FinishReason: "stop");
        yield return new ExecutorAgentResponseEvent("a", "part 2", new CoreTokenUsage { PromptTokens = 7, CompletionTokens = 3 }, "length");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParallelBranches_EachBranchExecutorCarriesItsOwnUsage()
    {
        var adapter = new MAFWorkflowAdapter("ledger-parallel", ParallelExecutor);

        var result = await adapter.ExecuteWorkflowAsync("go");

        var b1 = result.Steps.Single(s => s.ExecutorId == "branch1");
        var b2 = result.Steps.Single(s => s.ExecutorId == "branch2");
        Assert.Equal(11, b1.TokenUsage!.PromptTokens);
        Assert.Equal(22, b2.TokenUsage!.PromptTokens); // no cross-branch contamination
    }

    private static async IAsyncEnumerable<WorkflowEvent> ParallelExecutor(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ParallelBranchStartEvent("fan", ["branch1", "branch2"]);
        yield return new ExecutorAgentResponseEvent("branch1", "b1", new CoreTokenUsage { PromptTokens = 11, CompletionTokens = 1 }, "stop");
        yield return new EdgeTraversedEvent("branch1", "branch2");
        yield return new ExecutorAgentResponseEvent("branch2", "b2", new CoreTokenUsage { PromptTokens = 22, CompletionTokens = 2 }, "stop");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NoResponseEvent_LeavesTokenUsageAndFinishReasonNull_BackCompat()
    {
        var adapter = new MAFWorkflowAdapter("ledger-plain", PlainOutputOnly);

        var result = await adapter.ExecuteWorkflowAsync("go");

        var step = Assert.Single(result.Steps);
        Assert.Null(step.TokenUsage);
        Assert.Null(step.FinishReason);
    }

    private static async IAsyncEnumerable<WorkflowEvent> PlainOutputOnly(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("a", "plain output");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }
}
