// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// Implements <c>agenteval m365 doctor</c>: the readiness check an external tester runs first on their
/// own tenant. It wires the CLI options to <see cref="Microsoft365ReadinessCheck"/>, writes <c>readiness.md</c> and
/// <c>readiness.json</c> into the report folder (the trace and issue files go there too unless <c>--m365-trace</c>),
/// prints the console view and the file paths, and exits <c>0</c> when every stage that ran passed and nothing was
/// skipped for consent, <c>1</c> on any failed stage, <c>11</c> when nothing failed but a requested stage did not run
/// (the probe without <c>--i-understand-live-side-effects</c>), <c>2</c> on a usage error and <c>3</c> when the report
/// could not be written. Sibling of <see cref="Commands.McDoctorCommand"/> (portal binaries) and
/// <see cref="Commands.DoctorCommand"/> (workspace data); this one checks a live Work IQ connection.
/// </summary>
/// <remarks>
/// The command does not go through <c>SutTargetResolver</c>: it is not an evaluation, so it registers the consent flag
/// itself. Without consent it still runs the config, sign-in, discovery and protocol stages (no prompt reaches the
/// agent in any of them); the probe and the census are skipped and the command exits 11.
/// </remarks>
internal static class M365DoctorCommand
{
    /// <summary>The command name under <c>m365</c>.</summary>
    public const string CommandName = "doctor";

    /// <summary>The <c>--stage</c> value that runs the default stage set.</summary>
    public const string AllStages = "all";

    /// <summary>The default of <c>--timeout-seconds</c>.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The Markdown report written into the report folder.</summary>
    public const string MarkdownFileName = "readiness.md";

    /// <summary>The JSON report written into the report folder.</summary>
    public const string JsonFileName = "readiness.json";

    /// <summary>The prefix of a default report folder (<c>doctor-&lt;yyyyMMdd-HHmmss&gt;Z</c>).</summary>
    public const string ReportFolderPrefix = "doctor-";

    /// <summary>The values <c>--stage</c> accepts (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> StageValues = ["config", "auth", "discovery", "protocol", "probe", "census", AllStages];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Runs the readiness check (the provider's public API by default; tests inject an offline runner).</summary>
    /// <param name="configFile">The config file.</param>
    /// <param name="options">The check options.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    internal delegate Task<ReadinessReport> ReadinessRunner(FileInfo configFile, ReadinessCheckOptions options, CancellationToken cancellationToken);

    /// <summary>The bound command line of one invocation.</summary>
    internal sealed record DoctorArguments
    {
        /// <summary><c>--m365-config</c>.</summary>
        public FileInfo? ConfigFile { get; init; }

        /// <summary><c>--stage</c>.</summary>
        public string Stage { get; init; } = AllStages;

        /// <summary><c>--probe</c>.</summary>
        public bool Probe { get; init; }

        /// <summary><c>--i-understand-live-side-effects</c>.</summary>
        public bool Consent { get; init; }

        /// <summary><c>--continue</c>.</summary>
        public bool Continue { get; init; }

        /// <summary><c>--report</c>; null resolves to <see cref="DefaultReportDirectory"/>.</summary>
        public DirectoryInfo? Report { get; init; }

        /// <summary><c>--m365-profile</c>.</summary>
        public string Profile { get; init; } = Microsoft365CliOptions.DefaultProfile;

        /// <summary><c>--m365-capture-raw</c>.</summary>
        public DirectoryInfo? CaptureRaw { get; init; }

        /// <summary><c>--m365-verbose</c> (on by default for the doctor).</summary>
        public bool Verbose { get; init; } = true;

        /// <summary><c>--m365-trace</c>.</summary>
        public FileInfo? TraceFile { get; init; }

        /// <summary><c>--timeout-seconds</c>.</summary>
        public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    }

    /// <summary>Creates the <c>doctor</c> command bound to the real readiness check and the process console.</summary>
    public static Command Create() => Create(runner: null, stdout: null, stderr: null);

