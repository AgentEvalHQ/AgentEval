// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Models;
using AgentEval.Core;
using Microsoft.Extensions.AI;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.MAF;

public static partial class MAFWorkflowEventBridge
{
    private sealed class ActivationRecorder(
        IReadOnlySet<string> chatIds, IReadOnlyDictionary<string, string>? idMap,
        IReadOnlyList<string>? executorOrder, WorkflowGraphSnapshot? graph,
        ChatMessage? inputMessage, Func<TimeSpan> now, IReadOnlySet<string>? subworkflowIds,
        IReadOnlyDictionary<string, string>? parents)
    {
        private readonly Dictionary<string, Activation> _open = new(StringComparer.Ordinal);
        private readonly List<Activation> _ready = [];
        private readonly List<Activation> _emitted = [];
        private readonly Dictionary<(string Executor, string Call), PendingCall> _pendingCalls = [];
        private readonly Dictionary<string, BranchSummary> _branches = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedPendingRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Edge, int Source), int> _barrierDeliveries = [];
        private readonly Dictionary<(int Source, string Target), string> _forkBranches = [];
        private int _superstep;
        private int _ordinal;
        private Activation? _lastOutput;

        public string? FinalOutput => _lastOutput is null ? null : string.Concat(_lastOutput.Outputs.Select(output => output.Output));

        private string Normalize(string id) => idMap is not null && idMap.TryGetValue(id, out var clean) ? clean : id;
        private int Order(string id)
        {
            if (executorOrder is not null)
                for (var index = 0; index < executorOrder.Count; index++)
                    if (executorOrder[index] == id) return index;
            return int.MaxValue;
        }

        private Activation Get(string rawId)
        {
            if (!_open.TryGetValue(rawId, out var activation))
            {
                activation = new(rawId, Normalize(rawId), chatIds.Contains(rawId), subworkflowIds?.Contains(rawId) == true, _superstep, _ordinal++);
                _open.Add(rawId, activation);
                if (parents?.TryGetValue(rawId, out var parent) == true) activation.Parent = Get(parent);
            }
            return activation;
        }

