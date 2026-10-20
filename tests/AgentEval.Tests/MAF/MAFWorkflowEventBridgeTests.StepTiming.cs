// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Step timing: the bridge stamps each step's output with when MAF ran that step, and the adapter records it. These
/// feed <see cref="MAFWorkflowEventBridge.TranslateEventsAsync"/> exact MAF event orderings on a fake clock, so every
/// start offset and duration is known to the millisecond.
/// </summary>
public partial class MAFWorkflowEventBridgeTests
{
    [Fact]
    public async Task TranslateEvents_StepTiming_RunsFromTheDeliveryThatOpensTheStep_ToItsLastCompletion()
    {
        // A two-agent chain with the off-thread input race: TripPlanner's TurnToken lands one superstep after its
        // input. A step runs from the delivery that opened it until its last delivery completed, so TripPlanner runs
        // from its input (0 ms) through the 50 ms wait for its TurnToken and its 2 s turn, and FlightReservation from
        // its first forwarded messages to the end of its 3 s turn. The 10 ms between supersteps is in neither step.
        var clock = new FakeTimeProvider();
        var result = await RunTranslatedAsync(clock,
            chatProtocolExecutorIds: ["TripPlanner_1", "FlightReservation_2"],
            SuperStep(0, AgentDelivery("TripPlanner_1", new ChatMessage(ChatRole.User, "Plan a trip"))),
            Wait(TimeSpan.FromMilliseconds(50)),
            SuperStep(1, AgentTurn("TripPlanner_1", TimeSpan.FromSeconds(2), "TripPlanner reply")),
            Wait(TimeSpan.FromMilliseconds(10)),
            SuperStep(2,
                AgentDelivery("FlightReservation_2", new List<ChatMessage> { new(ChatRole.User, "Plan a trip") }),
                AgentDelivery("FlightReservation_2", new List<ChatMessage> { new(ChatRole.Assistant, "TripPlanner reply") }),
                AgentTurn("FlightReservation_2", TimeSpan.FromSeconds(3), "FlightReservation reply")),
            Wait(TimeSpan.FromMilliseconds(5)));

        Assert.Equal(
            ["TripPlanner 0-2050 ms", "FlightReservation 2060-5060 ms"],
            result.Steps.Select(TimingOf).ToArray());
    }

    [Fact]
    public async Task TranslateEvents_SelfLoop_TimesEachIterationAsItsOwnStep()
    {
        var clock = new FakeTimeProvider();
        var result = await RunTranslatedAsync(clock,
            chatProtocolExecutorIds: [],
            SuperStep(0, FunctionWork("A_1", "go", TimeSpan.FromMilliseconds(100), "A pass 1")),
            Wait(TimeSpan.FromMilliseconds(5)),
            SuperStep(1, FunctionWork("A_1", "A pass 1", TimeSpan.FromMilliseconds(200), "A pass 2")),
            Wait(TimeSpan.FromMilliseconds(5)),
            SuperStep(2, FunctionWork("B_2", "A pass 2", TimeSpan.FromMilliseconds(300), "B done")));

        Assert.Equal(
            ["A 0-100 ms", "A 105-305 ms", "B 310-610 ms"],
            result.Steps.Select(TimingOf).ToArray());
    }

    [Fact]
    public async Task TranslateEvents_StreamedTextThenFailure_StepRunsUntilTheFailure()
    {
        // An agent that streams part of its reply and then fails: its step ends at the ExecutorFailedEvent.
        var clock = new FakeTimeProvider();
        var result = await RunTranslatedAsync(clock,
            chatProtocolExecutorIds: ["Writer_1"],
            SuperStep(0,
                [new MAFWorkflows.ExecutorInvokedEvent("Writer_1", new MAFWorkflows.TurnToken(true))],
                Wait(TimeSpan.FromMilliseconds(400)),
                [StreamedText("Writer_1", "partial draft")],
                Wait(TimeSpan.FromMilliseconds(250)),
                [new MAFWorkflows.ExecutorFailedEvent("Writer_1", new InvalidOperationException("model quota exceeded"))]));

        Assert.Equal(["Writer 0-650 ms"], result.Steps.Select(TimingOf).ToArray());
        Assert.Equal("partial draft", result.Steps[0].Output);
        Assert.Contains(result.Errors!, e => e.ExecutorId == "Writer" && e.Message == "model quota exceeded");
    }

