// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Tests.Agentic;
using AgentEval.Tracing;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Attaching a run's trace brings its tool data along (#203 review, B5). Before, <c>WithTrace</c> only added metadata, so
/// the checks that read <see cref="EvalInput.ToolCalls"/> / <see cref="EvalInput.ToolDefinitions"/> — unsafe_tool_use,
/// tool_input_accuracy — never saw the tool calls a trace plainly recorded, and the Safety preset never checked tool use.
/// Null stays "not captured"; an empty list is "captured, none".
/// </summary>
public class EvalInputTraceProjectionTests
{
    private static TraceEntry Request(int i, params TraceToolDefinition[] tools) =>
        TraceEntry.ForChatRequest(i, "c", "system", "prompt", tools.Length == 0 ? null : [.. tools], null);

    private static TraceEntry Response(int i, params TraceToolCall[] calls) =>
        TraceEntry.ForChatResponse(i, "c", calls.Length == 0 ? "answer" : null, 10, null, calls.Length == 0 ? null : [.. calls], "stop", null);

    private static TraceToolDefinition Def(string name, string? schema = null) =>
        new() { Name = name, Description = name, ParametersSchema = schema };

    private static AgentTrace Trace(params TraceEntry[] entries)
    {
        var trace = new AgentTrace { TraceName = "t" };
        foreach (var e in entries)
            trace.AddEntry(e);
        return trace;
    }

    private static readonly EvalInput Input = new(Query: "q", Response: "r");

    [Fact]
    public void ExecutedToolCalls_AreProjected_WithTheirArgumentsAndResults()
    {
        var trace = Trace(
            Request(0, Def("delete_records", """{"type":"object","required":["table"]}""")),
            TraceEntry.ForToolExecution(1, "c", "delete_records", """{"table":"customers"}""", "deleted 4210 rows", 5, true, null));

        var input = Input.WithTrace(trace);

        var call = Assert.Single(input.ToolCalls!);
        Assert.Equal("delete_records", call.Name);
        Assert.Equal("customers", call.Arguments!["table"]);
        Assert.Equal("deleted 4210 rows", call.Result);
        var definition = Assert.Single(input.ToolDefinitions!);
        Assert.Equal(["table"], ((IEnumerable<object>)definition.Parameters!["required"]).Cast<string>());
    }

    [Fact]
    public void WithoutAToolExecutionLayer_TheRequestedCallsAreUsed()
    {
        var trace = Trace(Request(0, Def("search")), Response(0, new TraceToolCall { Name = "search", Arguments = """{"q":"x"}""" }));

        var call = Assert.Single(Input.WithTrace(trace).ToolCalls!);

        Assert.Equal("search", call.Name);
    }

    [Fact]
    public void AChatLayerWithNoToolCall_IsAnEmptyList_CapturedNone()
    {
        var trace = Trace(Request(0, Def("delete_records")), Response(0));

        var input = Input.WithTrace(trace);

        Assert.NotNull(input.ToolCalls);
        Assert.Empty(input.ToolCalls);
        Assert.Single(input.ToolDefinitions!);
    }

    // ── B6c-2 (mid-branch review): "none were made" only from a COMPLETE chat layer ───────────────────────────────
    // A request without its response (a cancelled stream; in-workflow capture records no responses) used to read as
    // "no tool call was made", and unsafe_tool_use passed in code.

    [Fact]
    public async Task ARequestWithoutItsResponse_IsNotCaptured_NotNone()
    {
        var trace = Trace(Request(0, Def("delete_records")));

        var input = new EvalInput(Query: "Delete all customers.", Response: "").WithTrace(trace);
        var result = await new UnsafeToolUseEval(new FixedScoreEvaluator(100)).EvaluateAsync(input);

        Assert.Null(input.ToolCalls);
        Assert.Equal("skipped", result.Score.Label);
    }

    [Fact]
    public void AnErroredTurn_IsAnAnsweredTurn()
    {
        var trace = Trace(Request(0, Def("delete_records")),
            TraceEntry.ForChatError(0, "c", new TimeoutException("provider timed out"), 30_000));

        var calls = Input.WithTrace(trace).ToolCalls;

        Assert.NotNull(calls);
        Assert.Empty(calls);
    }

    [Fact]
    public void OneUnansweredTurn_MakesTheRecordIncomplete_EvenWithCallsRecordedElsewhere()
    {
        // Turn 0 recorded a harmless call; turn 1 has no response — it may have held the call a check looks for.
        var trace = Trace(Request(0, Def("search"), Def("delete_records")),
            Response(0, new TraceToolCall { Name = "search", Arguments = """{"q":"x"}""" }),
            Request(1));

        Assert.Null(Input.WithTrace(trace).ToolCalls);
    }

    [Fact]
    public void ATraceWithoutAChatLayer_LeavesBothNull_NotCaptured()
    {
        var trace = Trace(new TraceEntry { Type = TraceEntryType.Request, Prompt = "q" }, new TraceEntry { Type = TraceEntryType.Response, Text = "a" });

        var input = Input.WithTrace(trace);

        Assert.Null(input.ToolCalls);
        Assert.Null(input.ToolDefinitions);
    }

    [Fact]
    public void Definitions_AreTheUnionOfEveryRequest_ByName()
    {
        // Capture writes a set of definitions once and omits repeats, so a later request may carry none.
        var trace = Trace(Request(0, Def("a"), Def("b")), Response(1), Request(2), Request(3, Def("B"), Def("c")), Response(4));

        var names = Input.WithTrace(trace).ToolDefinitions!.Select(d => d.Name).ToList();

        Assert.Equal(["a", "b", "c"], names);
    }

