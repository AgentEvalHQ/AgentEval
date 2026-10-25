// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;

namespace AgentEval.MAF;

/// <summary>Owns the telemetry sources used to measure explicitly instrumented MAF workflows.</summary>
/// <remarks>
/// Build the root and each declared subworkflow with <see cref="Build"/>, then use the usual
/// <see cref="MAFWorkflowAdapter.FromMAFWorkflow(Workflow, string, IEnumerable{string}, string)"/> entry point.
/// Keep this owner alive until all runs finish. Workflows are built once and retain their normal state semantics.
/// Each built workflow gets a private source; each evaluation run has a separate session identity.
/// </remarks>
public sealed class MAFWorkflowInstrumentation : IDisposable
{
    private static readonly ConditionalWeakTable<Workflow, ActivitySource> Sources = new();
    private readonly object _gate = new();
    private readonly List<ActivitySource> _owned = [];
    private bool _disposed;

    /// <summary>Builds a workflow with producer timing and an exclusive telemetry source.</summary>
    /// <param name="builder">The configured workflow builder. This explicitly replaces its telemetry configuration;
    /// sensitive payload capture remains disabled. The built workflow is not recreated between evaluations.</param>
    /// <param name="validateOrphans">Whether MAF validates unreachable executors when building.</param>
    /// <returns>The built workflow, ready for the normal AgentEval adapter or event bridge.</returns>
    public Workflow Build(WorkflowBuilder builder, bool validateOrphans = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var source = new ActivitySource("AgentEval.MAF.WorkflowTiming");
            try
            {
                var workflow = builder.WithOpenTelemetry(activitySource: source).Build(validateOrphans);
                Sources.Add(workflow, source);
                _owned.Add(source);
                return workflow;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
    }

    internal static ActivitySource? SourceFor(Workflow workflow) => Sources.TryGetValue(workflow, out var source) ? source : null;

    /// <summary>Releases all owned sources. Subsequent runs cannot obtain new producer timing from them.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var source in _owned) source.Dispose();
            _owned.Clear();
        }
    }
}