        public IEnumerable<WorkflowEvent> Add(MAFWorkflows.WorkflowEvent evt)
        {
            switch (evt)
            {
                case MAFWorkflows.SuperStepStartedEvent:
                    _superstep++;
                    break;
                case MAFWorkflows.ExecutorInvokedEvent invoked:
                    var activation = Get(invoked.ExecutorId);
                    var isAnswer = invoked.Data is MAFWorkflows.ExternalResponse answer &&
                        (activation.PendingRequests.Contains(answer.RequestId) || activation.IsSubworkflow);
                    if (!isAnswer && activation.Start is not null &&
                        ((!activation.IsChat && activation.LastSuperstep != _superstep) ||
                         (activation.IsChat && activation.HasTurn && activation.Running == 0)))
                    {
                        Close(activation);
                        activation = Get(invoked.ExecutorId);
                    }
                    activation.Start ??= now();
                    activation.LastSuperstep = _superstep;
                    activation.Running++;
                    for (var parent = activation.Parent; parent is not null; parent = parent.Parent)
                    {
                        parent.HasNestedActivity = true;
                        parent.NestedRunning++;
                    }
                    activation.Inputs.Add(invoked.Data!);
                    activation.HasTurn |= !activation.IsChat || !IsChatProtocolAccumulation(invoked.Data);
                    activation.PairedReturn = null;
                    break;

                case MAFWorkflows.AgentResponseUpdateEvent update:
                    var updateOwner = Get(update.ExecutorId);
                    if (update.Update.MessageId is { } messageId) updateOwner.MessageIds.Add(messageId);
                    foreach (var call in update.Update.Contents.OfType<FunctionCallContent>())
                    {
                        var id = call.CallId ?? Guid.NewGuid().ToString();
                        var pending = new PendingCall(updateOwner, id, call.Name, call.Arguments, now());
                        updateOwner.Calls.Add(pending);
                        _pendingCalls[(updateOwner.RawId, id)] = pending;
                    }
                    foreach (var result in update.Update.Contents.OfType<FunctionResultContent>())
                    {
                        var id = result.CallId;
                        if (string.IsNullOrEmpty(id))
                            id = _pendingCalls.Values.Where(call => call.Owner.RawId == updateOwner.RawId)
                                .OrderByDescending(call => call.Start).FirstOrDefault()?.Id;
                        if (id is not null && _pendingCalls.Remove((updateOwner.RawId, id), out var pending))
                        {
                            pending.Result = result.Result;
                            pending.End = now();
                            if (pending.Owner.StepIndex >= 0) yield return pending.Event();
                        }
                    }
                    updateOwner.Text.Append(update.Update.Text);
                    if (!string.IsNullOrEmpty(update.Update.Text)) _lastOutput = updateOwner;
                    break;

                case MAFWorkflows.ExecutorCompletedEvent completed:
                    var completedOwner = Get(completed.ExecutorId);
                    CompleteDelivery(completedOwner);
                    if (completed.Data is MAFWorkflows.ExternalRequest pendingRequest)
                        completedOwner.PendingRequests.Add(pendingRequest.RequestId);
                    if (completed.Data is MAFWorkflows.ExternalResponse completedResponse)
                        completedOwner.PendingRequests.Remove(completedResponse.RequestId);
                    if (completedOwner.Text.Length == 0 && completed.Data is not null)
                    {
                        var text = ExtractTextFromResult(completed.Data, inputMessage);
                        if (!string.IsNullOrEmpty(text))
                        {
                            completedOwner.Text.Append(text);
                            _lastOutput = completedOwner;
                            completedOwner.PairedReturn = completed.Data;
                        }
                    }
                    if (completed.Data is MAFWorkflows.ExternalResponse returnedResponse && returnedResponse.TryGetDataAs<object>(out var answerValue))
                        completedOwner.Returns.Add(answerValue);
                    else if (completed.Data is not null) completedOwner.Returns.Add(completed.Data);
                    FlushText(completedOwner);
                    break;

                case MAFWorkflows.ExecutorFailedEvent failed:
                    var failedOwner = Get(failed.ExecutorId);
                    CompleteDelivery(failedOwner);
                    failedOwner.Failed = true;
                    var failure = failed.Data as Exception;
                    yield return new ExecutorErrorEvent(failedOwner.Id, failure?.Message ?? "Executor failed",
                        failure?.StackTrace, failure?.GetType().Name);
                    break;

                case MAFWorkflows.AgentResponseEvent response:
                    var responseOwner = Get(response.ExecutorId);
                    foreach (var message in response.Response.Messages)
                        if (message.MessageId is { } responseMessageId) responseOwner.MessageIds.Add(responseMessageId);
                    var responseText = responseOwner.Text.Length > 0 ? responseOwner.Text.ToString() : response.Response.Text;
                    responseOwner.Text.Clear();
                    var responseEvent = new ExecutorAgentResponseEvent(responseOwner.Id, responseText,
                        TokenUsageMapping.FromUsage(response.Response.Usage), response.Response.FinishReason?.ToString());
                    if (responseOwner.Start is { } responseStart)
                        responseEvent = responseEvent with { StartOffset = responseStart, Duration = now() - responseStart };
                    responseOwner.Outputs.Add(responseEvent);
                    if (!string.IsNullOrEmpty(responseText)) _lastOutput = responseOwner;
                    responseOwner.HasTurn = true;
                    break;

                case MAFWorkflows.WorkflowOutputEvent output:
                    var outputOwner = Get(output.ExecutorId);
                    if (outputOwner.IsSubworkflow) outputOwner.End = now();
                    _lastOutput = outputOwner;
                    FlushText(outputOwner);
                    if (outputOwner.PairedReturn is null || !Equals(outputOwner.PairedReturn, output.Data))
                    {
                        var text = ExtractTextFromResult(output.Data, inputMessage);
                        if (!string.IsNullOrEmpty(text)) outputOwner.Outputs.Add(new ExecutorOutputEvent(outputOwner.Id, text));
                    }
                    outputOwner.PairedReturn = null;
                    break;

                case MAFWorkflows.ExecutorEvent custom:
                    var customOwner = Get(custom.ExecutorId);
                    var customText = ExtractTextFromResult(custom.Data, inputMessage);
                    customOwner.Text.Append(customText);
                    if (!string.IsNullOrEmpty(customText)) _lastOutput = customOwner;
                    break;

                case MAFWorkflows.WorkflowErrorEvent error:
                    var exception = error.Data as Exception;
                    yield return new ExecutorErrorEvent(error is MAFWorkflows.SubworkflowErrorEvent nested
                        ? Normalize(nested.SubworkflowId) : "workflow", exception?.Message ?? "Workflow error",
                        exception?.StackTrace, exception?.GetType().Name);
                    break;

                case MAFWorkflows.RequestInfoEvent request:
                    var port = Normalize(request.Request.PortInfo.PortId);
                    _reportedPendingRequests.Add(request.Request.RequestId);
                    yield return new ExecutorErrorEvent(port,
                        $"Expected: completed workflow. Actual: request '{request.Request.RequestId}' is waiting on port '{port}'. " +
                        "Suggestions: use a workflow host that can supply the required response.");
                    break;

                case MAFWorkflows.SuperStepCompletedEvent:
                    foreach (var ready in _open.Values.Where(item => !item.IsSubworkflow && item.PendingRequests.Count == 0 &&
                        item.Running == 0 && (!item.IsChat || item.HasTurn)).ToArray())
                        Close(ready);
                    // Nested host steps must precede their children and remain open across child supersteps.
                    // MAF exposes no host-completed event, so buffer nested runs until their stream finishes.
                    if (parents is null || parents.Count == 0)
                        foreach (var translated in EmitReady()) yield return translated;
                    break;
            }
        }

