// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper next-wave item — the ONE empirical fact
/// <c>Next-Wave-Gates-and-Crucible-Implementation-Plan-2026-07-16.md</c> §4 flags as needed before
/// <c>IToolPlanGate</c>'s interface shape can be finalized: when a single model turn proposes N sibling tool
/// calls (<c>FunctionCount &gt; 1</c>), does MAF's <c>FunctionInvokingChatClient</c> dispatch them
/// SEQUENTIALLY (call #1 only starts once call #0's own pipeline — including a gate's verdict — has been
/// fully processed) or does it start all N calls' pipelines independently/concurrently? This determines
/// whether a plan-level gate that inspects the whole batch and terminates at call #0 can actually PREVENT
/// calls #1..N-1 from running, or whether they may already be in flight by the time call #0's verdict lands.
///
/// <para><b>This test does NOT build <c>IToolPlanGate</c> itself</b> — per the task scope, only the empirical
/// question and its documented finding are in scope here. See §4 of the plan doc (mirrored in this file's
/// own header) for the finding.</para>
/// </summary>
public class ToolPlanGateEmpiricalTests
{
    private readonly ITestOutputHelper _output;

    public ToolPlanGateEmpiricalTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// THE decisive test. Registers <c>ForbiddenToolGate</c> against ONLY the first of two sibling tool calls
    /// scripted via <c>AddParallelToolCalls</c> (both calls come from ONE assistant message — <c>FunctionCount
    /// == 2</c> — proven already by <c>ToolGateTests.ParallelToolCalls_EachCallGatedIndependently_...</c>),
    /// under <see cref="ToolGatePolicy.Terminate"/> — the EXACT mechanism <c>AgentEvalToolGateExtensions</c>
    /// already uses to set MAF's own <c>FunctionInvocationContext.Terminate = true</c> on a block (see
    /// <c>AgentEvalToolGateExtensions.cs</c>, every <c>case ToolGatePolicy.Terminate</c> branch). If MAF
    /// dispatches sequentially, terminating during call #0's pipeline must prevent call #1's tool BODY from
    /// ever executing. If MAF had already started call #1 independently (concurrent dispatch), call #1's body
    /// could still run despite call #0's termination.
    /// </summary>
    [Fact]
    public async Task DefaultChatClientAgent_TerminateOnFirstSiblingCall_SecondSiblingNeverExecutes_ConfirmsSequentialDispatch()
    {
        var firstExecuted = 0;
        var secondExecuted = 0;
        var firstTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref firstExecuted); return "first-ran"; }, "first_tool");
        var secondTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref secondExecuted); return "second-ran"; }, "second_tool");

        var scripted = new ScriptedChatClient()
            .AddParallelToolCalls(
                ("c1", "first_tool", new Dictionary<string, object?>()),
                ("c2", "second_tool", new Dictionary<string, object?>()))
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [firstTool, secondTool] },
        });

        // ForbiddenToolGate targets ONLY "first_tool" (call index 0) — "second_tool" (index 1, the SAME
        // turn's sibling) has no gate blocking it at all. Terminate policy sets context.Terminate = true
        // the instant call #0's pipeline sees the Block verdict.
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("first_tool")], ToolGatePolicy.Terminate)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, firstExecuted);    // blocked, as expected — the direct-block case, not the interesting part
        // THE EMPIRICAL ANSWER: second_tool is UNGATED and sits right next to first_tool in the SAME
        // FICC iteration (FunctionCount == 2). If dispatch were concurrent/independent, nothing stops
        // second_tool's body from running before/alongside first_tool's Terminate signal lands. It does
        // NOT run — confirming MAF's FunctionInvokingChatClient (default AllowConcurrentInvocation=false,
        // confirmed independently below and in Microsoft.Extensions.AI's own XML docs) processes sibling
        // calls from one turn SERIALLY, one fully through the gate pipeline before the next one starts.
        Assert.Equal(0, secondExecuted);
    }

    /// <summary>
    /// Companion negative control for the test above: with NO gate at all, both siblings run — proving the
    /// first test's second-tool-never-executes result is caused by the Terminate signal, not by some
    /// unrelated reason the second call never gets dispatched at all (e.g. a scripting mistake in the fixture).
    /// </summary>
    [Fact]
    public async Task DefaultChatClientAgent_NoGate_BothSiblingsExecute_NegativeControl()
    {
        var firstExecuted = 0;
        var secondExecuted = 0;
        var firstTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref firstExecuted); return "first-ran"; }, "first_tool");
        var secondTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref secondExecuted); return "second-ran"; }, "second_tool");

        var scripted = new ScriptedChatClient()
            .AddParallelToolCalls(
                ("c1", "first_tool", new Dictionary<string, object?>()),
                ("c2", "second_tool", new Dictionary<string, object?>()))
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [firstTool, secondTool] },
        });

        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([], ToolGatePolicy.Terminate)   // zero gates registered — nothing blocks anything
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(1, firstExecuted);
        Assert.Equal(1, secondExecuted);   // both ran — the fixture and dispatch mechanism work as expected absent a block
    }

    /// <summary>
    /// Explores the OTHER half of the empirical question the plan doc raises — MAF's own
    /// <c>FunctionInvokingChatClient.AllowConcurrentInvocation</c> (default <see langword="false"/>, per
    /// <c>Microsoft.Extensions.AI</c>'s own XML docs: "By default, such function calls are processed
    /// serially"). This test manually constructs a <see cref="FunctionInvokingChatClient"/> with that flag
    /// explicitly set <see langword="true"/>, then passes it to <see cref="ChatClientAgent"/> with
    /// <c>UseProvidedChatClientAsIs = true</c> so MAF uses THIS pre-configured FICC instead of inserting its
    /// own default one. Documents whether AgentEval's Gatekeeper middleware still functions correctly when
    /// concurrent invocation is (manually) enabled, and whether termination still prevents the sibling call.
    /// </summary>
    [Fact]
    public async Task ManuallyConfiguredConcurrentFicc_TerminateOnFirstSiblingCall_Behavior()
    {
        var firstExecuted = 0;
        var secondExecuted = 0;
        var firstTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref firstExecuted); return "first-ran"; }, "first_tool");
        var secondTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref secondExecuted); return "second-ran"; }, "second_tool");

        var scripted = new ScriptedChatClient()
            .AddParallelToolCalls(
                ("c1", "first_tool", new Dictionary<string, object?>()),
                ("c2", "second_tool", new Dictionary<string, object?>()))
            .AddText("done");

        // Build our OWN FunctionInvokingChatClient with AllowConcurrentInvocation = true, then tell
        // ChatClientAgent to use it AS-IS (no second, default FICC layered on top — see
        // ToolGateSpikeTests.NonFicc_FailsClosed_ThrowsAtBuildOrRun for the other half of that flag's
        // behavior: without ANY FICC in the chain, the gate middleware fails closed).
        var concurrentFicc = new FunctionInvokingChatClient(scripted) { AllowConcurrentInvocation = true };
        var agent = new ChatClientAgent(concurrentFicc, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [firstTool, secondTool] },
            UseProvidedChatClientAsIs = true,
        });

        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("first_tool")], ToolGatePolicy.Terminate)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, firstExecuted);   // still blocked directly, regardless of concurrency mode
        _output.WriteLine($"[empirical] AllowConcurrentInvocation=true: secondExecuted={secondExecuted}");

        // THE OTHER HALF OF THE EMPIRICAL ANSWER, confirmed by actually running this: unlike the default
        // (sequential) mode above, second_tool DID execute (secondExecuted == 1) despite first_tool's
        // Terminate signal — MAF's FunctionInvokingChatClient, under AllowConcurrentInvocation=true, starts
        // sibling calls' pipelines independently, so call #0's termination does NOT retroactively stop a
        // sibling that was already dispatched. A plan-level "inspect-at-index-0, terminate the batch" gate
        // design does NOT reliably prevent sibling execution under this mode — see the plan-doc write-up
        // this test's finding feeds into for what that means for IToolPlanGate's eventual design.
        Assert.Equal(1, secondExecuted);
    }
}
