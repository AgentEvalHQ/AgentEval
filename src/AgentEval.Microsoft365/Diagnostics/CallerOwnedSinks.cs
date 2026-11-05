// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>A sink wrapper that forwards to a sink the caller owns; <see cref="WorkIqDiagnostics"/> names the inner sink when it fails.</summary>
internal interface ICallerOwnedSink
{
    /// <summary>The caller's sink.</summary>
    object Inner { get; }
}

/// <summary>
/// Forwards every event to a trace sink the caller owns (<see cref="WorkIqOptions.TraceSinks"/>,
/// <see cref="Readiness.ReadinessCheckOptions.TraceSinks"/>). It is deliberately not <see cref="IDisposable"/>:
/// <see cref="WorkIqDiagnostics.Dispose"/> disposes the sinks it owns, never the caller's.
/// </summary>
internal sealed class CallerOwnedTraceSink : IWorkIqTraceSink, ICallerOwnedSink
{
    private readonly IWorkIqTraceSink _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The caller's sink.</param>
    public CallerOwnedTraceSink(IWorkIqTraceSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public object Inner => _inner;

    /// <inheritdoc/>
    public void Write(WorkIqTraceEvent e) => _inner.Write(e);

    /// <summary>Wraps every sink of <paramref name="sinks"/> (none when null) and appends them to <paramref name="target"/>.</summary>
    /// <param name="target">The diagnostics' own sink list.</param>
    /// <param name="sinks">The caller's sinks.</param>
    /// <exception cref="ArgumentException">A sink is null.</exception>
    public static void AppendTo(List<IWorkIqTraceSink> target, IReadOnlyList<IWorkIqTraceSink>? sinks)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var sink in sinks ?? [])
        {
            target.Add(new CallerOwnedTraceSink(sink ?? throw new ArgumentException("A caller-owned trace sink list must not contain null.", nameof(sinks))));
        }
    }
}

/// <summary>
/// Forwards every issue to an issue sink the caller owns (<see cref="WorkIqOptions.IssueSinks"/>,
/// <see cref="Readiness.ReadinessCheckOptions.IssueSinks"/>); never disposed by the diagnostics.
/// </summary>
internal sealed class CallerOwnedIssueSink : IWorkIqIssueSink, ICallerOwnedSink
{
    private readonly IWorkIqIssueSink _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The caller's sink.</param>
    public CallerOwnedIssueSink(IWorkIqIssueSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public object Inner => _inner;

    /// <inheritdoc/>
    public void Write(WorkIqIssue i) => _inner.Write(i);

    /// <summary>Wraps every sink of <paramref name="sinks"/> (none when null) and appends them to <paramref name="target"/>.</summary>
    /// <param name="target">The diagnostics' own sink list.</param>
    /// <param name="sinks">The caller's sinks.</param>
    /// <exception cref="ArgumentException">A sink is null.</exception>
    public static void AppendTo(List<IWorkIqIssueSink> target, IReadOnlyList<IWorkIqIssueSink>? sinks)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var sink in sinks ?? [])
        {
            target.Add(new CallerOwnedIssueSink(sink ?? throw new ArgumentException("A caller-owned issue sink list must not contain null.", nameof(sinks))));
        }
    }
}