    [Fact]
    public async Task TranslateEvents_AgentResponseEvent_IsTimedToTheResponse_AndTheStepToTheTurnsCompletion()
    {
        // With EmitAgentResponseEvents, MAF raises the complete response inside the turn, before the turn's
        // ExecutorCompletedEvent: that response is timed to when it arrived, and the step to the turn's completion.
        var clock = new FakeTimeProvider();
        var events = await TranslateAsync(clock,
            chatProtocolExecutorIds: ["Presenter_1"],
            SuperStep(0,
                [new MAFWorkflows.ExecutorInvokedEvent("Presenter_1", new MAFWorkflows.TurnToken(true))],
                Wait(TimeSpan.FromSeconds(1)),
                [new MAFWorkflows.AgentResponseEvent("Presenter_1",
                    new Microsoft.Agents.AI.AgentResponse(new ChatMessage(ChatRole.Assistant, "Your itinerary")))],
                Wait(TimeSpan.FromMilliseconds(20)),
                [new MAFWorkflows.ExecutorCompletedEvent("Presenter_1", null)]));

        var response = Assert.Single(events.OfType<ExecutorAgentResponseEvent>());
        Assert.Equal(TimeSpan.Zero, response.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Duration);

        var result = await AdaptAsync(events);
        Assert.Equal(["Presenter 0-1020 ms"], result.Steps.Select(TimingOf).ToArray());
    }

    [Fact]
    public async Task TranslateEvents_FanOutSiblingInvokedWhileRunning_GoesOutUntimed_AndDoesNotStretchTheOpenStep()
    {
        // Known limit (docs/workflows.md): fan-out siblings run concurrently in one superstep and the bridge
        // attributes their output by stream position. Their timing must not be made up: Left is still running when
        // Right is invoked, so the bridge cannot know when Left's step ends and sends it untimed rather than as
        // ~0 ms. Right's step ends when RIGHT completes, not when its slower sibling does.
        var clock = new FakeTimeProvider();
        var events = await TranslateAsync(clock,
            chatProtocolExecutorIds: [],
            SuperStep(0, FunctionWork("Start_1", "go", TimeSpan.FromMilliseconds(10), "fan out")),
            SuperStep(1,
                [
                    new MAFWorkflows.ExecutorInvokedEvent("Left_2", "fan out"),
                    new MAFWorkflows.ExecutorInvokedEvent("Right_3", "fan out"),
                ],
                Wait(TimeSpan.FromMilliseconds(400)),
                [new MAFWorkflows.ExecutorCompletedEvent("Right_3", "right")],
                Wait(TimeSpan.FromMilliseconds(100)),
                [new MAFWorkflows.ExecutorCompletedEvent("Left_2", "left")]));

        var outputs = events.OfType<ExecutorOutputEvent>().ToList();
        Assert.Contains(outputs, o => o.ExecutorId == "Left");
        Assert.All(outputs.Where(o => o.ExecutorId == "Left"), o =>
        {
            Assert.Null(o.StartOffset);
            Assert.Null(o.Duration);
        });
        var right = Assert.Single(outputs.Where(o => o.ExecutorId == "Right" && o.Duration is not null)
            .Select(o => (o.StartOffset, o.Duration)).Distinct());
        Assert.Equal(TimeSpan.FromMilliseconds(10), right.StartOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(400), right.Duration);
    }

    [Fact]
    public async Task Adapter_RecordsTheTimingTheEventSourceMeasured_FromTheStepsLatestTimedOutput()
    {
        // Any event source can report timing: a step takes it from its latest output event that carries it.
        var adapter = new MAFWorkflowAdapter("timed-source", (prompt, ct) => Source());

        var result = await adapter.ExecuteWorkflowAsync("input");

        var a = result.Steps[0];
        Assert.Equal(("A", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)), (a.ExecutorId, a.StartOffset, a.Duration));
        var b = result.Steps[1];
        Assert.Equal(("B", "untimed"), (b.ExecutorId, b.Output));

