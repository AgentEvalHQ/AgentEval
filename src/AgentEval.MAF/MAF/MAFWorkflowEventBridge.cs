// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using AgentEval.Core;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.MAF;

/// <summary>
/// Bridges MAF's class-based <see cref="MAFWorkflows.WorkflowEvent"/> hierarchy
/// to AgentEval's record-based <see cref="WorkflowEvent"/> hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// MAF emits events as a class hierarchy (ExecutorInvokedEvent, AgentResponseUpdateEvent,
/// ExecutorCompletedEvent, etc.) via <see cref="MAFWorkflows.StreamingRun.WatchStreamAsync(CancellationToken)"/>.
/// AgentEval expects record-based events (ExecutorOutputEvent, EdgeTraversedEvent, etc.)
/// consumed by <see cref="MAFWorkflowAdapter"/>.
/// </para>
/// <para>
/// This bridge translates between the two hierarchies by streaming a real MAF workflow
/// execution and yielding AgentEval-compatible events.
/// </para>
/// <para>
/// <b>ChatProtocol requirement:</b> MAF's <c>AIAgentHostExecutor</c> (created by
/// <c>BindAsExecutor</c>) uses <c>ChatProtocolExecutor</c> internally, which requires:
/// (1) Input as <see cref="ChatMessage"/> (a plain string is accepted only when the executor sets a string
/// message role, as MAF 1.24's <c>AIAgentHostExecutor</c> does; a <c>ChatProtocolExecutor</c> without one drops it), and
/// (2) A <see cref="MAFWorkflows.TurnToken"/> to trigger actual LLM processing.
/// This bridge sends both automatically. The <c>TurnToken</c> is forwarded downstream
/// through the workflow chain, so only the initial send is needed.
/// This pattern works with both <c>BindAsExecutor(emitEvents: true)</c> and
/// implicit <c>AIAgent → ExecutorBinding</c> conversion, since <c>TurnToken(emitEvents: true)</c>
/// overrides the binding's default.
/// </para>
/// <para>
/// <b>Step boundaries:</b> MAF raises an <c>ExecutorInvokedEvent</c> for every message it delivers, so one agent
/// turn shows up as 2-3 invocations of the same executor (the incoming chat messages, which only accumulate, and
/// the <c>TurnToken</c> that runs the turn). The bridge reports one step per executor activation: deliveries to
/// the open executor within one superstep, plus a chat-protocol executor's deliveries up to the turn they lead
/// to, are one step. Only a re-invocation in a LATER superstep, once the open step has run, is a re-entry
/// (a self-loop or a route back) and starts a new step.
/// </para>
/// <para>
/// <b>Step timing:</b> a step's output only goes out once the step has finished, so a consumer cannot time the step
/// by when its output arrives. The bridge stamps each <see cref="ExecutorOutputEvent"/> with
/// <see cref="ExecutorOutputEvent.StartOffset"/> and <see cref="ExecutorOutputEvent.Duration"/> instead: a step runs
/// from the <c>ExecutorInvokedEvent</c> that opened it (its first delivery) until its latest delivery's
/// <c>ExecutorCompletedEvent</c> or <c>ExecutorFailedEvent</c>, each read when the event arrives (InProcessExecution's
/// default OffThread mode streams events out as they are raised). An agent's first delivery is the chat messages it
/// accumulates, which MAF hands over in the same superstep as the <c>TurnToken</c> that runs the turn, so the step
/// starts when its turn starts; the start agent's input can land one superstep before its <c>TurnToken</c>, and its
/// step then also counts the moment it waited for it. An output whose end cannot be known yet (a fan-out sibling was
/// invoked while this executor was still running) goes out untimed, never with a made-up duration.
/// </para>
/// </remarks>
public static class MAFWorkflowEventBridge
{
    /// <summary>
    /// Executes a MAF <see cref="MAFWorkflows.Workflow"/> and yields AgentEval-compatible
    /// <see cref="WorkflowEvent"/> records translated from MAF's event stream.
    /// </summary>
    /// <param name="workflow">The built MAF Workflow to execute.</param>
    /// <param name="input">The input prompt to send to the workflow's starting executor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="executorIdMap">
    /// Optional mapping from full MAF executor IDs (e.g., "Planner_abc123") to clean display names
    /// (e.g., "Planner"). When provided, all emitted event IDs are normalized to clean names.
    /// </param>
    /// <returns>An async enumerable of AgentEval <see cref="WorkflowEvent"/> records.</returns>
    public static async IAsyncEnumerable<WorkflowEvent> StreamAsAgentEvalEvents(
        MAFWorkflows.Workflow workflow,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? executorIdMap = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(input);

        // Step timings count from here, when the caller started enumerating this run, so they share their origin
        // with the caller's own clock (MAFWorkflowAdapter starts its clock just before it enumerates this stream).
        var runStartedAt = TimeProvider.System.GetTimestamp();

        // Start the MAF workflow execution via InProcessExecution.
        // Detect if workflow uses ChatProtocol (AIAgent-based executors via BindAsExecutor)
        // to determine the correct input type and whether a TurnToken is needed.
        // This mirrors MAF's own BeginRunHandlingChatProtocolAsync logic.
        var protocol = await workflow.DescribeProtocolAsync(cancellationToken).ConfigureAwait(false);
        bool isChatProtocol = MAFWorkflows.ChatProtocolExtensions.IsChatProtocol(protocol);

        // Which executors take TURNS (accumulate chat messages, run on a TurnToken) — needed to tell one turn's
        // several message deliveries apart from a genuine re-entry (see TranslateEventsAsync). Read before the
        // run starts so a reflection failure cannot leak a started run.
        var chatProtocolExecutorIds = GetChatProtocolExecutorIds(workflow);

        MAFWorkflows.StreamingRun run;
        if (isChatProtocol)
        {
            // Send a ChatMessage: every ChatProtocolExecutor accumulates one, while a plain string is accepted
            // only when the executor sets a string message role (AIAgentHostExecutor does in MAF 1.24).
            run = await MAFWorkflows.InProcessExecution
                .RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, input), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Function-based executors (Func<string, ValueTask<string>>) handle string input directly.
            run = await MAFWorkflows.InProcessExecution
                .RunStreamingAsync<string>(workflow, input, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        // Register disposal immediately after run is assigned — BEFORE the chat-protocol
        // TurnToken send below. Previously the send happened inside the if-branch before this
        // `await using`, so a send failure leaked the in-process workflow run (BUG-50).
        await using var _ = run;

        if (isChatProtocol)
        {
            // ChatProtocolExecutor uses a two-phase protocol: messages are accumulated first,
            // then processed only when a TurnToken is received. RunStreamingAsync does NOT auto-send
            // a TurnToken (only RunAsync does). We must send it manually.
            // emitEvents: true ensures executor events are emitted regardless of binding defaults.
            // Subsequent executors receive the TurnToken automatically via downstream forwarding.
            await run.TrySendMessageAsync(new MAFWorkflows.TurnToken(emitEvents: true)).ConfigureAwait(false);
        }

        await foreach (var evt in TranslateEventsAsync(
            run.WatchStreamAsync(cancellationToken), chatProtocolExecutorIds, executorIdMap, cancellationToken,
            runStartedAt: runStartedAt)
            .ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Translates a MAF workflow event stream into AgentEval <see cref="WorkflowEvent"/> records — the
    /// translation half of <see cref="StreamAsAgentEvalEvents"/>, split out so the step-boundary rules can be
    /// exercised against exact MAF event orderings (e.g. the off-thread input race) that a live run only
    /// produces nondeterministically.
    /// </summary>
    /// <param name="mafEvents">The MAF event stream (normally <c>StreamingRun.WatchStreamAsync</c>).</param>
    /// <param name="chatProtocolExecutorIds">
    /// Raw MAF IDs of the executors that implement the chat protocol (<see cref="MAFWorkflows.ChatProtocolExecutor"/>).
    /// </param>
    /// <param name="executorIdMap">Optional full-ID → clean-name map, as for <see cref="StreamAsAgentEvalEvents"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="timeProvider">The clock step timings and tool-call durations are read from; the system clock by default.</param>
    /// <param name="runStartedAt">
    /// The <paramref name="timeProvider"/> timestamp the run started at, which step start offsets count from;
    /// defaults to when translation starts.
    /// </param>
    internal static async IAsyncEnumerable<WorkflowEvent> TranslateEventsAsync(
        IAsyncEnumerable<MAFWorkflows.WorkflowEvent> mafEvents,
        IReadOnlySet<string> chatProtocolExecutorIds,
        IReadOnlyDictionary<string, string>? executorIdMap = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        long? runStartedAt = null)
    {
        ArgumentNullException.ThrowIfNull(mafEvents);
        ArgumentNullException.ThrowIfNull(chatProtocolExecutorIds);

        // Helper to translate full MAF IDs to clean names
        string NormalizeId(string id) =>
            executorIdMap is not null && executorIdMap.TryGetValue(id, out var clean) ? clean : id;

        // Track state for event translation
        string? previousExecutorId = null;
        string? currentExecutorId = null;
        var outputAccumulator = new StringBuilder();
        bool workflowOutputReceived = false;

        // One clock for every time the bridge reports, read when a MAF event arrives
        // (see "Step timing" in the class remarks).
        var clock = timeProvider ?? TimeProvider.System;
        var origin = runStartedAt ?? clock.GetTimestamp();
        TimeSpan Now() => clock.GetElapsedTime(origin);

        // Timing of the OPEN step: it runs from the delivery that opened it until its latest delivery completed or
        // failed. openStepStartedAt is null when the bridge did not see the open step start; openStepRunningDeliveries
        // counts its deliveries that were invoked and have not completed yet. openStepVisible is true once the
        // consumer has the step open (an output of it or the edge into it went out), so a closing output for it,
        // even an empty one, only re-times that step and never adds one.
        TimeSpan? openStepStartedAt = null;
        TimeSpan? openStepCompletedAt = null;
        var openStepRunningDeliveries = 0;
        var openStepVisible = false;

        // Stamps an output of the open step with when MAF ran that step. Its end is the step's latest completed
        // delivery, or outputCompletedAt for an output that is complete while its delivery still runs (an agent's
        // complete response, a workflow output). While a delivery still runs and the output is not known complete —
        // a fan-out sibling was invoked before this executor finished — the end is unknown and the output goes out
        // untimed, so the adapter does not record a duration nobody measured.
        ExecutorOutputEvent Timed(ExecutorOutputEvent output, TimeSpan? outputCompletedAt = null)
        {
            openStepVisible = true;
            var end = openStepRunningDeliveries == 0 ? openStepCompletedAt : outputCompletedAt;
            return openStepStartedAt is { } start && end is { } stop
                ? output with { StartOffset = start, Duration = stop - start }
                : output;
        }

        // A delivery finished (ExecutorCompletedEvent / ExecutorFailedEvent). If it belongs to the open step, that
        // step has run at least until now; a sibling's delivery finishing says nothing about the open step.
        void DeliveryFinished(string? executorId)
        {
            if (executorId is not null && NormalizeId(executorId) == currentExecutorId)
            {
                openStepCompletedAt = Now();
                openStepRunningDeliveries = Math.Max(0, openStepRunningDeliveries - 1);
            }
        }

        // Step-boundary state for the OPEN step (the current executor's activation).
        // superStep counts MAF SuperStepStartedEvents; openStepSuperStep is the superstep of the open step's most
        // recent delivery; openStepAwaitingTurn is true while the open step belongs to a chat-protocol executor
        // that has so far only been handed messages to accumulate (its turn has not run yet).
        var superStep = 0;
        var openStepSuperStep = 0;
        var openStepAwaitingTurn = false;

        // Track pending tool calls: FunctionCallContent arrives first, FunctionResultContent later.
        // We buffer the call and yield a complete ExecutorToolCallEvent only when the result arrives.
        var pendingToolCalls = new Dictionary<string, (string ExecutorId, string ToolName, string? CallId, IDictionary<string, object?>? Arguments, TimeSpan StartTime)>();

        // Watch the MAF event stream and translate each event
        await foreach (var mafEvent in mafEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (mafEvent)
            {
                // ── Executor lifecycle events ──────────────────────────────

                case MAFWorkflows.ExecutorInvokedEvent invoked:
                    // MAF raises one ExecutorInvokedEvent per DELIVERED MESSAGE, not per executor run, so an
                    // invocation of the executor that is already open is not by itself a new step. An agent
                    // executor (BindAsExecutor → a ChatProtocolExecutor) is handed 2-3 messages per turn — the
                    // input / forwarded chat messages, which it only accumulates, then the TurnToken that runs
                    // the turn — and all of them are that ONE turn. A step is one ACTIVATION: what MAF delivers
                    // to the executor within one superstep (MAF's BSP unit; messages sent in superstep N are
                    // only delivered in N+1, so nothing can route back into an executor within its superstep).
                    var invokedAt = Now();
                    var normalizedInvokedId = NormalizeId(invoked.ExecutorId);
                    var invokedIsChatProtocol = chatProtocolExecutorIds.Contains(invoked.ExecutorId);
                    var opensStep = currentExecutorId != normalizedInvokedId;
                    if (currentExecutorId != null && currentExecutorId != normalizedInvokedId)
                    {
                        // Flush accumulated output for the previous executor
                        yield return Timed(new ExecutorOutputEvent(
                            currentExecutorId,
                            outputAccumulator.ToString()));

                        // Emit edge between previous and current executor
                        yield return new EdgeTraversedEvent(
                            currentExecutorId,
                            normalizedInvokedId,
                            EdgeType.Sequential);

                        outputAccumulator.Clear();
                    }
                    else if (currentExecutorId == normalizedInvokedId
                        && superStep > openStepSuperStep
                        && !openStepAwaitingTurn)
                    {
                        // 15: a DIRECT SELF-LOOP — an executor's own edge routes back to itself (e.g. a
                        // retry/refine step), so this ExecutorInvokedEvent re-invokes the SAME executor ID in a
                        // LATER superstep, after its open step has run. Neither branch above fires for this case,
                        // which used to silently merge this invocation's streaming output into the SAME
                        // accumulator as the PRIOR invocation's output (folding two steps into one
                        // ExecutorOutputEvent) and never emit an EdgeTraversedEvent(A, A) — the self-loop
                        // transition was invisible. Flush the prior invocation's output as its own step (with the
                        // self-edge) before starting a clean accumulator for this invocation, mirroring the
                        // flush-then-edge order above.
                        // The superstep + turn guard keeps this branch to GENUINE re-entries: without it every
                        // extra message delivery of one agent turn was flushed as a self-loop step, so a 4-agent
                        // BindAsExecutor chain reported 11 steps, 7 of them empty.
                        yield return Timed(new ExecutorOutputEvent(
                            currentExecutorId,
                            outputAccumulator.ToString()));

                        yield return new EdgeTraversedEvent(
                            currentExecutorId,
                            normalizedInvokedId,
                            EdgeType.Sequential);

                        outputAccumulator.Clear();
                        opensStep = true;
                    }
                    else if (currentExecutorId == null && previousExecutorId == null)
                    {
                        // Very first executor — no edge to emit
                        outputAccumulator.Clear();
                    }
                    // Otherwise: another delivery within the open step's activation — the same superstep, or a
                    // chat-protocol executor whose turn has not run yet. That second case is how the bridge's
                    // own input arrives: InProcessExecution runs the workflow off-thread, so the input
                    // ChatMessage and the TurnToken sent after it can land in different supersteps. It stays
                    // one step; nothing to flush.

                    // A chat-protocol step keeps awaiting its turn only while every delivery so far was a
                    // message the protocol merely accumulates; the TurnToken (or any other message) runs it.
                    openStepAwaitingTurn = (opensStep || openStepAwaitingTurn)
                        && invokedIsChatProtocol
                        && IsChatProtocolAccumulation(invoked.Data);
                    openStepSuperStep = superStep;

                    // A new step starts its clock at the delivery that opens it; any delivery runs until its
                    // ExecutorCompletedEvent or ExecutorFailedEvent. A step after the first one is visible at once:
                    // the edge into it just went out.
                    if (opensStep)
                    {
                        openStepStartedAt = invokedAt;
                        openStepCompletedAt = null;
                        openStepRunningDeliveries = 0;
                        openStepVisible = currentExecutorId != null;
                    }
                    openStepRunningDeliveries++;

                    previousExecutorId = currentExecutorId;
                    currentExecutorId = normalizedInvokedId;
                    break;

                // ── Agent streaming update (RC: now inherits WorkflowOutputEvent, not ExecutorEvent) ──
                case MAFWorkflows.AgentResponseUpdateEvent agentUpdate:
                    // Agent streaming update — inspect for tool calls AND text
                    var updateExecutorId = NormalizeId(agentUpdate.ExecutorId ?? currentExecutorId ?? "unknown");

                    // Check for tool call invocations (FunctionCallContent)
                    foreach (var call in agentUpdate.Update.Contents.OfType<FunctionCallContent>())
                    {
                        var callId = call.CallId ?? Guid.NewGuid().ToString();
                        pendingToolCalls[callId] = (
                            ExecutorId: updateExecutorId,
                            ToolName: call.Name,
                            CallId: callId,
                            Arguments: call.Arguments,
                            StartTime: Now()
                        );
                    }

                    // Check for tool results (FunctionResultContent)
                    foreach (var result in agentUpdate.Update.Contents.OfType<FunctionResultContent>())
                    {
                        var resultCallId = result.CallId ?? string.Empty;
                        
                        // Try exact CallId match first
                        if (!pendingToolCalls.TryGetValue(resultCallId, out var pending))
                        {
                            // Fallback: when CallId is null/empty, match the most recent pending call.
                            // This handles the case where FunctionCallContent.CallId was null (stored
                            // under a generated GUID) and FunctionResultContent.CallId is also null.
                            if (string.IsNullOrEmpty(resultCallId) && pendingToolCalls.Count > 0)
                            {
                                var fallbackEntry = pendingToolCalls
                                    .OrderByDescending(kvp => kvp.Value.StartTime)
                                    .First();
                                resultCallId = fallbackEntry.Key;
                                pending = fallbackEntry.Value;
                            }
                        }
                        
                        if (pending != default)
                        {
                            var duration = Now() - pending.StartTime;
                            yield return new ExecutorToolCallEvent(
                                ExecutorId: pending.ExecutorId,
                                ToolName: pending.ToolName,
                                CallId: pending.CallId,
                                Arguments: pending.Arguments,
                                Result: result.Result,
                                Duration: duration);
                            pendingToolCalls.Remove(resultCallId);
                        }
                    }

                    // Also extract text from the update (existing behavior)
                    var updateText = agentUpdate.Update.Text;
                    if (!string.IsNullOrEmpty(updateText))
                    {
                        outputAccumulator.Append(updateText);
                    }
                    break;

                case MAFWorkflows.ExecutorEvent genericExecutorEvent
                    when genericExecutorEvent is not MAFWorkflows.ExecutorInvokedEvent
                    && genericExecutorEvent is not MAFWorkflows.ExecutorCompletedEvent
                    && genericExecutorEvent is not MAFWorkflows.ExecutorFailedEvent:
                    // Catch remaining ExecutorEvent subtypes
                    // by extracting text from their Data property
                    var evtText = ExtractTextFromResult(genericExecutorEvent.Data);
                    if (!string.IsNullOrEmpty(evtText))
                    {
                        outputAccumulator.Append(evtText);
                    }
                    break;

                case MAFWorkflows.ExecutorCompletedEvent completed:
                    DeliveryFinished(completed.ExecutorId);

                    // Executor finished — extract text from result if no streaming output was accumulated
                    if (currentExecutorId != null)
                    {
                        // If no streaming text was accumulated, try to extract from the result
                        if (outputAccumulator.Length == 0 && completed.Data is not null)
                        {
                            var resultText = ExtractTextFromResult(completed.Data);
                            if (!string.IsNullOrEmpty(resultText))
                            {
                                outputAccumulator.Append(resultText);
                            }
                        }

                        var output = outputAccumulator.ToString();
                        if (!string.IsNullOrEmpty(output))
                        {
                            yield return Timed(new ExecutorOutputEvent(currentExecutorId, output));
                            outputAccumulator.Clear();
                        }
                    }
                    break;

                case MAFWorkflows.ExecutorFailedEvent failed:
                    // Executor encountered an error; the failed delivery ran until now.
                    DeliveryFinished(failed.ExecutorId);
                    var exception = failed.Data as Exception;
                    yield return new ExecutorErrorEvent(
                        NormalizeId(failed.ExecutorId),
                        exception?.Message ?? "Executor failed",
                        exception?.StackTrace,
                        exception?.GetType().Name);
                    break;

                // ── Agent complete (non-streaming) response ──────────────
                // AgentResponseEvent inherits WorkflowOutputEvent, so this case
                // MUST come before the generic WorkflowOutputEvent handler.

                case MAFWorkflows.AgentResponseEvent agentResponse:
                    // Non-streaming complete response from an executor.
                    // Extract per-executor text, token usage, and finish reason.
                    var responseExecutorId = NormalizeId(agentResponse.ExecutorId);

                    // Flush any previously accumulated streaming output for a different executor
                    if (currentExecutorId != null && currentExecutorId != responseExecutorId && outputAccumulator.Length > 0)
                    {
                        yield return Timed(new ExecutorOutputEvent(
                            currentExecutorId,
                            outputAccumulator.ToString()));
                        outputAccumulator.Clear();
                    }
                    if (currentExecutorId != responseExecutorId)
                    {
                        // A response from an executor the open step does not belong to (e.g. a concurrent sibling):
                        // the bridge did not see when that executor's step started, so its outputs go out untimed.
                        openStepStartedAt = null;
                    }
                    currentExecutorId = responseExecutorId;

                    // Build response text: prefer accumulated streaming text, fall back to Response.Text
                    var agentResponseText = outputAccumulator.Length > 0
                        ? outputAccumulator.ToString()
                        : agentResponse.Response.Text;
                    outputAccumulator.Clear();

                    // Extract token usage from AgentResponse.Usage: null unless both counts were reported (a missing
                    // count is not 0).
                    var responseUsage = TokenUsageMapping.FromUsage(agentResponse.Response.Usage);

                    // Extract finish reason (ChatFinishReason.ToString())
                    var finishReason = agentResponse.Response.FinishReason?.ToString();

                    // MAF raises the response inside the turn's handler, before its ExecutorCompletedEvent: the
                    // response is complete now.
                    yield return Timed(
                        new ExecutorAgentResponseEvent(
                            ExecutorId: responseExecutorId,
                            Output: agentResponseText,
                            Usage: responseUsage,
                            FinishReason: finishReason),
                        outputCompletedAt: Now());
                    break;

                // ── Workflow-level events ────────────────────────────────

                case MAFWorkflows.WorkflowOutputEvent workflowOutput:
                    // Workflow produced final output — flush any remaining output, or close a step the consumer
                    // already has open so it carries the step's final timing. A workflow output the open step's
                    // executor yields completes that step's output, even if its handler has not returned.
                    if (currentExecutorId != null && (outputAccumulator.Length > 0 || openStepVisible))
                    {
                        var outputCompletedAt = workflowOutput.ExecutorId is { } outputExecutorId
                            && NormalizeId(outputExecutorId) == currentExecutorId
                                ? Now()
                                : (TimeSpan?)null;
                        yield return Timed(
                            new ExecutorOutputEvent(currentExecutorId, outputAccumulator.ToString()),
                            outputCompletedAt);
                        outputAccumulator.Clear();
                    }
                    workflowOutputReceived = true;
                    yield return new WorkflowCompleteEvent();
                    break;

                case MAFWorkflows.WorkflowErrorEvent workflowError:
                    // Global workflow error
                    var errorData = workflowError.Data as Exception;
                    yield return new ExecutorErrorEvent(
                        "workflow",
                        errorData?.Message ?? "Workflow error",
                        errorData?.StackTrace,
                        errorData?.GetType().Name);
                    break;

                // ── SuperStep events (no direct mapping) ──

                case MAFWorkflows.SuperStepStartedEvent:
                    // Superstep boundaries delimit executor activations — see the ExecutorInvokedEvent case.
                    superStep++;
                    break;

                case MAFWorkflows.SuperStepCompletedEvent:
                case MAFWorkflows.WorkflowStartedEvent:
                case MAFWorkflows.WorkflowWarningEvent:
                case MAFWorkflows.RequestInfoEvent:
                    // These MAF events don't have direct AgentEval equivalents.
                    // They could be used for detailed tracing in the future.
                    break;
            }
        }

        // If the stream ended without a WorkflowOutputEvent, still flush (or close the open step, as above) and complete
        if (!workflowOutputReceived)
        {
            if (currentExecutorId != null && (outputAccumulator.Length > 0 || openStepVisible))
            {
                yield return Timed(new ExecutorOutputEvent(
                    currentExecutorId,
                    outputAccumulator.ToString()));
            }
            yield return new WorkflowCompleteEvent();
        }
    }

    /// <summary>
    /// The raw MAF IDs of the workflow's executors that implement the chat protocol — every
    /// <see cref="MAFWorkflows.ChatProtocolExecutor"/>, which includes each agent bound with <c>BindAsExecutor</c>
    /// (an <c>AIAgentBinding</c> declares <c>AIAgentHostExecutor</c> as its executor type before it is instantiated).
    /// </summary>
    internal static HashSet<string> GetChatProtocolExecutorIds(MAFWorkflows.Workflow workflow)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, binding) in workflow.ReflectExecutors())
        {
            if (binding.ExecutorType is { } executorType
                && typeof(MAFWorkflows.ChatProtocolExecutor).IsAssignableFrom(executorType))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Whether <paramref name="message"/> is one a <see cref="MAFWorkflows.ChatProtocolExecutor"/> only ACCUMULATES
    /// for its next turn (its <see cref="ChatMessage"/>, chat-message-collection and — when the executor sets a
    /// string message role, as <c>AIAgentHostExecutor</c> does — <see cref="string"/> handlers), as opposed to the
    /// <see cref="MAFWorkflows.TurnToken"/> that runs the turn or any other message.
    /// </summary>
    private static bool IsChatProtocolAccumulation(object? message) =>
        message is ChatMessage or IEnumerable<ChatMessage> or string;

    /// <summary>
    /// Attempts to extract text from an executor's completed result or event data.
    /// Uses reflection-free duck typing: checks for .Text property common to
    /// AgentResponse, AgentResponseUpdate, and string results.
    /// </summary>
    private static string? ExtractTextFromResult(object? result)
    {
        if (result is null)
            return null;

        // Plain string result
        if (result is string str)
            return str;

        // Duck-type: check for a Text property (covers AgentResponse, AgentResponseUpdate, etc.)
        var textProp = result.GetType().GetProperty("Text");
        if (textProp is not null && textProp.PropertyType == typeof(string))
        {
            return textProp.GetValue(result) as string;
        }

        // Fallback: use ToString() if it provides meaningful output
        var text = result.ToString();
        return text != result.GetType().ToString() ? text : null;
    }
}