        public IEnumerable<WorkflowEvent> Finish(bool streamCompleted)
        {
            if (!streamCompleted)
                foreach (var host in _open.Values.Concat(_ready).Where(item => item.IsSubworkflow))
                    host.NestedIncomplete = true;
            foreach (var pending in _open.Values)
                foreach (var request in pending.PendingRequests)
                    if (_reportedPendingRequests.Add(request))
                        yield return new ExecutorErrorEvent(pending.Id,
                            $"Expected: completed workflow. Actual: request '{request}' is waiting on port '{pending.Id}'. " +
                            "Suggestions: expose nested requests through an outer request-port relay and provide a responder.");
            foreach (var child in _open.Values.Where(item => TimingNotMeasuredReason(item) is not null))
                for (var parent = child.Parent; parent is not null; parent = parent.Parent)
                    parent.NestedIncomplete = true;
            foreach (var activation in _open.Values.ToArray()) Close(activation);
            foreach (var translated in EmitReady()) yield return translated;
            foreach (var branch in _branches.Values.Where(branch => branch.Measured))
                yield return new ParallelBranchEndEvent(branch.Id, branch.Executors, branch.Start,
                    branch.End - branch.Start, branch.Success, branch.Output);
        }

        private void CompleteDelivery(Activation activation)
        {
            activation.End = now();
            activation.Running = Math.Max(0, activation.Running - 1);
            for (var parent = activation.Parent; parent is not null; parent = parent.Parent)
            {
                parent.End = activation.End;
                parent.LastSuperstep = _superstep;
                parent.NestedRunning = Math.Max(0, parent.NestedRunning - 1);
            }
        }

        private static void FlushText(Activation activation)
        {
            if (activation.Text.Length == 0) return;
            activation.Outputs.Add(new ExecutorOutputEvent(activation.Id, activation.Text.ToString()));
            activation.Text.Clear();
        }

        private void Close(Activation activation)
        {
            if (TimingNotMeasuredReason(activation) is not null)
                for (var parent = activation.Parent; parent is not null; parent = parent.Parent)
                    parent.NestedIncomplete = true;
            FlushText(activation);
            _open.Remove(activation.RawId);
            _ready.Add(activation);
        }

        private IEnumerable<WorkflowEvent> EmitReady()
        {
            var batch = _ready.OrderBy(item => item.FirstSuperstep).ThenBy(item => Order(item.Id))
                .ThenBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Ordinal).ToArray();
            _ready.Clear();
            var previous = _emitted.ToArray();
            foreach (var activation in batch)
            {
                activation.StepIndex = _emitted.Count;
                _emitted.Add(activation);
            }
            foreach (var activation in batch) activation.Incoming.AddRange(Incoming(activation, previous));

            var forks = batch.SelectMany(target => target.Incoming.Where(edge => edge.EdgeType == EdgeType.ParallelFanOut)
                    .Select(edge => (edge.SourceStepIndex, Target: target)))
                .GroupBy(item => item.SourceStepIndex).Where(group => group.Select(item => item.Target.Id).Distinct().Count() > 1);
            foreach (var fork in forks)
                foreach (var target in fork.GroupBy(item => item.Target.Id))
                {
                    var key = (fork.Key!.Value, target.Key);
                    if (!_forkBranches.TryGetValue(key, out var branchId))
                        _forkBranches.Add(key, branchId = $"fork-{fork.Key}-step-{target.First().Target.StepIndex}");
                    foreach (var item in target)
                        item.Target.BranchId = branchId;
                }