        static async IAsyncEnumerable<WorkflowEvent> Source()
        {
            await Task.Yield();
            yield return new ExecutorOutputEvent("A", "first") { StartOffset = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(2) };
            yield return new ExecutorOutputEvent("A", " second");
            yield return new ExecutorOutputEvent("A", "") { StartOffset = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(3) };
            yield return new EdgeTraversedEvent("A", "B");
            yield return new ExecutorOutputEvent("B", "untimed");
            yield return new WorkflowCompleteEvent();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string TimingOf(ExecutorStep step) =>
        $"{step.ExecutorId} {step.StartOffset.TotalMilliseconds:F0}-{(step.StartOffset + step.Duration).TotalMilliseconds:F0} ms";

    /// <summary>Translates the event sequence on <paramref name="clock"/> and reads it back through the adapter.</summary>
    private static async Task<WorkflowExecutionResult> RunTranslatedAsync(
        FakeTimeProvider clock,
        IReadOnlyCollection<string> chatProtocolExecutorIds,
        params IEnumerable<MAFWorkflows.WorkflowEvent>[] superSteps) =>
        await AdaptAsync(await TranslateAsync(clock, chatProtocolExecutorIds, superSteps));

    /// <summary>
    /// The bridge's translation of the event sequence. A <see cref="ClockAdvance"/> in it moves
    /// <paramref name="clock"/> on instead of reaching the bridge, so the bridge reads each event's time as it arrives.
    /// </summary>
    private static async Task<List<WorkflowEvent>> TranslateAsync(
        FakeTimeProvider clock,
        IReadOnlyCollection<string> chatProtocolExecutorIds,
        params IEnumerable<MAFWorkflows.WorkflowEvent>[] superSteps)
    {
        var events = superSteps.SelectMany(s => s).ToList();
        var rawIds = events.OfType<MAFWorkflows.ExecutorEvent>().Select(e => e.ExecutorId)
            .Concat(events.OfType<MAFWorkflows.WorkflowOutputEvent>().Select(e => e.ExecutorId))
            .Distinct().ToList();
        var idMap = rawIds.ToDictionary(id => id, id => id[..id.LastIndexOf('_')]);

        var translated = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.TranslateEventsAsync(
            WithClock(events, clock), chatProtocolExecutorIds.ToHashSet(StringComparer.Ordinal), idMap, timeProvider: clock))
        {
            translated.Add(evt);
        }

        return translated;

        static async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> WithClock(
            IEnumerable<MAFWorkflows.WorkflowEvent> source, FakeTimeProvider clock)
        {
            foreach (var e in source)
            {
                await Task.Yield();
                if (e is ClockAdvance advance)
                {
                    clock.Advance(advance.By);
                    continue;
                }

                yield return e;
            }
        }
    }

    /// <summary>Reads translated events back through the adapter, exactly as <c>FromMAFWorkflow</c> does.</summary>
    private static Task<WorkflowExecutionResult> AdaptAsync(IReadOnlyList<WorkflowEvent> translated)
    {
        var adapter = new MAFWorkflowAdapter(
            "synthetic",
            (prompt, ct) => Replay(translated),
            translated.OfType<ExecutorOutputEvent>().Select(e => e.ExecutorId).Distinct());
        return adapter.ExecuteWorkflowAsync("input");

        static async IAsyncEnumerable<WorkflowEvent> Replay(IReadOnlyList<WorkflowEvent> source)
        {
            foreach (var e in source)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private static IEnumerable<MAFWorkflows.WorkflowEvent> Wait(TimeSpan by) => [new ClockAdvance(by)];

    /// <summary>An agent's turn: the TurnToken is delivered, the turn takes <paramref name="takes"/>, streams its reply, completes.</summary>
    private static IEnumerable<MAFWorkflows.WorkflowEvent> AgentTurn(string executorId, TimeSpan takes, string reply) =>
    [
        new MAFWorkflows.ExecutorInvokedEvent(executorId, new MAFWorkflows.TurnToken(true)),
        new ClockAdvance(takes),
        StreamedText(executorId, reply),
        new MAFWorkflows.ExecutorCompletedEvent(executorId, null),
    ];

    /// <summary>A function executor handed <paramref name="message"/>, working for <paramref name="takes"/>, then completing with <paramref name="result"/>.</summary>
    private static IEnumerable<MAFWorkflows.WorkflowEvent> FunctionWork(string executorId, string message, TimeSpan takes, string result) =>
    [
        new MAFWorkflows.ExecutorInvokedEvent(executorId, message),
        new ClockAdvance(takes),
        new MAFWorkflows.ExecutorCompletedEvent(executorId, result),
    ];

    private static MAFWorkflows.AgentResponseUpdateEvent StreamedText(string executorId, string text) =>
        new(executorId, new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            AuthorName = executorId,
            Contents = [new TextContent(text)],
        });

    /// <summary>Not a MAF event: a marker in a test's event sequence that moves the fake clock on.</summary>
    private sealed class ClockAdvance(TimeSpan by) : MAFWorkflows.WorkflowEvent
    {
        public TimeSpan By { get; } = by;
    }
}
