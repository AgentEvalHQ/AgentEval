// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
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
/// same function executor within one superstep are one step. A chat-protocol executor's deliveries up to each
/// turn are one activation; a second turn starts another even in the same superstep. Concurrent executors own
/// separate output, tool, usage and timing state. Completed activations are emitted at superstep boundaries in
/// configured executor order, with ordinal name order for executors not in that list.
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
/// step then also counts the moment it waited for it. Each parallel sibling is timed to its own completion,
/// independently of display order. A step whose completion was never observed is emitted without measured timing.
/// </para>
/// </remarks>
public static partial class MAFWorkflowEventBridge
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
    public static IAsyncEnumerable<WorkflowEvent> StreamAsAgentEvalEvents(
        MAFWorkflows.Workflow workflow,
        string input,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? executorIdMap = null) =>
        StreamAsAgentEvalEventsCore(workflow, input, cancellationToken, executorIdMap);

    internal static async IAsyncEnumerable<WorkflowEvent> StreamAsAgentEvalEventsCore(
        MAFWorkflows.Workflow workflow,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? executorIdMap = null,
        IReadOnlyList<string>? executorOrder = null,
        WorkflowGraphSnapshot? graph = null)
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
        graph ??= MAFGraphExtractor.ExtractGraph(workflow, workflow.ReflectExecutors().Keys.ToList(), executorIdMap);

        MAFWorkflows.StreamingRun run;
        ChatMessage? inputMessage = null;
        if (isChatProtocol)
        {
            // Send a ChatMessage: every ChatProtocolExecutor accumulates one, while a plain string is accepted
            // only when the executor sets a string message role (AIAgentHostExecutor does in MAF 1.24).
            inputMessage = new ChatMessage(ChatRole.User, input);
            run = await MAFWorkflows.InProcessExecution
                .RunStreamingAsync(workflow, inputMessage, cancellationToken: cancellationToken)
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
            run.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken: cancellationToken),
            chatProtocolExecutorIds, executorIdMap, cancellationToken,
            runStartedAt: runStartedAt, inputMessage: inputMessage, executorOrder: executorOrder, graph: graph)
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
    /// <param name="inputMessage">The original chat input, excluded by identity from chat-list output.</param>
    /// <param name="executorOrder">Preferred display-name order within a superstep; remaining names sort ordinally.</param>
    /// <param name="graph">Reflected topology for attributing observed transitions; null for custom event streams.</param>
    internal static async IAsyncEnumerable<WorkflowEvent> TranslateEventsAsync(
        IAsyncEnumerable<MAFWorkflows.WorkflowEvent> mafEvents,
        IReadOnlySet<string> chatProtocolExecutorIds,
        IReadOnlyDictionary<string, string>? executorIdMap = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        long? runStartedAt = null,
        ChatMessage? inputMessage = null,
        IReadOnlyList<string>? executorOrder = null,
        WorkflowGraphSnapshot? graph = null)
    {
        ArgumentNullException.ThrowIfNull(mafEvents);
        ArgumentNullException.ThrowIfNull(chatProtocolExecutorIds);

        var clock = timeProvider ?? TimeProvider.System;
        var origin = runStartedAt ?? clock.GetTimestamp();
        var recorder = new ActivationRecorder(chatProtocolExecutorIds, executorIdMap, executorOrder, graph,
            inputMessage, () => clock.GetElapsedTime(origin));

        Exception? streamError = null;
        await using var enumerator = mafEvents.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                streamError = error;
                break;
            }
            foreach (var translated in recorder.Add(enumerator.Current))
                yield return translated;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (streamError is not null)
            yield return new ExecutorErrorEvent("workflow", streamError.Message, streamError.StackTrace, streamError.GetType().Name);
        foreach (var translated in recorder.Finish())
            yield return translated;
        yield return new WorkflowCompleteEvent { FinalOutput = recorder.FinalOutput };
    }

    /// <summary>
    /// The raw MAF IDs of the workflow's executors that implement the chat protocol — every
    /// <see cref="MAFWorkflows.ChatProtocolExecutor"/> and <see cref="MAFWorkflows.ChatForwardingExecutor"/>, including each agent bound with <c>BindAsExecutor</c>
    /// (an <c>AIAgentBinding</c> declares <c>AIAgentHostExecutor</c> as its executor type before it is instantiated).
    /// </summary>
    internal static HashSet<string> GetChatProtocolExecutorIds(MAFWorkflows.Workflow workflow)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, binding) in workflow.ReflectExecutors())
        {
            if (binding.ExecutorType is { } executorType
                && (typeof(MAFWorkflows.ChatProtocolExecutor).IsAssignableFrom(executorType)
                    || executorType == typeof(MAFWorkflows.ChatForwardingExecutor)))
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
    private static string? ExtractTextFromResult(object? result, ChatMessage? inputMessage = null)
    {
        // A request-port handler returns its request envelope, not an answer from the executor.
        if (result is null or MAFWorkflows.ExternalRequest)
            return null;

        // Plain string result
        if (result is string str)
            return str;

        if (result is IEnumerable<ChatMessage> messages)
            return string.Join(Environment.NewLine, messages.Where(message => !ReferenceEquals(message, inputMessage))
                .Select(message => message.Text));

        if (result is ChatMessage message)
            return ReferenceEquals(message, inputMessage) ? string.Empty : message.Text;

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
