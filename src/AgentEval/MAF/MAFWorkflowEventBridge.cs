// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
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
/// ExecutorCompletedEvent, etc.) via <see cref="MAFWorkflows.StreamingRun.WatchStreamAsync"/>.
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
/// (1) Input as <see cref="ChatMessage"/> (not plain string — string messages are silently dropped), and
/// (2) A <see cref="MAFWorkflows.TurnToken"/> to trigger actual LLM processing.
/// This bridge sends both automatically. The <c>TurnToken</c> is forwarded downstream
/// through the workflow chain, so only the initial send is needed.
/// This pattern works with both <c>BindAsExecutor(emitEvents: true)</c> and
/// implicit <c>AIAgent → ExecutorBinding</c> conversion, since <c>TurnToken(emitEvents: true)</c>
/// overrides the binding's default.
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

        // Helper to translate full MAF IDs to clean names
        string NormalizeId(string id) =>
            executorIdMap is not null && executorIdMap.TryGetValue(id, out var clean) ? clean : id;

        // Track state for event translation
        string? previousExecutorId = null;
        string? currentExecutorId = null;
        var outputAccumulator = new StringBuilder();
        bool workflowOutputReceived = false;
        var sw = Stopwatch.StartNew();

        // Track pending tool calls: FunctionCallContent arrives first, FunctionResultContent later.
        // We buffer the call and yield a complete ExecutorToolCallEvent only when the result arrives.
        var pendingToolCalls = new Dictionary<string, (string ExecutorId, string ToolName, string? CallId, IDictionary<string, object?>? Arguments, TimeSpan StartTime)>();

        // Start the MAF workflow execution via InProcessExecution.
        // Detect if workflow uses ChatProtocol (AIAgent-based executors via BindAsExecutor)
        // to determine the correct input type and whether a TurnToken is needed.
        // This mirrors MAF's own BeginRunHandlingChatProtocolAsync logic.
        var protocol = await workflow.DescribeProtocolAsync(cancellationToken).ConfigureAwait(false);
        bool isChatProtocol = MAFWorkflows.ChatProtocolExtensions.IsChatProtocol(protocol);

        MAFWorkflows.StreamingRun run;
        if (isChatProtocol)
        {
            // AIAgentHostExecutor (from BindAsExecutor) requires ChatMessage input — plain strings
            // are silently dropped because ChatProtocolExecutor has no string handler by default.
            run = await MAFWorkflows.InProcessExecution
                .RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, input), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // ChatProtocolExecutor uses a two-phase protocol: messages are accumulated first,
            // then processed only when a TurnToken is received. RunStreamingAsync does NOT auto-send
            // a TurnToken (only RunAsync does). We must send it manually.
            // emitEvents: true ensures executor events are emitted regardless of binding defaults.
            // Subsequent executors receive the TurnToken automatically via downstream forwarding.
            await run.TrySendMessageAsync(new MAFWorkflows.TurnToken(emitEvents: true)).ConfigureAwait(false);
        }
        else
        {
            // Function-based executors (Func<string, ValueTask<string>>) handle string input directly.
            run = await MAFWorkflows.InProcessExecution
                .RunStreamingAsync<string>(workflow, input, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        await using var _ = run;

        // Watch the MAF event stream and translate each event
        await foreach (var mafEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (mafEvent)
            {
                // ── Executor lifecycle events ──────────────────────────────

                case MAFWorkflows.ExecutorInvokedEvent invoked:
                    // A new executor is starting. If we had a previous one, flush it.
                    var normalizedInvokedId = NormalizeId(invoked.ExecutorId);
                    if (currentExecutorId != null && currentExecutorId != normalizedInvokedId)
                    {
                        // Flush accumulated output for the previous executor
                        yield return new ExecutorOutputEvent(
                            currentExecutorId,
                            outputAccumulator.ToString());

                        // Emit edge between previous and current executor
                        yield return new EdgeTraversedEvent(
                            currentExecutorId,
                            normalizedInvokedId,
                            EdgeType.Sequential);

                        outputAccumulator.Clear();
                    }
                    else if (currentExecutorId == null && previousExecutorId == null)
                    {
                        // Very first executor — no edge to emit
                        outputAccumulator.Clear();
                    }

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
                            StartTime: sw.Elapsed
                        );
                    }

                    // Check for tool results (FunctionResultContent)
                    foreach (var result in agentUpdate.Update.Contents.OfType<FunctionResultContent>())
                    {
                        var resultCallId = result.CallId ?? string.Empty;
                        if (pendingToolCalls.TryGetValue(resultCallId, out var pending))
                        {
                            var duration = sw.Elapsed - pending.StartTime;
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
                            yield return new ExecutorOutputEvent(currentExecutorId, output);
                            outputAccumulator.Clear();
                        }
                    }
                    break;

                case MAFWorkflows.ExecutorFailedEvent failed:
                    // Executor encountered an error
                    var exception = failed.Data as Exception;
                    yield return new ExecutorErrorEvent(
                        NormalizeId(failed.ExecutorId),
                        exception?.Message ?? "Executor failed",
                        exception?.StackTrace,
                        exception?.GetType().Name);
                    break;

                // ── Workflow-level events ────────────────────────────────

                case MAFWorkflows.WorkflowOutputEvent workflowOutput:
                    // Workflow produced final output — flush any remaining output
                    if (currentExecutorId != null && outputAccumulator.Length > 0)
                    {
                        yield return new ExecutorOutputEvent(
                            currentExecutorId,
                            outputAccumulator.ToString());
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

                // ── SuperStep events (informational, no direct mapping) ──

                case MAFWorkflows.SuperStepStartedEvent:
                case MAFWorkflows.SuperStepCompletedEvent:
                case MAFWorkflows.WorkflowStartedEvent:
                case MAFWorkflows.WorkflowWarningEvent:
                case MAFWorkflows.RequestInfoEvent:
                    // These MAF events don't have direct AgentEval equivalents.
                    // They could be used for detailed tracing in the future.
                    break;
            }
        }

        // If the stream ended without a WorkflowOutputEvent, still flush and complete
        if (!workflowOutputReceived)
        {
            if (currentExecutorId != null && outputAccumulator.Length > 0)
            {
                yield return new ExecutorOutputEvent(
                    currentExecutorId,
                    outputAccumulator.ToString());
            }
            yield return new WorkflowCompleteEvent();
        }
    }

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