            foreach (var activation in batch)
            {
                if (activation.BranchId is null && activation.Incoming.Count == 1 &&
                    activation.Incoming[0] is { EdgeType: EdgeType.ParallelFanOut, SourceStepIndex: { } source })
                    activation.BranchId = _forkBranches.GetValueOrDefault((source, activation.Id));
                if (activation.BranchId is null && activation.Incoming.Count == 1 &&
                    activation.Incoming[0].EdgeType != EdgeType.ParallelFanIn)
                    activation.BranchId = _emitted[activation.Incoming[0].SourceStepIndex!.Value].BranchId;
                activation.BranchId ??= activation.Parent?.BranchId;

                foreach (var edge in activation.Incoming) yield return edge;
                yield return Stamp(activation, new ExecutorOutputEvent(activation.Id, string.Empty)
                    { StartsNewStep = true, ParallelBranchId = activation.BranchId });
                foreach (var call in activation.Calls) yield return call.Event();
                foreach (var output in activation.Outputs) yield return output;
                yield return Stamp(activation, new ExecutorOutputEvent(activation.Id, string.Empty));

                if (activation.BranchId is { } branchId)
                {
                    if (!_branches.TryGetValue(branchId, out var branch))
                        _branches.Add(branchId, branch = new BranchSummary(branchId));
                    branch.Add(activation);
                }
            }
        }

        private IEnumerable<EdgeTraversedEvent> Incoming(Activation target, Activation[] previous)
        {
            if (graph is null)
            {
                var latestStep = previous.Where(source => source.FirstSuperstep < target.FirstSuperstep)
                    .Select(source => source.LastSuperstep).DefaultIfEmpty(-1).Max();
                var sources = previous.Where(source => source.LastSuperstep == latestStep).ToArray();
                if (sources.Length == 1 && latestStep >= target.FirstSuperstep - 1)
                    yield return Edge(sources[0], target, EdgeType.Sequential);
                yield break;
            }

            var inputs = target.Inputs.SelectMany(InputItems).ToArray();
            var candidates = graph.Edges.Where(edge => edge.TargetExecutorId == target.Id)
                .SelectMany(edge => Sources(edge).Select(source => (Edge: edge, Source: source))).ToArray();

            IEnumerable<Activation> Sources(WorkflowEdge edge)
            {
                var eligible = _emitted.Where(source => source.Id == edge.SourceExecutorId &&
                    source.Ordinal < target.Ordinal && source.FirstSuperstep < target.FirstSuperstep);
                if (edge.EdgeType == EdgeType.ParallelFanIn)
                {
                    eligible = eligible.Where(source =>
                        !_barrierDeliveries.TryGetValue((edge.EdgeId, source.StepIndex), out var deliveredAt) ||
                        deliveredAt == target.FirstSuperstep);
                    // A release drains all buffered messages, including several activations of one source.
                    // Preserve every identifiable contributor, not only the last activation of each edge.
                    var matched = eligible.Where(source => inputs.Any(input => Matches(source, input))).ToArray();
                    if (matched.Length > 0) return matched;
                }
                // Without matching payload evidence retain the existing immediate-predecessor rule.
                var latest = eligible.LastOrDefault(source => source.LastSuperstep >= target.FirstSuperstep - 1);
                return latest is null ? [] : [latest];
            }
            foreach (var candidate in candidates)
            {
                // A barrier's invocation proves all its inputs arrived. A unique incoming candidate is also
                // unambiguous. For other joins require matching payload evidence rather than inventing a source.
                if (candidates.Length > 1 && candidate.Edge.EdgeType != EdgeType.ParallelFanIn &&
                    !inputs.Any(input => Matches(candidate.Source, input) &&
                        candidates.Count(other => Matches(other.Source!, input)) == 1))
                    continue;
                // A barrier can hold an earlier source across several supersteps. Payload evidence identifies
                // that queued source; a consumed release cannot be reused by a later barrier cycle. Multiple
                // agent turns released in the same superstep still share that release's source activations.
                if (candidate.Edge.EdgeType == EdgeType.ParallelFanIn)
                    _barrierDeliveries[(candidate.Edge.EdgeId, candidate.Source!.StepIndex)] = target.FirstSuperstep;
                yield return Edge(candidate.Source!, target, candidate.Edge.EdgeType, candidate.Edge.EdgeId,
                    candidate.Edge.SwitchLabel);
            }
        }

        private static IEnumerable<object?> InputItems(object? input) =>
            input is System.Collections.IEnumerable items && input is not string ? items.Cast<object?>() : [input];

        private static bool Matches(Activation source, object? input) =>
            input is ChatMessage { MessageId: { } id } && source.MessageIds.Contains(id) ||
            source.Returns.Any(value => Equals(input, value));

        private static EdgeTraversedEvent Edge(Activation source, Activation target, EdgeType type,
            string? id = null, string? switchLabel = null) => new(source.Id, target.Id, type, id,
                type == EdgeType.Conditional ? true : null, switchLabel)
            { SourceStepIndex = source.StepIndex, TargetStepIndex = target.StepIndex, ObservedAt = target.Start };

        private static ExecutorOutputEvent Stamp(Activation activation, ExecutorOutputEvent output)
        {
            var reason = TimingNotMeasuredReason(activation);
            return reason is null
                ? output with { StartOffset = activation.Start, Duration = activation.End - activation.Start, TimingNotMeasuredReason = null }
                : output with { StartOffset = activation.Start, Duration = null, TimingNotMeasuredReason = reason };
        }

        private static string? TimingNotMeasuredReason(Activation activation) =>
            activation.IsSubworkflow && (!activation.HasNestedActivity || activation.NestedRunning != 0 || activation.NestedIncomplete)
                ? "Sub-workflow completion was not fully observed." :
            activation.PendingRequests.Count > 0 ? "The request port is waiting for a response." :
            activation.Start is null ? "Executor invocation was not observed." :
            activation.End is null || activation.Running != 0 ? "Executor completion was not observed before the event stream ended." :
            activation.IsChat && !activation.HasTurn ? "Agent turn was not observed; only chat accumulation was delivered." : null;

        private sealed class Activation(string rawId, string id, bool isChat, bool isSubworkflow, int superstep, int ordinal)
        {
            public string RawId { get; } = rawId;
            public string Id { get; } = id;
            public bool IsChat { get; } = isChat;
            public bool IsSubworkflow { get; } = isSubworkflow;
            public Activation? Parent { get; set; }
            public bool HasNestedActivity { get; set; }
            public int NestedRunning { get; set; }
            public bool NestedIncomplete { get; set; }
            public HashSet<string> PendingRequests { get; } = new(StringComparer.Ordinal);
            public int FirstSuperstep { get; } = superstep;
            public int LastSuperstep { get; set; } = superstep;
            public int Ordinal { get; } = ordinal;
            public int StepIndex { get; set; } = -1;
            public TimeSpan? Start { get; set; }
            public TimeSpan? End { get; set; }
            public int Running { get; set; }
            public bool HasTurn { get; set; }
            public bool Failed { get; set; }
            public object? PairedReturn { get; set; }
            public string? BranchId { get; set; }
            public StringBuilder Text { get; } = new();
            public List<ExecutorOutputEvent> Outputs { get; } = [];
            public List<PendingCall> Calls { get; } = [];
            public List<object> Inputs { get; } = [];
            public List<object> Returns { get; } = [];
            public HashSet<string> MessageIds { get; } = new(StringComparer.Ordinal);
            public List<EdgeTraversedEvent> Incoming { get; } = [];
        }

        private sealed class PendingCall(Activation owner, string id, string name, IDictionary<string, object?>? arguments, TimeSpan start)
        {
            public Activation Owner { get; } = owner;
            public string Id { get; } = id;
            public TimeSpan Start { get; } = start;
            public TimeSpan? End { get; set; }
            public object? Result { get; set; }
            public ExecutorToolCallEvent Event() => new(Owner.Id, name, Id, arguments, Result, End is { } end ? end - Start : default)
                { HasResult = End.HasValue };
        }

        private sealed class BranchSummary(string id)
        {
            public string Id { get; } = id;
            public List<string> Executors { get; } = [];
            public TimeSpan Start { get; private set; } = TimeSpan.MaxValue;
            public TimeSpan End { get; private set; }
            public bool Measured { get; private set; } = true;
            public bool Success { get; private set; } = true;
            public string Output { get; private set; } = string.Empty;
            public void Add(Activation activation)
            {
                if (!Executors.Contains(activation.Id)) Executors.Add(activation.Id);
                Measured &= TimingNotMeasuredReason(activation) is null;
                if (activation.Start is { } start && start < Start) Start = start;
                if (activation.End is { } end && end > End) End = end;
                Success &= !activation.Failed;
                Output = string.Concat(activation.Outputs.Select(output => output.Output));
            }
        }
    }
}
