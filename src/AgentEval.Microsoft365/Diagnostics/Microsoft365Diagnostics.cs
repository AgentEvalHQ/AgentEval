// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The resolved diagnostics locations of a run, as the CLI prints them before the first turn. Nothing is created by
/// resolving them. The public projection of the provider's internal <c>WorkIqDiagnosticsPaths</c>.
/// </summary>
/// <param name="RunId">The run id the trace file name carries; null when <see cref="WorkIqOptions.RunId"/> is blank (the trace file name then carries the <c>{runId}</c> placeholder).</param>
/// <param name="Directory">The diagnostics directory.</param>
/// <param name="TraceFile">The JSONL trace file.</param>
/// <param name="IssueFile">The JSONL issue register.</param>
public sealed record Microsoft365DiagnosticsPaths(string? RunId, DirectoryInfo Directory, FileInfo TraceFile, FileInfo IssueFile);

/// <summary>
/// The public face of the provider's diagnostics factory for callers outside this assembly (the CLI's
/// <c>--sut microsoft365</c> target and <c>agenteval m365 doctor</c>). It only forwards to the internal
/// <c>WorkIqDiagnosticsFactory</c>, so the paths a caller prints are exactly the paths a live run writes to.
/// Nothing here makes a network call or creates a file.
/// </summary>
public static class Microsoft365Diagnostics
{
    /// <summary>
    /// Mints a run id (<c>m365-&lt;yyyyMMdd-HHmmss UTC&gt;-&lt;4 hex&gt;</c>) for <see cref="WorkIqOptions.RunId"/>, so a
    /// caller can name the trace file before the run starts instead of printing the <c>{runId}</c> placeholder.
    /// </summary>
    public static string NewRunId() => WorkIqDiagnosticsFactory.NewRunId(TimeProvider.System);

    /// <summary>
    /// Resolves the trace and issue file paths a live run under <paramref name="options"/> would write, without creating
    /// anything: <see cref="WorkIqOptions.TraceFile"/> and <see cref="WorkIqOptions.IssueFile"/> win; otherwise the files
    /// sit in <see cref="WorkIqOptions.DiagnosticsDirectory"/>, else the workspace's <c>.agenteval/m365/</c>, else
    /// <c>&lt;temp&gt;/agenteval-m365</c>.
    /// </summary>
    /// <param name="options">The options of the run.</param>
    public static Microsoft365DiagnosticsPaths Describe(WorkIqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var paths = WorkIqDiagnosticsFactory.Describe(options);
        return new Microsoft365DiagnosticsPaths(paths.RunId, paths.Directory, paths.TraceFile, paths.IssueFile);
    }
}
