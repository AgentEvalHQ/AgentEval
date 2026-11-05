// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>An in-memory trace sink for tests and the doctor report: every event in order, as a snapshot. Thread-safe.</summary>
public sealed class ListTraceSink : IWorkIqTraceSink
{
    private readonly object _gate = new();
    private readonly List<WorkIqTraceEvent> _events = [];

    /// <summary>The events received so far, oldest first (a snapshot).</summary>
    public IReadOnlyList<WorkIqTraceEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>How many events were received.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <inheritdoc/>
    public void Write(WorkIqTraceEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_gate)
        {
            _events.Add(e);
        }
    }

    /// <summary>Forgets every event.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}

/// <summary>An in-memory issue sink for tests and the doctor report: every issue in order, as a snapshot. Thread-safe.</summary>
public sealed class ListIssueSink : IWorkIqIssueSink
{
    private readonly object _gate = new();
    private readonly List<WorkIqIssue> _issues = [];

    /// <summary>The issues received so far, oldest first (a snapshot).</summary>
    public IReadOnlyList<WorkIqIssue> Issues
    {
        get
        {
            lock (_gate)
            {
                return _issues.ToArray();
            }
        }
    }

    /// <summary>How many issues were received.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _issues.Count;
            }
        }
    }

    /// <inheritdoc/>
    public void Write(WorkIqIssue i)
    {
        ArgumentNullException.ThrowIfNull(i);
        lock (_gate)
        {
            _issues.Add(i);
        }
    }

    /// <summary>Forgets every issue.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _issues.Clear();
        }
    }
}