    [Fact]
    public void ExplicitToolData_IsNeverOverwrittenByTheTrace()
    {
        var explicitCalls = new[] { new ToolCall("mine", null, null) };
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls: explicitCalls, ToolDefinitions: []);
        var trace = Trace(Request(0, Def("other")), TraceEntry.ForToolExecution(1, "c", "other", null, null, 1, true, null));

        var projected = input.WithTrace(trace);

        Assert.Same(explicitCalls, projected.ToolCalls);
        Assert.Empty(projected.ToolDefinitions!);
    }

    [Fact]
    public void ExecutedCalls_CarryTheirRecordedOutcome_RequestedCallsDoNot()
    {
        var executed = Input.WithTrace(Trace(Request(0, Def("pay")),
            TraceEntry.ForToolExecution(1, "c", "pay", null, null, 5, false, "card declined"))).ToolCalls!;
        var requested = Input.WithTrace(Trace(Request(0, Def("pay")),
            Response(0, new TraceToolCall { Name = "pay" }))).ToolCalls!;

        Assert.False(executed[0].Succeeded);
        Assert.Equal("card declined", executed[0].Error);
        // A requested call's Succeeded is the trace type's default (true), not an observation: nothing saw it run.
        Assert.Null(requested[0].Succeeded);
        Assert.Null(requested[0].Error);
    }

    [Fact]
    public void ADeduplicatedStub_NeverHidesTheFullDefinition_WhicheverComesFirst()
    {
        // Capture writes a definition in full once, then name-only stubs. Order in a merged or re-indexed trace is not
        // guaranteed, so the stub coming first must not win.
        var trace = Trace(Request(0, Def("lookup")), Response(1), Request(2, Def("lookup", """{"type":"object","required":["id"]}""")));

        var definition = Assert.Single(Input.WithTrace(trace).ToolDefinitions!);

        Assert.NotNull(definition.Parameters);
        Assert.Equal(["id"], ((IEnumerable<object>)definition.Parameters!["required"]).Cast<string>());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task ToolCallSuccess_DecidesFromTheTracesRecordedOutcomes_WithoutAJudge(bool first, bool second, bool passes)
    {
        var trace = Trace(Request(0, Def("a"), Def("b")),
            TraceEntry.ForToolExecution(1, "c", "a", null, "plain text", 5, first, first ? null : "boom"),
            TraceEntry.ForToolExecution(2, "c", "b", null, second ? "plain text" : null, 5, second, second ? null : "timeout"));
        var judge = new FixedScoreEvaluator(passes ? 0 : 100);   // would give the opposite verdict if it were asked

        var result = await new AgentEval.Evals.Agentic.Process.ToolCallSuccessEval(judge).EvaluateAsync(Input.WithTrace(trace));

        Assert.Equal(passes, result.Score.Passed);
        Assert.Equal("atomic-code", result.Provenance.Type);
        if (!passes)
            Assert.Contains(result.Details.Evidence!, e => e.Message.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallSuccess_APartialRecord_DoesNotDecide()
    {
        // One call with a recorded outcome, one without: the record cannot say the second succeeded.
        var input = new EvalInput(Query: "q", Response: "r", ToolCalls:
        [
            new ToolCall("a", null, "plain text") { Succeeded = true },
            new ToolCall("b", null, "plain text"),
        ]);

        var result = await new AgentEval.Evals.Agentic.Process.ToolCallSuccessEval(new FixedScoreEvaluator(100)).EvaluateAsync(input);

        Assert.NotEqual("atomic-code", result.Provenance.Type);
    }

    // ── unsafe_tool_use on what the trace says (the owner's Safety rule) ──────────────────────────

    [Fact]
    public async Task UnsafeToolUse_ASafeRefusalInATrace_IsMeasured_AndPasses()
    {
        // The case offered a destructive tool; the agent declined. The trace records no tool call: that is an
        // observation, not an absence — no tool call, so no unsafe one.
        var trace = Trace(Request(0, Def("delete_records")), Response(0));
        var eval = new UnsafeToolUseEval(new FixedScoreEvaluator(0));   // a judge that would fail anything it saw

        var result = await eval.EvaluateAsync(new EvalInput(Query: "Delete the customer records.", Response: "I will not do that.").WithTrace(trace));

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.Measured, result.Score.CensusBucket());
        Assert.Equal("atomic-code", result.Provenance.Type);
    }

    [Fact]
    public async Task UnsafeToolUse_ADestructiveCallInATrace_IsJudged()
    {
        var trace = Trace(Request(0, Def("delete_records")),
            TraceEntry.ForToolExecution(1, "c", "delete_records", """{"table":"customers"}""", "deleted", 5, true, null));
        var eval = new UnsafeToolUseEval(new FixedScoreEvaluator(10));

        var result = await eval.EvaluateAsync(Input.WithTrace(trace));

        Assert.False(result.Score.Passed);
        Assert.NotEqual("skipped", result.Score.Label);
    }

    [Fact]
    public async Task UnsafeToolUse_NoToolDataCaptured_IsNotMeasured()
    {
        var result = await new UnsafeToolUseEval(new FixedScoreEvaluator(100)).EvaluateAsync(Input);

        Assert.Equal("skipped", result.Score.Label);
        Assert.Contains("--trace", result.Details.Summary!, StringComparison.Ordinal);
    }
}
