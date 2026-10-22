// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentEval.MAF;

/// <summary>Captures producer boundaries; reading queued WorkflowEvents cannot measure execution time.</summary>
internal sealed class MAFWorkflowTimingCapture : IDisposable
{
    private const string OperationPrefix = "executor.process ";
    private const string SourceName = "Microsoft.Agents.AI.Workflows";
    private static readonly AsyncLocal<MAFWorkflowTimingCapture?> Current = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Delivery>> _deliveries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Activity, Delivery> _running = new();
    private readonly ActivityListener _listener;
    private readonly long _origin;

    public MAFWorkflowTimingCapture(long origin)
    {
        _origin = origin;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => Sample(options.Name),
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => Sample(options.Name),
            ActivityStarted = Started,
            ActivityStopped = Stopped
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IDisposable Enter()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Scope(previous);
    }

    public Delivery? Take(string executorId) =>
        _deliveries.TryGetValue(executorId, out var queue) && queue.TryDequeue(out var delivery) ? delivery : null;

    private ActivitySamplingResult Sample(string name) => ReferenceEquals(Current.Value, this) &&
        name.StartsWith(OperationPrefix, StringComparison.Ordinal) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None;

    private void Started(Activity activity)
    {
        if (!ReferenceEquals(Current.Value, this) || !activity.OperationName.StartsWith(OperationPrefix, StringComparison.Ordinal)) return;
        // MAF sets executor.id after StartActivity returns, so it is not present in this callback yet.
        var id = activity.OperationName[OperationPrefix.Length..];
        var delivery = new Delivery(TimeProvider.System.GetElapsedTime(_origin));
        _running.TryAdd(activity, delivery);
        _deliveries.GetOrAdd(id, _ => new()).Enqueue(delivery);
    }

    private void Stopped(Activity activity)
    {
        if (_running.TryRemove(activity, out var delivery))
            delivery.Complete(TimeProvider.System.GetElapsedTime(_origin));
    }

    public void Dispose() => _listener.Dispose();

    internal sealed class Delivery(TimeSpan start)
    {
        private long _endTicks = -1;
        public TimeSpan Start { get; } = start;
        public TimeSpan? End
        {
            get
            {
                var ticks = Interlocked.Read(ref _endTicks);
                return ticks >= 0 ? TimeSpan.FromTicks(ticks) : null;
            }
        }
        public void Complete(TimeSpan end) => Interlocked.Exchange(ref _endTicks, end.Ticks);
    }

    private sealed class Scope(MAFWorkflowTimingCapture? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
