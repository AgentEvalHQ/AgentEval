// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>
/// The issue sink of a readiness check: forwards every issue to the issue register except the one reply the doctor
/// provokes on purpose while an expectation is active — the JSON-RPC TaskNotFound of the protocol stage's
/// <c>GetTask</c> on a random id, and the TaskNotCancelable of the census's <c>CancelTask</c> on a finished task.
/// Those replies are the expected answers, not anomalies; the trace still records them (<c>rpc.error</c> and the
/// mirrored <c>issue.*</c> event) and the doctor traces a line saying they were held back. Thread-safe.
/// </summary>
internal sealed class ExpectedIssueFilter : IWorkIqIssueSink, IDisposable
{
    private readonly IWorkIqIssueSink _inner;
    private Func<WorkIqIssue, bool>? _expected;
    private int _suppressed;

    /// <summary>Creates the filter.</summary>
    /// <param name="inner">The issue register the filter forwards to.</param>
    public ExpectedIssueFilter(IWorkIqIssueSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>How many issues were held back so far.</summary>
    public int Suppressed => Volatile.Read(ref _suppressed);

    /// <summary>Holds back the issues that match <paramref name="predicate"/> until the returned scope is disposed.</summary>
    /// <param name="predicate">Which issue is the expected answer.</param>
    public IDisposable Expect(Func<WorkIqIssue, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Volatile.Write(ref _expected, predicate);
        return new Scope(this);
    }

    /// <inheritdoc/>
    public void Write(WorkIqIssue i)
    {
        ArgumentNullException.ThrowIfNull(i);
        if (IsExpected(i))
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }

        _inner.Write(i);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// A sink that forwards to <paramref name="extra"/> under this filter's expectation, so a caller-owned issue sink
    /// receives exactly what the issue register receives. It does not count toward <see cref="Suppressed"/> (the
    /// register's own filter counts once per held-back issue), is a separate sink of the diagnostics (a failing extra
    /// sink is dropped alone, the register keeps writing) and is never disposed by the filter.
    /// </summary>
    /// <param name="extra">The caller's sink.</param>
    public IWorkIqIssueSink Filtering(IWorkIqIssueSink extra) => new View(this, extra ?? throw new ArgumentNullException(nameof(extra)));

    private bool IsExpected(WorkIqIssue issue) => Volatile.Read(ref _expected) is { } expected && expected(issue);

    private sealed class View : IWorkIqIssueSink, ICallerOwnedSink
    {
        private readonly ExpectedIssueFilter _filter;
        private readonly IWorkIqIssueSink _extra;

        public View(ExpectedIssueFilter filter, IWorkIqIssueSink extra)
        {
            _filter = filter;
            _extra = extra;
        }

        public object Inner => _extra;

        public void Write(WorkIqIssue i)
        {
            ArgumentNullException.ThrowIfNull(i);
            if (!_filter.IsExpected(i))
            {
                _extra.Write(i);
            }
        }
    }

    private sealed class Scope : IDisposable
    {
        private ExpectedIssueFilter? _owner;

        public Scope(ExpectedIssueFilter owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
            {
                Volatile.Write(ref owner._expected, null);
            }
        }
    }
}
