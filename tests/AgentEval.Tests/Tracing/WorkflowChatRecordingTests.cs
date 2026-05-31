// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Tracing;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Phase 5b: <see cref="WorkflowChatRecording"/> — per-executor chat-boundary recording for
/// MAF workflows (collects one trace per executor for per-executor Trace Fidelity).
/// </summary>
public class WorkflowChatRecordingTests
{
    [Fact]
    public async Task Instrument_CollectsOneTracePerExecutor()
    {
        // Arrange — two executors, each with its own scripted client
        var recording = new WorkflowChatRecording();
        var planner = recording.Instrument("TripPlanner", new ScriptedChatClient().AddText("planned"));
        var booker = recording.Instrument("FlightReservation", new ScriptedChatClient().AddText("booked"));

        // Act — each executor's client runs independently
        await planner.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "plan") });
        await booker.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "book") });

        // Assert — separate traces, each with its executor's round-trip
        Assert.Equal(2, recording.Traces.Count);
        Assert.Equal("planned", recording.TraceFor("TripPlanner")!.Entries.First(e => e.Type == TraceEntryType.Response).Text);
        Assert.Equal("booked", recording.TraceFor("FlightReservation")!.Entries.First(e => e.Type == TraceEntryType.Response).Text);
        Assert.All(recording.Traces.Values, t => Assert.All(t.Entries, e => Assert.Equal(TraceEntryScope.ChatTurn, e.EffectiveScope)));
    }

    [Fact]
    public void TraceFor_UnknownExecutor_ReturnsNull()
    {
        var recording = new WorkflowChatRecording();
        Assert.Null(recording.TraceFor("never-instrumented"));
    }
}
