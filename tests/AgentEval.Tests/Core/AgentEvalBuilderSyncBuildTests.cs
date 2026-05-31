// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Regression coverage for PERF-01: the synchronous <see cref="AgentEvalBuilder.Build"/> used
/// <c>BuildAsync().GetAwaiter().GetResult()</c>, which deadlocks on a thread with a captured
/// <see cref="SynchronizationContext"/> when a plugin's <c>InitializeAsync</c> resumes onto it.
/// Build() now offloads the async build via <see cref="System.Threading.Tasks.Task.Run{T}"/>.
/// </summary>
public class AgentEvalBuilderSyncBuildTests
{
    /// <summary>A SynchronizationContext that queues posted continuations but never pumps them —
    /// modelling a single-threaded UI/legacy-ASP.NET context whose thread is blocked in a sync wait.</summary>
    private sealed class NonPumpingSyncContext : SynchronizationContext
    {
        public readonly BlockingCollection<(SendOrPostCallback, object?)> Queue = new();
        public override void Post(SendOrPostCallback d, object? state) => Queue.Add((d, state));
    }

    private sealed class YieldingPlugin : IAgentEvalPlugin
    {
        public string PluginId => "yielding";
        public string Name => "yielding";
        public string? Description => "yields during init to force a continuation";
        public Version Version { get; } = new(1, 0, 0);
        public PluginLifecycleStage Stage => PluginLifecycleStage.Ready;
        public IReadOnlyList<string> Dependencies { get; } = Array.Empty<string>();

        public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => await Task.Yield(); // posts the continuation to the captured SynchronizationContext

        public Task OnBeforeEvaluationAsync(EvaluationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnAfterEvaluationAsync(EvaluationContext context, IList<MetricResult> results, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() { }
    }

    [Fact]
    public void Build_UnderCapturedSynchronizationContext_DoesNotDeadlock()
    {
        var completed = new ManualResetEventSlim(false);
        Exception? error = null;
        var built = false;

        var thread = new Thread(() =>
        {
            // Install a non-pumping context on this thread, then block in Build(). With the old
            // GetResult() implementation the plugin's Task.Yield continuation would post here and
            // never run (this thread is blocked) → deadlock.
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSyncContext());
            try
            {
                var runner = AgentEvalBuilder.Create()
                    .WithNoLogging()
                    .AddPlugin(new YieldingPlugin())
                    .Build();
                built = runner is not null;
                runner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
            finally { completed.Set(); }
        }) { IsBackground = true, Name = "sync-build-test" };

        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)),
            "Build() deadlocked under a captured SynchronizationContext (PERF-01).");
        Assert.Null(error);
        Assert.True(built);
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