    /// <summary>Creates the <c>doctor</c> command with the readiness runner and the console destinations injectable (tests).</summary>
    /// <param name="runner">The readiness runner; null uses <see cref="Microsoft365ReadinessCheck.RunAsync(Microsoft365Config, FileInfo, ReadinessCheckOptions, CancellationToken)"/>.</param>
    /// <param name="stdout">Where the report view goes; null means <see cref="Console.Out"/> at invocation time.</param>
    /// <param name="stderr">Where the sign-in message, the verbose trace and errors go; null means <see cref="Console.Error"/> at invocation time.</param>
    internal static Command Create(ReadinessRunner? runner, TextWriter? stdout, TextWriter? stderr)
    {
        var configOpt = Microsoft365CliOptions.Config("Required.", required: true);
        var stageOpt = new Option<string>("--stage")
        {
            DefaultValueFactory = _ => AllStages,
            Description = "Which stage to run: config, auth, discovery, protocol, probe, census, or all (default: config, auth, discovery and protocol; --probe adds probe and census). The config stage always runs; a single network stage acquires its own token and card as needed.",
        };
        var probeOpt = new Option<bool>("--probe")
        {
            Description = "Also run the probe and the capability census. The probe sends one real prompt (\"Reply with the single word READY.\"). Under the default public profile the census sends the same prompt a SECOND time under the experimental native118 profile; it then calls CancelTask on the finished task (the expected answer is TaskNotCancelable). Needs --i-understand-live-side-effects; without it both are SKIPPED, no prompt is sent and the command exits 11.",
        };
        var consentOpt = new Option<bool>("--i-understand-live-side-effects")
        {
            Description = SutTargetResolver.ConsentDescription + " For m365 doctor it gates the probe and the census: without it they are SKIPPED and no prompt reaches the agent (the config, sign-in, discovery and protocol stages run without it).",
        };
        var continueOpt = new Option<bool>("--continue")
        {
            Description = "Run the remaining stages after a FAIL instead of stopping (a stage still needs a config that loaded).",
        };
        var reportOpt = new Option<DirectoryInfo?>("--report")
        {
            Description = "Folder for readiness.md, readiness.json and the trace and issues files (default: .agenteval/m365/doctor-<yyyyMMdd-HHmmss>Z when an .agenteval/ folder is found walking up from the current directory, else <temp>/agenteval-m365/doctor-<stamp>).",
        };
        var profileOpt = Microsoft365CliOptions.Profile();
        var captureRawOpt = Microsoft365CliOptions.CaptureRaw();
        var verboseOpt = Microsoft365CliOptions.Verbose(
            "their paths are printed at the end. On by default for m365 doctor (--m365-verbose false turns it off).", onByDefault: true);
        var traceOpt = Microsoft365CliOptions.Trace("in the report folder");
        var timeoutOpt = new Option<int>("--timeout-seconds")
        {
            DefaultValueFactory = _ => DefaultTimeoutSeconds,
            Description = "The most one stage may take for its network calls, in seconds (also the HTTP timeout and the probe's turn deadline). A device-code sign-in is not cut short by it: once the sign-in prompt is shown, the stage waits until the sign-in code expires (about 15 minutes). Default 120.",
        };

        var command = new Command(
            CommandName,
            "Readiness check of a Microsoft 365 agent over Work IQ A2A 1.0, stage by stage: config, sign-in, agent card, protocol handshake (GetTask on a random id); with --probe and consent, one real prompt and the capability census. " +
            "Stops at the first FAIL (--continue runs on) and writes readiness.md, readiness.json, the trace and the issues file into the report folder. Exit 0 all passed, 1 a stage failed, 11 a stage was skipped for consent, 2 usage, 3 the report or the diagnostics files could not be written.");
        command.Options.Add(configOpt);
        command.Options.Add(stageOpt);
        command.Options.Add(probeOpt);
        command.Options.Add(consentOpt);
        command.Options.Add(continueOpt);
        command.Options.Add(reportOpt);
        command.Options.Add(profileOpt);
        command.Options.Add(captureRawOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(traceOpt);
        command.Options.Add(timeoutOpt);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var arguments = new DoctorArguments
            {
                ConfigFile = parseResult.GetValue(configOpt),
                Stage = parseResult.GetValue(stageOpt) ?? AllStages,
                Probe = parseResult.GetValue(probeOpt),
                Consent = parseResult.GetValue(consentOpt),
                Continue = parseResult.GetValue(continueOpt),
                Report = parseResult.GetValue(reportOpt),
                Profile = parseResult.GetValue(profileOpt) ?? Microsoft365CliOptions.DefaultProfile,
                CaptureRaw = parseResult.GetValue(captureRawOpt),
                Verbose = parseResult.GetValue(verboseOpt),
                TraceFile = parseResult.GetValue(traceOpt),
                TimeoutSeconds = parseResult.GetValue(timeoutOpt),
            };
            return await ExecuteAsync(arguments, runner, stdout ?? Console.Out, stderr ?? Console.Error, ct).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>
    /// Validates the arguments (usage errors exit 2 before anything is created), resolves the report folder, runs the
    /// readiness check, writes <see cref="MarkdownFileName"/> and <see cref="JsonFileName"/>, prints the console view and
    /// the paths, and returns the exit code (<see cref="ExitCodeFor"/>).
    /// </summary>
    /// <param name="arguments">The bound command line.</param>
    /// <param name="runner">The readiness runner; null uses the provider's public API.</param>
    /// <param name="stdout">Where the report view and the paths go.</param>
    /// <param name="stderr">Where the sign-in message, the verbose trace and errors go.</param>
    /// <param name="cancellationToken">Stops the check (Ctrl+C); the report is still written.</param>
    /// <param name="now">The instant that names a default report folder; null uses the current UTC time.</param>
    internal static async Task<int> ExecuteAsync(
        DoctorArguments arguments,
        ReadinessRunner? runner,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        // ── usage (exit 2, nothing created, nothing sent) ──
        if (arguments.ConfigFile is null)
        {
            stderr.WriteLine($"m365 doctor: --m365-config <file.json> is required ({Microsoft365CliOptions.ConfigFields}).");
            return ExitCodes.UsageError;
        }

        if (!TryResolveStages(arguments.Stage, arguments.Probe, out var stages, out var stageError))
        {
            stderr.WriteLine(stageError);
            return ExitCodes.UsageError;
        }

        if (arguments.TimeoutSeconds < 1)
        {
            stderr.WriteLine($"m365 doctor: --timeout-seconds must be at least 1 (got {arguments.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}).");
            return ExitCodes.UsageError;
        }

        DiagnosticProfile profile;
        try
        {
            profile = Microsoft365CliOptions.ParseProfile(arguments.Profile);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine("m365 doctor: " + ex.Message);
            return ExitCodes.UsageError;
        }

        // ── the report folder ──
        var reportDirectory = arguments.Report ?? DefaultReportDirectory(now ?? DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(reportDirectory.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            stderr.WriteLine($"m365 doctor: the report folder could not be created: {reportDirectory.FullName} ({ex.GetType().Name}: {ex.Message})");
            return ExitCodes.RuntimeError;
        }

        var options = new ReadinessCheckOptions
        {
            Stages = stages,
            Probe = arguments.Probe,
            IUnderstandLiveSideEffects = arguments.Consent,
            ContinueAfterFailure = arguments.Continue,
            StageTimeout = TimeSpan.FromSeconds(arguments.TimeoutSeconds),
            Profile = profile,
            ProfileSource = "--m365-profile",
            RawCaptureDirectory = arguments.CaptureRaw,
            Verbose = arguments.Verbose,
            TraceFile = arguments.TraceFile,
            DiagnosticsDirectory = reportDirectory,
            Console = stderr,
        };

        stderr.WriteLine($"m365 doctor: report folder {reportDirectory.FullName}" + (arguments.Verbose ? "; every trace event follows on this stream (--m365-verbose false hides them)." : "."));
        stderr.Flush();

        // ── the check ──
        ReadinessReport report;
        try
        {
            report = await (runner ?? DefaultRunner)(arguments.ConfigFile, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"m365 doctor could not run: {ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}");
            return ExitCodes.RuntimeError;
        }

        report = report with { ReportDirectory = reportDirectory.FullName };

        // ── the report files ──
        var markdownPath = Path.Combine(reportDirectory.FullName, MarkdownFileName);
        var jsonPath = Path.Combine(reportDirectory.FullName, JsonFileName);
        string? writeError = null;
        try
        {
            File.WriteAllText(markdownPath, ReadinessReportRenderer.ToMarkdown(report), Utf8NoBom);
            File.WriteAllText(jsonPath, ReadinessReportRenderer.ToJson(report), Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writeError = $"m365 doctor: the report could not be written to {reportDirectory.FullName} ({ex.GetType().Name}: {ex.Message})";
        }

        ReadinessReportRenderer.WriteConsole(report, stdout);
        stdout.WriteLine();
        stdout.WriteLine($"Report folder: {reportDirectory.FullName}");
        stdout.WriteLine($"  {MarkdownFileName}:   {markdownPath}");
        stdout.WriteLine($"  {JsonFileName}: {jsonPath}");
        stdout.WriteLine($"Trace:  {report.TraceFile ?? "not written"}");
        stdout.WriteLine($"Issues: {report.IssueFile ?? "not written"}");
        stdout.WriteLine($"Send back the report folder, the trace and the issues file. {ReadinessReportRenderer.NoTokenStatement}");
        stdout.Flush();

        if (writeError is not null)
        {
            stderr.WriteLine(writeError);
            return ExitCodes.RuntimeError;
        }

        return ExitCodeFor(report);
    }

    /// <summary>
    /// The exit code of a report: <see cref="ExitCodes.TestFailure"/> (1) when a stage failed;
    /// <see cref="ExitCodes.GateIndeterminate"/> (11) when nothing failed but a stage was skipped for consent, or a
    /// requested stage did not run; <see cref="ExitCodes.Success"/> (0) when every requested stage passed.
    /// </summary>
    /// <param name="report">The report.</param>
    internal static int ExitCodeFor(ReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.AnyFailed)
        {
            return ExitCodes.TestFailure;
        }

        if (report.AnySkippedForConsent)
        {
            return ExitCodes.GateIndeterminate;
        }

        return report.AllPassed ? ExitCodes.Success : ExitCodes.GateIndeterminate;
    }

    /// <summary>
    /// Maps <c>--stage</c> (and <c>--probe</c>) to the stage set: <c>all</c> → null (the default set, with the probe and
    /// the census when <paramref name="probe"/>); a single stage → that stage plus the config (the census also needs the
    /// probe); <paramref name="probe"/> adds the probe and the census to a single stage.
    /// </summary>
    /// <param name="stage">The <c>--stage</c> value (case-insensitive).</param>
    /// <param name="probe">Whether <c>--probe</c> was given.</param>
    /// <param name="stages">The stage set; null means the default set.</param>
    /// <param name="error">The usage error when the value is not recognised.</param>
    internal static bool TryResolveStages(string? stage, bool probe, out IReadOnlySet<ReadinessStage>? stages, out string error)
    {
        HashSet<ReadinessStage>? set;
        switch (stage?.Trim().ToLowerInvariant())
        {
            case AllStages:
                stages = null;
                error = "";
                return true;
            case "config":
                set = [ReadinessStage.Config];
                break;
            case "auth":
                set = [ReadinessStage.Config, ReadinessStage.Auth];
                break;
            case "discovery":
                set = [ReadinessStage.Config, ReadinessStage.Discovery];
                break;
            case "protocol":
                set = [ReadinessStage.Config, ReadinessStage.Protocol];
                break;
            case "probe":
                set = [ReadinessStage.Config, ReadinessStage.Probe];
                break;
            case "census":
                set = [ReadinessStage.Config, ReadinessStage.Probe, ReadinessStage.Census];
                break;
            default:
                stages = null;
                error = $"m365 doctor: --stage must be one of {string.Join(", ", StageValues)}; got: '{stage}'.";
                return false;
        }

        if (probe)
        {
            set.UnionWith([ReadinessStage.Probe, ReadinessStage.Census]);
        }

        stages = set;
        error = "";
        return true;
    }

    /// <summary>
    /// The default report folder: <c>doctor-&lt;yyyyMMdd-HHmmss&gt;Z</c> under the workspace's <c>.agenteval/m365/</c>
    /// (the first <c>.agenteval</c> found walking up from the current directory), else under
    /// <c>&lt;temp&gt;/agenteval-m365/</c> — the same folder the run's trace and issue files default to. Nothing is created.
    /// </summary>
    /// <param name="now">The instant that names the folder.</param>
    internal static DirectoryInfo DefaultReportDirectory(DateTimeOffset now)
    {
        var baseDirectory = Microsoft365Diagnostics.Describe(new WorkIqOptions()).Directory;
        var stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new DirectoryInfo(Path.Combine(baseDirectory.FullName, ReportFolderPrefix + stamp + "Z"));
    }

    private static Task<ReadinessReport> DefaultRunner(FileInfo configFile, ReadinessCheckOptions options, CancellationToken cancellationToken) =>
        Microsoft365ReadinessCheck.RunAsync(config: null, configFile, options, cancellationToken);
}
