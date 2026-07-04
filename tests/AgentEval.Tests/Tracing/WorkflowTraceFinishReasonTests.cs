// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Part 2 (P2.B2) — the additive <c>FinishReason</c> field survives serialization (WhenWritingNull
/// so null omits the key) AND the record → save → load → replay round trip (WorkflowTraceReplayingAgent).
/// </summary>
public class WorkflowTraceFinishReasonTests
{
    private static WorkflowTrace TraceWithStep(string? finishReason)
    {
        var trace = new WorkflowTrace { TraceName = "t", WorkflowType = "Sequential", OriginalPrompt = "go" };
        trace.Steps.Add(new WorkflowTraceStep
        {
            ExecutorId = "a", StepIndex = 0, Output = "out",
            FinishReason = finishReason,
        });
        return trace;
    }

    [Fact]
    public async Task FinishReason_SerializesAsCamelCase_AndSurvivesRoundTrip()
    {
        var json = await WorkflowTraceSerializer.SerializeToStringAsync(TraceWithStep("content_filter"));

        Assert.Contains("\"finishReason\": \"content_filter\"", json);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var reloaded = await WorkflowTraceSerializer.DeserializeAsync(stream);
        Assert.Equal("content_filter", reloaded.Steps[0].FinishReason);
    }

    [Fact]
    public async Task FinishReason_Null_OmitsKey_ForByteIdenticalBackCompat()
    {
        var json = await WorkflowTraceSerializer.SerializeToStringAsync(TraceWithStep(null));

        Assert.DoesNotContain("finishReason", json);
    }

    [Fact]
    public async Task FinishReason_SurvivesReplayReconstruction()
    {
        // Record→save→load leaves FinishReason in the WorkflowTrace; replay must project it back onto ExecutorStep.
        var replay = new WorkflowTraceReplayingAgent(TraceWithStep("length"));

        var result = await replay.ExecuteWorkflowAsync("go");

        Assert.Equal("length", result.Steps[0].FinishReason);
    }
}
