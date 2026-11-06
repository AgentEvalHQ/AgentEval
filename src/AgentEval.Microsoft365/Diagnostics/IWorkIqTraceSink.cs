// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// Receives every (already stamped and redacted) trace event. Implementations must be thread-safe: seams trace
/// from the auth handler, the capture handler and the stream reader concurrently. A thrown exception is swallowed
/// by <see cref="WorkIqDiagnostics"/> into one last-resort stderr line and the sink is dropped for the rest of
/// the run; it never fails the operation being traced.
/// </summary>
public interface IWorkIqTraceSink
{
    /// <summary>Writes one event.</summary>
    /// <param name="e">The stamped, redacted event.</param>
    void Write(WorkIqTraceEvent e);
}
