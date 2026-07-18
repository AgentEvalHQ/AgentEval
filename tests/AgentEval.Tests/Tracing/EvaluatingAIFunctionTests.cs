// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Tracing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Phase 2 (T2.4): the <see cref="EvaluatingAIFunction"/> tool-execution wrapper — records
/// timing/args/result/exception as a ToolExecution entry correlated to the parent invocation, and
/// forwards the inner function's declaration surface.
/// </summary>
public class EvaluatingAIFunctionTests
{
    private static int Add(int a, int b) => a + b;

    private static string Boom() => throw new InvalidOperationException("tool failed");

    // AIFunction.InvokeAsync returns the result JSON-serialized (typically a JsonElement); normalize to int.
    private static int AsInt(object? result) => result is JsonElement je ? je.GetInt32() : Convert.ToInt32(result);

    [Fact]
    public async Task WrappedFunction_RecordsToolExecutionEntry_WithCorrelationArgsAndResult()
    {
        // Arrange
        var trace = new AgentTrace();
        var inner = AIFunctionFactory.Create(Add);
        var wrapped = inner.WithEvaluation(trace);
        var args = new AIFunctionArguments { ["a"] = 2, ["b"] = 3 };

        // Act
        object? result;
        using (new ToolCorrelationScope("chat-0001"))
        {
            result = await wrapped.InvokeAsync(args);
        }

        // Assert
        Assert.Equal(5, AsInt(result));
        var entry = Assert.Single(trace.Entries);
        Assert.Equal(TraceEntryScope.ToolExecution, entry.Scope);
        Assert.Equal(TraceEntryType.ToolCall, entry.Type);
        Assert.Equal("chat-0001", entry.CorrelationId);
        var call = Assert.Single(entry.ToolCalls!);
        Assert.Equal(inner.Name, call.Name);
        Assert.True(call.Succeeded);
        Assert.True(call.DurationMs >= 0);
        Assert.Contains("\"a\"", call.Arguments);     // arguments serialized
        Assert.Equal("5", call.Result);               // result serialized
    }

    [Fact]
    public async Task WrappedThrowingFunction_RecordsFailureAndRethrows()
    {
        // Arrange
        var trace = new AgentTrace();
        var wrapped = AIFunctionFactory.Create(Boom).WithEvaluation(trace);

        // Act / Assert — the underlying exception propagates (wrapped by the invocation pipeline)
        await Assert.ThrowsAnyAsync<Exception>(() => wrapped.InvokeAsync(new AIFunctionArguments()).AsTask());
        var entry = Assert.Single(trace.Entries);
        var call = Assert.Single(entry.ToolCalls!);
        Assert.False(call.Succeeded);
        Assert.NotNull(call.Error);
    }

    [Fact]
    public void ForwardsInnerDeclarationSurface()
    {
        // Arrange
        var inner = AIFunctionFactory.Create(Add);

        // Act
        var wrapped = inner.WithEvaluation(new AgentTrace());

        // Assert — the model (and any tool introspection: schema regen, telemetry) must see the inner
        // function's full declaration surface unchanged, not the wrapper's defaults.
        Assert.Equal(inner.Name, wrapped.Name);
        Assert.Equal(inner.Description, wrapped.Description);
        Assert.Equal(inner.JsonSchema.GetRawText(), wrapped.JsonSchema.GetRawText());
        Assert.Equal(inner.UnderlyingMethod, wrapped.UnderlyingMethod);
        Assert.Equal(inner.ReturnJsonSchema?.GetRawText(), wrapped.ReturnJsonSchema?.GetRawText());
        Assert.Equal(inner.AdditionalProperties, wrapped.AdditionalProperties);
    }

    [Fact]
    public async Task ConcurrentToolInvocations_SharingOneTrace_GetDistinctIndices()
    {
        // Regression (issue #14): the index used to be read as `_trace.Entries.Count` with no synchronization —
        // a TOCTOU race under MEAI AllowConcurrentInvocation (multiple tools invoked concurrently within one
        // round-trip, all sharing this trace). Two racing reads before either call's AddEntry could return the
        // SAME count, so two ToolExecution entries could land with the SAME Index. Drive real concurrency with a
        // short artificial delay so both wrappers are mid-flight before either finishes.
        var trace = new AgentTrace();
        var slowAdd = AIFunctionFactory.Create(async (int a, int b) => { await Task.Delay(20); return a + b; }, name: "SlowAdd");
        var wrapped = slowAdd.WithEvaluation(trace);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => wrapped.InvokeAsync(new AIFunctionArguments { ["a"] = i, ["b"] = 1 }).AsTask())
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(8, trace.Entries.Count);
        var indices = trace.Entries.Select(e => e.Index).ToList();
        Assert.Equal(indices.Count, indices.Distinct().Count());   // no duplicate index handed to two concurrent calls
    }

    [Fact]
    public async Task WithoutCorrelationScope_RecordsNullCorrelationId_StillSucceeds()
    {
        // Arrange
        var trace = new AgentTrace();
        var wrapped = AIFunctionFactory.Create(Add).WithEvaluation(trace);

        // Act
        var result = await wrapped.InvokeAsync(new AIFunctionArguments { ["a"] = 1, ["b"] = 1 });

        // Assert
        Assert.Equal(2, AsInt(result));
        Assert.Null(Assert.Single(trace.Entries).CorrelationId);
    }
}
