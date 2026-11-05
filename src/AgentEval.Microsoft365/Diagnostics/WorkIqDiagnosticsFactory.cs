// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>Where the diagnostics directory of a run was resolved from.</summary>
internal enum WorkIqDiagnosticsDirectorySource
{
    /// <summary><see cref="WorkIqOptions.DiagnosticsDirectory"/> was set.</summary>
    Options,

    /// <summary>An <c>.agenteval/</c> workspace directory was found walking up from the current directory; its <c>m365/</c> subfolder is used.</summary>
    Workspace,

    /// <summary>No workspace: <c>&lt;temp&gt;/agenteval-m365</c>.</summary>
    Temp,
}

/// <summary>The resolved diagnostics locations of a run (nothing is created by resolving them).</summary>
/// <param name="RunId">The run id the file names use; null when none was given and none was minted (the trace file name then carries the <c>{runId}</c> placeholder).</param>
/// <param name="Directory">The diagnostics directory.</param>
/// <param name="TraceFile">The JSONL trace file.</param>
/// <param name="IssueFile">The JSONL issue register.</param>
/// <param name="DirectorySource">How <paramref name="Directory"/> was chosen.</param>
internal sealed record WorkIqDiagnosticsPaths(
    string? RunId,
    DirectoryInfo Directory,
    FileInfo TraceFile,
    FileInfo IssueFile,
    WorkIqDiagnosticsDirectorySource DirectorySource);

/// <summary>
/// Builds the <see cref="WorkIqDiagnostics"/> of a live run from the config and options: run id, trace and issue
/// files (under <see cref="WorkIqOptions.DiagnosticsDirectory"/>, else the workspace's <c>.agenteval/m365/</c>,
/// else the temp directory), the console sink when <see cref="WorkIqOptions.Verbose"/>, and the config identity
/// hash. The first traced event, <c>config.diagnostics_ready</c>, names both files so the tester knows what to send
/// back. <see cref="Describe"/> resolves the same paths without creating anything (the CLI prints them).
/// </summary>
internal static class WorkIqDiagnosticsFactory
{
    /// <summary>The trace file name prefix (<c>m365-trace-&lt;runId&gt;.jsonl</c>).</summary>
    public const string TraceFilePrefix = "m365-trace-";

    /// <summary>The trace file extension.</summary>
    public const string TraceFileExtension = ".jsonl";

    /// <summary>The issue register file name.</summary>
    public const string IssueFileName = "m365-issues.jsonl";

    /// <summary>The workspace marker directory searched upward from the current directory.</summary>
    public const string WorkspaceDirectoryName = ".agenteval";

    /// <summary>The subfolder of the workspace directory used for Work IQ diagnostics.</summary>
    public const string WorkspaceSubfolderName = "m365";

    /// <summary>The folder under the temp directory used when no workspace exists.</summary>
    public const string TempFolderName = "agenteval-m365";

    /// <summary>The placeholder in a described trace file name when no run id is known yet.</summary>
    public const string RunIdPlaceholder = "{runId}";

    /// <summary>The code of the first traced event.</summary>
    public const string ReadyEventCode = "config.diagnostics_ready";

