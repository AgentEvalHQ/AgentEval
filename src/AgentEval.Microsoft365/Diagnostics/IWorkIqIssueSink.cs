// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// Receives every (already stamped and redacted) issue. Implementations must be thread-safe. A thrown exception
/// is swallowed by <see cref="WorkIqDiagnostics"/> into one last-resort stderr line and the sink is dropped for
/// the rest of the run; it never fails the operation that registered the issue.
/// </summary>
public interface IWorkIqIssueSink
{
    /// <summary>Writes one issue.</summary>
    /// <param name="i">The stamped, redacted issue.</param>
    void Write(WorkIqIssue i);
}
