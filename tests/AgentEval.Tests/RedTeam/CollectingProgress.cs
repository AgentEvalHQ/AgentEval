// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// An <see cref="IProgress{T}"/> that records each report on the reporting thread, under a lock.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> queues every callback to the thread pool, so its callbacks can run concurrently with one
/// another and after the awaited scan has returned: a <c>List.Add</c> from them corrupts the list, and an assertion that
/// enumerates it can meet a callback still adding ("Collection was modified"). That flaked
/// <c>ScanAsync_ReportsProgress</c> (#203 review round 3, B10j). Every report here has landed when the scan returns.
/// </remarks>
internal sealed class CollectingProgress<T> : IProgress<T>
{
    private readonly object _gate = new();
    private readonly List<T> _reports = [];

    public void Report(T value)
    {
        lock (_gate)
            _reports.Add(value);
    }

    /// <summary>A snapshot of the reports so far.</summary>
    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_gate)
                return [.. _reports];
        }
    }
}