    /// <summary>Mints a run id: <c>m365-&lt;yyyyMMdd-HHmmss UTC&gt;-&lt;4 random hex&gt;</c>.</summary>
    /// <param name="clock">The clock.</param>
    public static string NewRunId(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        return $"m365-{stamp}-{random}";
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the config's canonical identity — endpoint, agent id, tenant id, app client id,
    /// scope and profile (no secret exists in the config). The same value is meant for AEF run metadata.
    /// </summary>
    /// <param name="config">The config (validated or not).</param>
    public static string ComputeConfigSha256(Microsoft365Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string profile;
        try
        {
            profile = config.ResolveProfile().ToString();
        }
        catch (InvalidOperationException)
        {
            profile = config.DiagnosticProfile?.Trim() ?? "";
        }

        var canonical = JsonSerializer.Serialize(new ConfigIdentity(
            config.Endpoint?.Trim() ?? "",
            config.AgentId?.Trim(),
            config.TenantId?.Trim() ?? "",
            config.AppClientId?.Trim() ?? "",
            config.Scope?.Trim() ?? "",
            profile));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the diagnostics paths without creating anything: <see cref="WorkIqOptions.TraceFile"/> /
    /// <see cref="WorkIqOptions.IssueFile"/> win; otherwise the files sit in <see cref="WorkIqOptions.DiagnosticsDirectory"/>,
    /// else <c>&lt;workspace&gt;/.agenteval/m365/</c> (the first <c>.agenteval</c> found walking up from
    /// <paramref name="searchStartDirectory"/>, the current directory by default), else
    /// <paramref name="fallbackDirectory"/> (<c>&lt;temp&gt;/agenteval-m365</c> by default).
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="runId">The run id for the trace file name; null falls back to <see cref="WorkIqOptions.RunId"/>, then to the <c>{runId}</c> placeholder.</param>
    /// <param name="searchStartDirectory">Where the workspace search starts; null means the current directory.</param>
    /// <param name="fallbackDirectory">Where files go without a workspace; null means <c>&lt;temp&gt;/agenteval-m365</c>.</param>
    public static WorkIqDiagnosticsPaths Describe(WorkIqOptions options, string? runId = null, string? searchStartDirectory = null, string? fallbackDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolvedRunId = NormalizeRunId(runId) ?? NormalizeRunId(options.RunId);

        DirectoryInfo directory;
        WorkIqDiagnosticsDirectorySource source;
        if (options.DiagnosticsDirectory is not null)
        {
            directory = options.DiagnosticsDirectory;
            source = WorkIqDiagnosticsDirectorySource.Options;
        }
        else if (FindWorkspaceDirectory(searchStartDirectory ?? Directory.GetCurrentDirectory()) is { } workspace)
        {
            directory = new DirectoryInfo(Path.Combine(workspace, WorkspaceSubfolderName));
            source = WorkIqDiagnosticsDirectorySource.Workspace;
        }
        else
        {
            directory = new DirectoryInfo(fallbackDirectory ?? Path.Combine(Path.GetTempPath(), TempFolderName));
            source = WorkIqDiagnosticsDirectorySource.Temp;
        }

        var traceFile = options.TraceFile ?? new FileInfo(Path.Combine(directory.FullName, TraceFilePrefix + (resolvedRunId ?? RunIdPlaceholder) + TraceFileExtension));
        var issueFile = options.IssueFile ?? new FileInfo(Path.Combine(directory.FullName, IssueFileName));
        return new WorkIqDiagnosticsPaths(resolvedRunId, directory, traceFile, issueFile, source);
    }

    /// <summary>
    /// Creates the diagnostics of a live run: mints the run id when <see cref="WorkIqOptions.RunId"/> is blank,
    /// opens the JSONL trace and issue sinks (directories created), adds the console sink when
    /// <see cref="WorkIqOptions.Verbose"/>, appends the caller's <see cref="WorkIqOptions.TraceSinks"/> and
    /// <see cref="WorkIqOptions.IssueSinks"/> (never disposed with the run), stamps the config hash, and traces
    /// <c>config.diagnostics_ready</c> with both file paths as data.
    /// </summary>
    /// <param name="config">The config.</param>
    /// <param name="options">The options.</param>
    /// <param name="clock">The clock.</param>
    /// <exception cref="InvalidOperationException">A diagnostics file could not be opened; the message names the path.</exception>
    public static WorkIqDiagnostics Create(Microsoft365Config config, WorkIqOptions options, TimeProvider clock) =>
        Create(config, options, clock, searchStartDirectory: null, fallbackDirectory: null, console: null);

    /// <summary>The seam of <see cref="Create(Microsoft365Config, WorkIqOptions, TimeProvider)"/> with the directory search and console destination injectable (tests).</summary>
    /// <param name="config">The config.</param>
    /// <param name="options">The options.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="searchStartDirectory">Where the workspace search starts; null means the current directory.</param>
    /// <param name="fallbackDirectory">Where files go without a workspace; null means <c>&lt;temp&gt;/agenteval-m365</c>.</param>
    /// <param name="console">The verbose console destination; null means <see cref="Console.Error"/>.</param>
    public static WorkIqDiagnostics Create(
        Microsoft365Config config,
        WorkIqOptions options,
        TimeProvider clock,
        string? searchStartDirectory,
        string? fallbackDirectory,
        TextWriter? console)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var runId = NormalizeRunId(options.RunId) ?? NewRunId(clock);
        var paths = Describe(options, runId, searchStartDirectory, fallbackDirectory);

        var traceSinks = new List<IWorkIqTraceSink>();
        var issueSinks = new List<IWorkIqIssueSink>();
        JsonlTraceSink? traceSink = null;
        try
        {
            traceSink = new JsonlTraceSink(paths.TraceFile);
            traceSinks.Add(traceSink);
            if (options.Verbose)
            {
                traceSinks.Add(new ConsoleTraceSink(console));
            }

            issueSinks.Add(new JsonlIssueSink(paths.IssueFile));
        }
        catch (Exception)
        {
            traceSink?.Dispose();
            throw;
        }

        // The caller's own sinks come last: they see exactly what the JSONL files see, after the same stamping and
        // redaction, and they are never disposed with the run (the caller owns them).
        CallerOwnedTraceSink.AppendTo(traceSinks, options.TraceSinks);
        CallerOwnedIssueSink.AppendTo(issueSinks, options.IssueSinks);

        var diagnostics = new WorkIqDiagnostics(runId, ComputeConfigSha256(config), traceSinks, issueSinks, clock);
        diagnostics.Trace(new WorkIqTraceEvent(
            default,
            WorkIqTraceStage.Config,
            WorkIqTraceLevel.Info,
            ReadyEventCode,
            $"Diagnostics ready for run {runId}: trace file {paths.TraceFile.FullName}; issue file {paths.IssueFile.FullName} " +
            $"(directory from {DescribeSource(paths.DirectorySource)}; verbose console {(options.Verbose ? "on" : "off")}). Send both files back with a report.")
        {
            Data = JsonSerializer.SerializeToElement(new ReadyData(
                runId,
                paths.Directory.FullName,
                paths.TraceFile.FullName,
                paths.IssueFile.FullName,
                paths.DirectorySource.ToString(),
                options.Verbose,
                diagnostics.ConfigSha256,
                WorkIqDiagnostics.AdapterVersion,
                WorkIqDiagnostics.A2AVersion), WorkIqDiagnosticsJson.Options),
        });
        return diagnostics;
    }

    /// <summary>Walks up from <paramref name="start"/> and returns the first <c>.agenteval</c> directory, or null.</summary>
    /// <param name="start">The directory to start from.</param>
    public static string? FindWorkspaceDirectory(string start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, WorkspaceDirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? NormalizeRunId(string? runId) => string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();

    private static string DescribeSource(WorkIqDiagnosticsDirectorySource source) => source switch
    {
        WorkIqDiagnosticsDirectorySource.Options => "the DiagnosticsDirectory option",
        WorkIqDiagnosticsDirectorySource.Workspace => "the workspace's .agenteval/m365 folder",
        _ => "the temp directory (no .agenteval workspace found)",
    };

    private sealed record ConfigIdentity(string Endpoint, string? AgentId, string TenantId, string AppClientId, string Scope, string Profile);

    private sealed record ReadyData(
        string RunId,
        string Directory,
        string TraceFile,
        string IssueFile,
        string DirectorySource,
        bool Verbose,
        string? ConfigSha256,
        string? AdapterVersion,
        [property: JsonPropertyName("a2aVersion")] string A2AVersion);
}
