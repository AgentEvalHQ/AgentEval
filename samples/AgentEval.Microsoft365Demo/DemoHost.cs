// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;
using Microsoft.Extensions.AI;

namespace AgentEval.Microsoft365Demo;

/// <summary>The demo's exit codes.</summary>
internal static class DemoExitCodes
{
    /// <summary>Every stage that ran passed and everything requested was measured.</summary>
    public const int Success = 0;

    /// <summary>A stage failed (a readiness stage, or the prompt did not complete) or a case FAILED a check.</summary>
    public const int Failure = 1;

    /// <summary>A command-line mistake; nothing was created or sent.</summary>
    public const int Usage = 2;

    /// <summary>A runtime error, or a file could not be written; the run folder is still written.</summary>
    public const int Runtime = 3;

    /// <summary>Nothing failed, but something was not measured (no consent past readiness, no judge, a turn that did not complete, the turn cap, no config).</summary>
    public const int NotMeasured = 11;
}

/// <summary>
/// The host seams of <see cref="DemoHost.RunAsync"/>, for tests and embedding hosts. Every member is optional; null keeps
/// the real behaviour. <see cref="InnerHandler"/> and <see cref="TokenProvider"/> reach the provider only through its
/// public seams (<see cref="Microsoft365AgentFactory.BuildLive"/> and
/// <see cref="Microsoft365ReadinessCheck.RunAsync(Microsoft365Config, FileInfo, ReadinessCheckOptions, HttpMessageHandler, IWorkIqTokenProvider, CancellationToken)"/>);
/// the demo never substitutes a mock target of its own.
/// </summary>
internal sealed record DemoHostOverrides
{
    /// <summary>The innermost HTTP handler of every Work IQ connection (readiness and the live agent). The demo never disposes it.</summary>
    public HttpMessageHandler? InnerHandler { get; init; }

    /// <summary>The token source of every Work IQ connection; null uses the provider's MSAL device-code sign-in.</summary>
    public IWorkIqTokenProvider? TokenProvider { get; init; }

    /// <summary>The screen's standard output; null is <see cref="Console.Out"/> (and only then may lines be coloured).</summary>
    public TextWriter? Out { get; init; }

    /// <summary>The screen's error output; null is <see cref="Console.Error"/>.</summary>
    public TextWriter? Err { get; init; }

    /// <summary>Where default run folders are created; null is <c>./m365-demo-runs</c>. <c>--out</c> wins over it.</summary>
    public DirectoryInfo? RunsRoot { get; init; }

    /// <summary>The clock that names the run folder and stamps the reports; null is <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset>? Now { get; init; }

    /// <summary>The judge's chat client; null resolves one from the environment (<c>AI_INFERENCE_PROVIDER</c>). <c>--no-judge</c> wins.</summary>
    public IChatClient? JudgeClient { get; init; }

    /// <summary>Reads an environment variable (<c>M365_CONFIG_PATH</c>, the consent variable, the judge's provider, <c>NO_COLOR</c>); null reads the process environment.</summary>
    public Func<string, string?>? GetEnvironmentVariable { get; init; }
}

/// <summary>
/// The demo: parses the command line, then banner → readiness → one prompt → benchmark → summary, with the screen at
/// the chosen verbosity and every file in the run folder. All demo output goes to standard output; standard error carries
/// usage errors (exit 2) and runtime errors (exit 3) only.
/// </summary>
internal static class DemoHost
{
    /// <summary>The prompt step's stage name.</summary>
    public const string PromptStage = "prompt";

    /// <summary>The benchmark's stage name.</summary>
    public const string BenchmarkStage = "benchmark";

    /// <summary>The prefix of the readiness stages' names.</summary>
    public const string ReadinessPrefix = "readiness: ";

    /// <summary>Runs the demo and returns the exit code (<see cref="DemoExitCodes"/>).</summary>
    /// <param name="args">The command line.</param>
    /// <param name="overrides">Host seams; null for none.</param>
    /// <param name="cancellationToken">Stops the run (Ctrl+C): the stage in progress ends, the rest are not run, the run folder is written.</param>
    internal static async Task<int> RunAsync(string[] args, DemoHostOverrides? overrides = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        overrides ??= new DemoHostOverrides();
        var stdout = overrides.Out ?? Console.Out;
        var stderr = overrides.Err ?? Console.Error;
        var env = overrides.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var now = overrides.Now ?? (() => DateTimeOffset.UtcNow);

        // ── the command line (usage errors exit 2 before anything is created or sent) ──
        var cli = new DemoCommandLine.Definition();
        var parse = cli.Root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                stderr.WriteLine("m365 demo: " + error.Message);
            }

            stderr.WriteLine("Run with --help to see every option.");
            stderr.Flush();
            return DemoExitCodes.Usage;
        }

        if (parse.Action is not null)
        {
            // --help or --version.
            return await parse.InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, cancellationToken).ConfigureAwait(false);
        }

        if (!DemoCommandLine.TryBind(parse, cli, env, out var settings, out var usageError) || settings is null)
        {
            stderr.WriteLine("m365 demo: " + usageError);
            stderr.WriteLine("Run with --help to see every option.");
            stderr.Flush();
            return DemoExitCodes.Usage;
        }

        DemoCaseSet? caseSet = null;
        if (settings.DatasetFile is not null)
        {
            try
            {
                caseSet = DemoDataset.Load(settings.DatasetFile);
            }
            catch (InvalidDataException ex)
            {
                stderr.WriteLine("m365 demo: " + ex.Message);
                stderr.Flush();
                return DemoExitCodes.Usage;
            }
        }

        var colors = overrides.Out is null && !Console.IsOutputRedirected && string.IsNullOrEmpty(env("NO_COLOR"));
        using var console = new DemoConsole(stdout, stderr, settings.Verbosity, colors);

        // ── no config: say exactly what is missing and how to create it; nothing is created, nothing is sent ──
        settings.ConfigFile.Refresh();
        if (!settings.ConfigFile.Exists)
        {
            WriteMissingConfig(console, settings);
            return DemoExitCodes.NotMeasured;
        }

        // ── the run folder ──
        RunFolder folder;
        try
        {
            folder = RunFolder.Create(settings.OutDirectory, overrides.RunsRoot, now());
            console.OpenLog(folder.ConsoleLog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            console.Error($"m365 demo: the run folder could not be created ({ex.GetType().Name}: {ex.Message}). Nothing was sent.");
            return DemoExitCodes.Runtime;
        }

        var run = new DemoRun(settings, overrides, console, folder, caseSet, env, now);
        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The "no config file" message: what is missing, where to get it, how to point at an existing one.</summary>
    /// <param name="console">The console.</param>
    /// <param name="settings">The settings.</param>
    internal static void WriteMissingConfig(DemoConsole console, DemoSettings settings)
    {
        var cwd = Directory.GetCurrentDirectory();
        var template = new FileInfo(Path.Combine(cwd, DemoCommandLine.TemplateRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var example = new FileInfo(Path.Combine(cwd, "samples", "AgentEval.Microsoft365Demo", "workiq.example.json"));
        if (!example.Exists)
        {
            example = new FileInfo(Path.Combine(AppContext.BaseDirectory, "workiq.example.json"));
        }

        var text = new StringBuilder(2048);
        text.Append("Microsoft 365 demo: no config file at ").Append(settings.ConfigFile.FullName).Append(" (").Append(settings.ConfigSource).Append(").\n");
        text.Append("Nothing was sent to any tenant and no run folder was created.\n\n");
        text.Append("What is missing: the Work IQ connection file (JSON, no secret in it). It needs two values from your Entra app registration:\n");
        text.Append("  tenantId     the Directory (tenant) ID (the app registration's Overview page)\n");
        text.Append("  appClientId  the Application (client) ID of a public-client app registration (\"Allow public client flows\" on) with the\n");
        text.Append("               delegated permission WorkIQAgent.Ask on Work IQ, admin consent granted\n");
        text.Append("Optional: agentId (null = the default Work IQ agent), agentName, timeZone; leave endpoint, scope and trustedHosts as they are.\n\n");
        text.Append("Create it from the template, then replace both REPLACE-WITH-... values:\n");
        text.Append("  cp ").Append(DemoCommandLine.TemplateRelativePath).Append(" ./").Append(DemoCommandLine.DefaultConfigFileName).Append("               (bash, from the repository root)\n");
        text.Append("  Copy-Item ").Append(DemoCommandLine.TemplateRelativePath).Append(" ./").Append(DemoCommandLine.DefaultConfigFileName).Append("        (PowerShell)\n");
        text.Append("  template: ").Append(template.Exists ? template.FullName : DemoCommandLine.TemplateRelativePath + " (in the repository)").Append('\n');
        if (example.Exists)
        {
            text.Append("  the same template next to this demo: ").Append(example.FullName).Append('\n');
        }

        text.Append("Or point at an existing file: --config <file>, or set ").Append(DemoCommandLine.ConfigPathVariable).Append("=<file>.\n\n");
        text.Append("Then check the connection without sending a prompt:\n");
        text.Append("  ").Append(DemoCommandLine.RunCommand).Append(" --readiness-only\n\n");
        text.Append("Exit 11: nothing failed, nothing was measured.");
        console.Line(DemoVerbosity.Quiet, text.ToString(), Tint.Warn);
    }
}

/// <summary>One run of the demo after the command line and the run folder are settled.</summary>
internal sealed class DemoRun
{
    private readonly DemoSettings _settings;
    private readonly DemoHostOverrides _overrides;
    private readonly DemoConsole _console;
    private readonly RunFolder _folder;
    private readonly Func<string, string?> _env;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<StageRow> _stages = [];
    private readonly DemoTraceSink _traceSink;
    private readonly DemoIssueSink _issueSink;
    private readonly TurnRecorder _turns;
    private DemoCaseSet? _caseSet;
    private Microsoft365Config? _config;
    private Microsoft365Config? _overriddenConfig;
    private string? _configError;
    private ReadinessReport? _readiness;
    private WorkIqTurnResult? _promptTurn;
    private BenchmarkOutcome? _benchmark;
    private JudgeChoice _judge = new(null, "not used (the benchmark does not run in this invocation)", "the benchmark did not run", Owned: false);

    /// <summary>Creates the run.</summary>
    public DemoRun(
        DemoSettings settings,
        DemoHostOverrides overrides,
        DemoConsole console,
        RunFolder folder,
        DemoCaseSet? caseSet,
        Func<string, string?> env,
        Func<DateTimeOffset> now)
    {
        _settings = settings;
        _overrides = overrides;
        _console = console;
        _folder = folder;
        _caseSet = caseSet;
        _env = env;
        _now = now;
        _traceSink = new DemoTraceSink(console) { OnStage = PrintReadinessStage };
        _issueSink = new DemoIssueSink(console);
        _turns = new TurnRecorder(folder, console, settings.ContentPolicy);
    }

    private bool BenchmarkPlanned => _settings.Consent && !_settings.ReadinessOnly && !_settings.NoBenchmark;

    /// <summary>Runs every stage, writes the reports, prints the final table, zips when asked, and returns the exit code.</summary>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var startedAt = _now();
        string? runtimeError = null;
        try
        {
            LoadConfig();
            if (BenchmarkPlanned)
            {
                _caseSet ??= DemoDataset.LoadDefault();
                _judge = JudgeResolver.Resolve(_settings.NoJudge, _overrides.JudgeClient, _env);
            }

            Banner();
            await RunReadinessAsync(cancellationToken).ConfigureAwait(false);
            await RunLiveStagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runtimeError = $"{ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}";
            _console.Error("m365 demo: runtime error: " + runtimeError + ". The run folder is still written.");
            _console.LogOnly(WorkIqRedaction.Redact(ex.ToString()));
            AddMissingStageRows("not run: the run stopped on a runtime error");
        }
        finally
        {
            _judge.Dispose();
        }

        var (exit, meaning) = ExitCode(runtimeError);
        var report = BuildReport(startedAt, exit, meaning, runtimeError);
        var errorsBefore = _folder.WriteErrors.Count;
        Summary.WriteAll(report);
        if (_folder.WriteErrors.Count > errorsBefore || _console.LogError is not null)
        {
            // A report file could not be written: the run is not fully saved. Rewrite the summary with the true exit.
            (exit, meaning) = ExitCode(runtimeError);
            report = BuildReport(startedAt, exit, meaning, runtimeError);
            _folder.TryWriteText(_folder.Summary, Summary.ToSummaryMarkdown(report));
        }

        _console.Line(DemoVerbosity.Quiet, Summary.FinalTable(report), Summary.ResultWord(exit) switch
        {
            "PASS" => Tint.Good,
            "NOT MEASURED" => Tint.Warn,
            _ => Tint.Bad,
        });

        if (_settings.Zip)
        {
            _console.Line(DemoVerbosity.Quiet, $"Zip: {_folder.ZipFile.FullName} (written after this log is closed; send it back after reading {RunFolder.SummaryName})");
        }

        _console.Line(DemoVerbosity.Quiet, Summary.NoTokenStatement);
        _console.CloseLog();
        if (_console.LogError is not null && exit != DemoExitCodes.Runtime)
        {
            _console.Error("m365 demo: " + _console.LogError);
            exit = DemoExitCodes.Runtime;
        }

        if (_settings.Zip)
        {
            try
            {
                var zip = _folder.WriteZip();
                _console.Line(DemoVerbosity.Quiet, $"Zip written: {zip.FullName} ({zip.Length.ToString(CultureInfo.InvariantCulture)} bytes).", Tint.Accent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _console.Error($"m365 demo: the zip could not be written ({ex.GetType().Name}: {ex.Message}); zip the run folder yourself.");
                exit = DemoExitCodes.Runtime;
            }
        }

        return exit;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Config and banner
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        try
        {
            var config = Microsoft365Config.Load(_settings.ConfigFile);
            if (_settings.AgentId is not null)
            {
                // Kept unvalidated for the readiness check: an override that breaks the config fails its config stage.
                _overriddenConfig = config with { AgentId = _settings.AgentId };
                _overriddenConfig.Validate();
                config = _overriddenConfig;
            }

            _config = config;
        }
        catch (InvalidOperationException ex)
        {
            // The readiness check's config stage reports it in full; the banner says it too.
            _config = null;
            _configError = ex.Message;
        }
    }

    private void Banner()
    {
        var s = _settings;
        var c = _config;
        var b = new StringBuilder(2048);
        b.Append("Microsoft 365 agent demo — AgentEval ").Append(Summary.DemoVersion).Append(", provider ").Append(WorkIqDiagnostics.AdapterVersion ?? "unknown")
            .Append(", A2A ").Append(WorkIqDiagnostics.A2AVersion).Append('\n');
        Field(b, "run", $"{_folder.RunId} → {_folder.Root.FullName}");
        Field(b, "config", $"{s.ConfigFile.FullName} ({s.ConfigSource})");
        if (c is null)
        {
            Field(b, "agent", "unknown: the config did not load — " + IssueText.Flatten(_configError));
            Field(b, "identity", "unknown: the config did not load");
        }
        else
        {
            Field(b, "agent", (string.IsNullOrWhiteSpace(c.AgentId) ? "the default Work IQ agent (Microsoft 365 Copilot)" : c.AgentId!.Trim() + (s.AgentId is null ? " (from the config)" : " (from --agent-id)")) +
                $", shown as \"{c.DisplayName}\"");
            Field(b, "identity", $"tenant {c.TenantId.Trim()}, app {c.AppClientId.Trim()}; you sign in as yourself (device code); the account is shown after sign-in");
            Field(b, "endpoint", $"{c.Endpoint.Trim()} (a token is only ever sent to: {string.Join(", ", c.TrustedHosts)})");
        }

        Field(b, "profile", $"{s.ProfileName} ({s.Profile}){(s.Profile == DiagnosticProfile.Native118Diagnostics ? " — EXPERIMENTAL" : "")}; content policy {s.ContentPolicy}");
        Field(b, "plan", "1. readiness: config, sign-in, agent card, protocol handshake (no prompt is sent)");
        if (s.ReadinessOnly)
        {
            Field(b, "", "stops after readiness (--readiness-only)");
        }
        else if (!s.Consent)
        {
            Field(b, "", $"2. prompt and 3. benchmark: NOT RUN — they need {DemoCommandLine.ConsentFlag} (or {DemoCommandLine.ConsentVariable}=true)");
        }
        else
        {
            Field(b, "", $"2. one prompt: \"{IssueText.Truncate(IssueText.Flatten(s.Prompt), 100)}\"");
            Field(b, "", s.NoBenchmark
                ? "3. benchmark: not run (--no-benchmark)"
                : $"3. benchmark: {(_caseSet?.Cases.Count ?? 0).ToString(CultureInfo.InvariantCulture)} cases ({_caseSet?.Source}) through MAFEvaluationHarness");
            if (!s.NoBenchmark)
            {
                Field(b, "judge", _judge.Description);
            }
        }

        var planned = s.Consent && !s.ReadinessOnly ? 1 + (BenchmarkPlanned ? _caseSet?.Cases.Count ?? 0 : 0) : 0;
        Field(b, "limits", $"turn cap {s.MaxTurns.ToString(CultureInfo.InvariantCulture)} (this run plans {planned.ToString(CultureInfo.InvariantCulture)} turn(s)); {s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s per stage or turn");
        if (planned > s.MaxTurns)
        {
            Field(b, "", $"the turn cap stops the run after {s.MaxTurns.ToString(CultureInfo.InvariantCulture)} turn(s): {(planned - s.MaxTurns).ToString(CultureInfo.InvariantCulture)} case(s) will be NOT MEASURED");
        }

        Field(b, "screen", $"{s.Verbosity.ToString().ToLowerInvariant()}; console.log in the run folder keeps the full view");
        _console.Line(DemoVerbosity.Normal, b.ToString().TrimEnd('\n'), Tint.Accent);
        if (s.Consent && !s.ReadinessOnly)
        {
            _console.Line(DemoVerbosity.Normal,
                "LIVE: the prompt and the benchmark run as the signed-in user. The delegated WorkIQAgent.Ask permission covers reads AND writes " +
                "of that user's Microsoft 365 data, and a prompt can trigger real actions. Consent: " + s.ConsentSource + ". Use a non-production tenant.",
                Tint.Warn);
        }

        if (s.CaptureRaw)
        {
            _console.Line(DemoVerbosity.Normal, "raw/ will hold the raw A2A traffic (tenant content): read it before you send it.", Tint.Warn);
        }

        _console.Blank();

        static void Field(StringBuilder b, string name, string value) => b.Append("  ").Append(name.PadRight(10)).Append(value).Append('\n');
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Readiness
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task RunReadinessAsync(CancellationToken cancellationToken)
    {
        _console.Line(DemoVerbosity.Normal, "Readiness (no prompt is sent)", Tint.Accent);
        using var signIn = new SignInPromptWriter(_console);
        var options = new ReadinessCheckOptions
        {
            Stages = null,
            Probe = false,
            IUnderstandLiveSideEffects = _settings.Consent,
            ContinueAfterFailure = false,
            StageTimeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds),
            Profile = _settings.Profile,
            RawCaptureDirectory = _settings.CaptureRaw ? _folder.RawFolder : null,
            Verbose = false,
            TraceFile = _folder.Trace,
            DiagnosticsDirectory = _folder.Root,
            Console = signIn,
            TraceSinks = [_traceSink],
            IssueSinks = [_issueSink],
        };

        // The file keeps the config's path and hash in the report; an --agent-id override is passed as the loaded config
        // (unvalidated, so an override that breaks it fails the config stage; a file that does not load is passed as the file).
        var inCode = _settings.AgentId is not null ? _overriddenConfig : null;
        var report = await Microsoft365ReadinessCheck.RunAsync(
            inCode,
            inCode is null ? _settings.ConfigFile : null,
            options,
            Wrap(_overrides.InnerHandler),
            _overrides.TokenProvider,
            cancellationToken).ConfigureAwait(false);
        signIn.Flush();

        // One issue register for the run: the check wrote m365-issues.jsonl into the run folder; the live agent appends to issues.jsonl.
        var adoptError = _folder.AdoptReadinessIssues(report.IssueFile);
        report = report with
        {
            ReportDirectory = _folder.Root.FullName,
            IssueFile = adoptError is null ? _folder.Issues.FullName : report.IssueFile,
        };
        _readiness = report;
        _folder.TryWriteText(_folder.ReadinessMarkdown, ReadinessReportRenderer.ToMarkdown(report));
        _folder.TryWriteText(_folder.ReadinessJson, ReadinessReportRenderer.ToJson(report));

        foreach (var stage in report.Stages.Where(st => st.Requested))
        {
            var name = DemoHost.ReadinessPrefix + stage.Stage.ToString().ToLowerInvariant();
            var result = stage.Result switch
            {
                ReadinessResult.Pass => StageResult.Pass,
                ReadinessResult.Fail => StageResult.Fail,
                _ when report.AnyFailed => StageResult.Skipped,
                _ => StageResult.NotMeasured,
            };
            var detail = stage.Result == ReadinessResult.Skipped ? stage.SkipReason ?? stage.Summary : stage.Summary;
            if (stage.Result == ReadinessResult.Fail && !string.IsNullOrWhiteSpace(stage.Action))
            {
                detail += " — do: " + stage.Action;
            }

            _stages.Add(new StageRow(name, result, detail));
            PrintStageDetail(stage);
        }

        _console.Line(DemoVerbosity.Normal, $"  readiness {report.Verdict}: {_folder.Relative(_folder.ReadinessMarkdown.FullName)}, {_folder.Relative(_folder.ReadinessJson.FullName)}",
            report.AnyFailed ? Tint.Bad : report.AllPassed ? Tint.Good : Tint.Warn);
        _console.Blank();
    }

    /// <summary>Prints a readiness stage line as its verdict event arrives (the trace sink calls it).</summary>
    private void PrintReadinessStage(WorkIqTraceEvent e, string stage, string verdict, bool requested)
    {
        if (!requested)
        {
            _console.Line(DemoVerbosity.Detailed, $"  – {(DemoHost.ReadinessPrefix + stage).PadRight(22)} not requested", Tint.Dim);
            return;
        }

        var (symbol, tint) = verdict switch
        {
            "pass" => ("✓", Tint.Good),
            "fail" => ("✗", Tint.Bad),
            _ => ("–", Tint.Warn),
        };
        var message = e.Message;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        var summary = colon >= 0 ? message[(colon + 2)..] : message;
        var elapsed = e.ElapsedMs is { } ms && _console.Shows(DemoVerbosity.Detailed) ? $"  ({ms.ToString("0", CultureInfo.InvariantCulture)} ms)" : "";
        _console.Line(verdict == "fail" ? DemoVerbosity.Quiet : DemoVerbosity.Normal, $"  {symbol} {(DemoHost.ReadinessPrefix + stage).PadRight(22)} {summary}{elapsed}", tint);
    }

    private void PrintStageDetail(ReadinessStageResult stage)
    {
        if (stage.Result == ReadinessResult.Fail)
        {
            var block = new StringBuilder(512);
            block.Append("    ").Append(DemoHost.ReadinessPrefix).Append(stage.Stage.ToString().ToLowerInvariant()).Append(" FAILED:\n");
            if (stage.Issue is { } issue)
            {
                Field(block, "category", $"{issue.Category} — {IssueText.WhoFixes(issue.Category)}");
                Field(block, "code", issue.Code);
                var codes = IssueText.Codes(issue);
                Field(block, "codes", codes.Length == 0 ? "none" : codes);
                Field(block, "request-id", issue.RequestId ?? "not received");
                if (issue.TaskId is not null || issue.ContextId is not null)
                {
                    Field(block, "task/context", $"{issue.TaskId ?? "(none)"} / {issue.ContextId ?? "(none)"}");
                }

                Field(block, "message", issue.Message);
            }

            Field(block, "meaning", stage.Meaning);
            Field(block, "action", stage.Action);
            _console.Line(DemoVerbosity.Quiet, block.ToString().TrimEnd('\n'), Tint.Bad);
        }

        foreach (var warning in stage.Warnings)
        {
            _console.Line(DemoVerbosity.Normal, "      warning      " + IssueText.Flatten(warning), Tint.Warn);
        }

        if (stage.Evidence.Count > 0)
        {
            _console.Line(DemoVerbosity.Detailed, $"    {DemoHost.ReadinessPrefix}{stage.Stage.ToString().ToLowerInvariant()} evidence:", Tint.Dim);
        }

        foreach (var entry in stage.Evidence)
        {
            _console.Line(DemoVerbosity.Detailed, $"      {entry.Key}: {IssueText.Flatten(entry.Value)}", Tint.Dim);
        }

        static void Field(StringBuilder block, string name, string value) =>
            block.Append("      ").Append(name.PadRight(13)).Append(IssueText.Flatten(value)).Append('\n');
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // The prompt and the benchmark
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task RunLiveStagesAsync(CancellationToken cancellationToken)
    {
        var readiness = _readiness!;
        if (_settings.ReadinessOnly)
        {
            _stages.Add(new StageRow(DemoHost.PromptStage, StageResult.Skipped, "not requested (--readiness-only)"));
            _stages.Add(new StageRow(DemoHost.BenchmarkStage, StageResult.Skipped, "not requested (--readiness-only)"));
            return;
        }

        if (readiness.AnyFailed || !readiness.AllPassed)
        {
            var why = readiness.AnyFailed
                ? "not run: readiness failed (fix it first; the table above says what to do)"
                : "not run: readiness did not complete";
            var result = readiness.AnyFailed ? StageResult.Skipped : StageResult.NotMeasured;
            _stages.Add(new StageRow(DemoHost.PromptStage, result, why));
            _stages.Add(new StageRow(DemoHost.BenchmarkStage, _settings.NoBenchmark ? StageResult.Skipped : result, _settings.NoBenchmark ? "not requested (--no-benchmark)" : why));
            return;
        }

        if (!_settings.Consent)
        {
            var why = $"not run: needs {DemoCommandLine.ConsentFlag} (or {DemoCommandLine.ConsentVariable}=true) — it runs as the signed-in user";
            _stages.Add(new StageRow(DemoHost.PromptStage, StageResult.NotMeasured, why));
            _stages.Add(new StageRow(DemoHost.BenchmarkStage, _settings.NoBenchmark ? StageResult.Skipped : StageResult.NotMeasured, _settings.NoBenchmark ? "not requested (--no-benchmark)" : why));
            _console.Line(DemoVerbosity.Normal, $"Readiness passed. The prompt and the benchmark need {DemoCommandLine.ConsentFlag}: nothing was sent to the agent.", Tint.Warn);
            _console.Blank();
            return;
        }

        var options = new WorkIqOptions
        {
            SendTimeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds),
            TurnDeadline = TimeSpan.FromSeconds(_settings.TimeoutSeconds),
            RecoveryBudget = TimeSpan.FromSeconds(Math.Min(120, _settings.TimeoutSeconds)),
            ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,
            MaxTurns = _settings.MaxTurns,
            ContentPolicy = _settings.ContentPolicy,
            RawCaptureDirectory = _settings.CaptureRaw ? _folder.RawFolder : null,
            ProfileOverride = _settings.Profile,
            Verbose = false,
            TraceFile = _folder.Trace,
            IssueFile = _folder.Issues,
            RunId = _folder.RunId,
            TraceSinks = [_traceSink],
            IssueSinks = [_issueSink],
        };

        var config = _config ?? throw new InvalidOperationException("The config passed readiness but did not load in the demo.");
        await using var agent = Microsoft365AgentFactory.BuildLive(
            config,
            iUnderstandLiveSideEffects: true,
            options,
            _overrides.TokenProvider,
            _turns.OnTurn,
            Wrap(_overrides.InnerHandler));

        var promptResult = await RunPromptAsync(agent, cancellationToken).ConfigureAwait(false);
        if (_settings.NoBenchmark)
        {
            _stages.Add(new StageRow(DemoHost.BenchmarkStage, StageResult.Skipped, "not requested (--no-benchmark)"));
            return;
        }

        if (promptResult == StageResult.Fail)
        {
            _stages.Add(new StageRow(DemoHost.BenchmarkStage, StageResult.Skipped, "not run: the prompt failed (the benchmark would send its turns into the same failure)"));
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _stages.Add(new StageRow(DemoHost.BenchmarkStage, StageResult.NotMeasured, "not run: the run was canceled"));
            return;
        }

        await RunBenchmarkAsync(agent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StageResult> RunPromptAsync(WorkIqEvaluableAgent agent, CancellationToken cancellationToken)
    {
        _console.Line(DemoVerbosity.Normal, "Prompt (one turn, fresh context)", Tint.Accent);
        _console.Line(DemoVerbosity.Normal, "  > " + IssueText.Flatten(_settings.Prompt));
        var since = _turns.Count;
        agent.NextCaseId = DemoDataset.PromptCaseId;
        StageRow row;
        try
        {
            var response = await agent.InvokeAsync(_settings.Prompt, cancellationToken).ConfigureAwait(false);
            var turn = _turns.LatestFor(DemoDataset.PromptCaseId, since);
            _promptTurn = turn;
            var elapsed = turn is null ? "" : $" in {TurnRecorder.Seconds(turn.Timing.Elapsed)}";
            var completeness = turn?.AnswerCompleteness;
            if (completeness == OutputCompleteness.Ambiguous)
            {
                row = new StageRow(DemoHost.PromptStage, StageResult.NotMeasured, $"Completed{elapsed} with several answer candidates and none selected (the evidence keeps them all)");
            }
            else if (string.IsNullOrWhiteSpace(response.Text))
            {
                row = new StageRow(DemoHost.PromptStage, StageResult.Fail, $"Completed{elapsed} with no answer text ({completeness?.ToString() ?? "no turn result"})");
            }
            else
            {
                row = new StageRow(DemoHost.PromptStage, StageResult.Pass, $"Completed{elapsed}: {response.Text.Length.ToString(CultureInfo.InvariantCulture)}-character answer");
                ShowAnswer(response.Text, turn);
            }
        }
        catch (WorkIqTurnCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _promptTurn = ex.TurnResult;
            row = new StageRow(DemoHost.PromptStage, StageResult.NotMeasured, $"canceled: the turn ended {ex.TurnResult.Outcome}; the remote task was not canceled");
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            _promptTurn = ex.TurnResult;
            row = new StageRow(DemoHost.PromptStage, StageResult.Fail, "the turn did not complete: " + TurnRecorder.NotCompletedSummary(ex.TurnResult));
        }
        catch (WorkIqProtocolException ex)
        {
            row = new StageRow(DemoHost.PromptStage, StageResult.Fail, $"the agent failed before a turn result: {TurnRecorder.FailureCodes(ex.Failure)}: {IssueText.Flatten(ex.Failure.SafeMessage)}");
        }
        catch (WorkIqTurnBudgetExceededException ex)
        {
            row = new StageRow(DemoHost.PromptStage, StageResult.NotMeasured, "not sent: " + IssueText.Flatten(ex.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            row = new StageRow(DemoHost.PromptStage, StageResult.NotMeasured, "canceled before the turn returned");
        }
        finally
        {
            agent.NextCaseId = null;
        }

        _stages.Add(row);
        _console.Line(row.Result == StageResult.Fail ? DemoVerbosity.Quiet : DemoVerbosity.Normal,
            $"  {Summary.Symbol(row.Result)} {DemoHost.PromptStage.PadRight(22)} {row.Detail}",
            row.Result switch { StageResult.Pass => Tint.Good, StageResult.Fail => Tint.Bad, _ => Tint.Warn });
        if (_promptTurn is { } recorded && _turns.EvidenceFileOf(recorded) is { } file)
        {
            _console.Line(DemoVerbosity.Normal, "    evidence: " + file + " — " + TurnRecorder.EvidenceAvailability(recorded.Evidence), Tint.Dim);
        }

        _console.Blank();
        return row.Result;
    }

    private void ShowAnswer(string raw, WorkIqTurnResult? turn)
    {
        var display = turn?.Evidence.Answer?.DisplayText is { IsPresent: true, Value: { } shown } ? shown : raw;
        var full = _console.Shows(DemoVerbosity.Detailed);
        var text = full ? display : IssueText.Truncate(display, 600);
        var answer = new StringBuilder(text.Length + 64).Append("  Answer").Append(full || display.Length <= 600 ? "" : " (first 600 characters; --verbosity detailed shows all)").Append(":\n");
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            answer.Append("    ").Append(line).Append('\n');
        }

        _console.Line(DemoVerbosity.Normal, answer.ToString().TrimEnd('\n'));
    }

    private async Task RunBenchmarkAsync(WorkIqEvaluableAgent agent, CancellationToken cancellationToken)
    {
        var cases = _caseSet!.Cases;
        _console.Line(DemoVerbosity.Normal, $"Benchmark ({cases.Count.ToString(CultureInfo.InvariantCulture)} cases, {_caseSet.Source}; judge: {_judge.Description})", Tint.Accent);
        _benchmark = await Benchmark.RunAsync(agent, cases, _judge, _turns, _console, cancellationToken).ConfigureAwait(false);
        var outcomes = _benchmark.Cases;
        var pass = outcomes.Count(o => o.Verdict == CaseVerdict.Pass);
        var fail = outcomes.Count(o => o.Verdict == CaseVerdict.Fail);
        var error = outcomes.Count(o => o.Verdict == CaseVerdict.Error);
        var notMeasured = outcomes.Count(o => o.Verdict == CaseVerdict.NotMeasured);
        var detail = $"{outcomes.Count.ToString(CultureInfo.InvariantCulture)} cases: {pass.ToString(CultureInfo.InvariantCulture)} PASS, {fail.ToString(CultureInfo.InvariantCulture)} FAIL, " +
                     $"{error.ToString(CultureInfo.InvariantCulture)} ERROR, {notMeasured.ToString(CultureInfo.InvariantCulture)} NOT MEASURED";
        if (!_judge.IsAvailable && cases.Any(c => c.Criteria.Count > 0))
        {
            detail += "; criteria not graded: " + _judge.NotMeasuredReason;
        }

        if (_benchmark.StopReason is not null)
        {
            detail += "; stopped early: " + _benchmark.StopReason;
        }

        var result = fail > 0 ? StageResult.Fail : error + notMeasured > 0 ? StageResult.NotMeasured : StageResult.Pass;
        _stages.Add(new StageRow(DemoHost.BenchmarkStage, result, detail));
        _console.Line(DemoVerbosity.Normal, $"  {Summary.Symbol(result)} {DemoHost.BenchmarkStage.PadRight(22)} {detail}",
            result switch { StageResult.Pass => Tint.Good, StageResult.Fail => Tint.Bad, _ => Tint.Warn });
        _console.Blank();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Exit code and report
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>After a runtime error: a row for every stage the run did not reach, so the table never ends silently.</summary>
    private void AddMissingStageRows(string why)
    {
        if (!_stages.Any(r => r.Name.StartsWith(DemoHost.ReadinessPrefix, StringComparison.Ordinal)))
        {
            _stages.Add(new StageRow("readiness", StageResult.NotMeasured, why));
        }

        foreach (var name in new[] { DemoHost.PromptStage, DemoHost.BenchmarkStage })
        {
            if (!_stages.Any(r => r.Name == name))
            {
                _stages.Add(new StageRow(name, StageResult.NotMeasured, why));
            }
        }
    }

    private (int Code, string Meaning) ExitCode(string? runtimeError)
    {
        var writeErrors = _folder.WriteErrors;
        if (runtimeError is not null)
        {
            return (DemoExitCodes.Runtime, "the run stopped on a runtime error: " + runtimeError);
        }

        if (writeErrors.Count > 0 || _console.LogError is not null)
        {
            return (DemoExitCodes.Runtime, "a file of the run folder could not be written: " + (_console.LogError ?? writeErrors[0]));
        }

        var cases = _benchmark?.Cases ?? [];
        var failed = _stages.Where(r => r.Result == StageResult.Fail).Select(r => r.Name)
            .Concat(cases.Where(c => c.Verdict == CaseVerdict.Fail).Select(c => "case " + c.Case.Id)).ToArray();
        if (failed.Length > 0)
        {
            return (DemoExitCodes.Failure, "failed: " + string.Join(", ", failed));
        }

        var notMeasured = new List<string>();
        foreach (var row in _stages.Where(r => r.Result == StageResult.NotMeasured && r.Name != DemoHost.BenchmarkStage))
        {
            notMeasured.Add($"{row.Name} ({IssueText.Truncate(IssueText.Flatten(row.Detail), 160)})");
        }

        var errorCases = cases.Where(c => c.Verdict == CaseVerdict.Error).Select(c => c.Case.Id).ToArray();
        if (errorCases.Length > 0)
        {
            notMeasured.Add($"{errorCases.Length.ToString(CultureInfo.InvariantCulture)} case(s) ERROR, the turn did not complete ({string.Join(", ", errorCases)})");
        }

        var notMeasuredCases = cases.Where(c => c.Verdict == CaseVerdict.NotMeasured).Select(c => c.Case.Id).ToArray();
        if (notMeasuredCases.Length > 0)
        {
            var why = _benchmark?.StopReason is { } stop ? "; stopped early: " + stop : "";
            if (!_judge.IsAvailable && cases.Any(c => c.Case.Criteria.Count > 0))
            {
                why += "; criteria: " + _judge.NotMeasuredReason;
            }

            notMeasured.Add($"{notMeasuredCases.Length.ToString(CultureInfo.InvariantCulture)} case(s) NOT MEASURED ({string.Join(", ", notMeasuredCases)}{why})");
        }

        if (notMeasured.Count == 0 && _stages.Any(r => r.Result == StageResult.NotMeasured))
        {
            notMeasured.Add("benchmark (" + IssueText.Truncate(IssueText.Flatten(_stages.First(r => r.Result == StageResult.NotMeasured).Detail), 160) + ")");
        }

        if (notMeasured.Count > 0)
        {
            return (DemoExitCodes.NotMeasured, "nothing failed, but not measured: " + string.Join("; ", notMeasured));
        }

        return (DemoExitCodes.Success, _settings.ReadinessOnly
            ? "readiness passed (--readiness-only)"
            : "every stage that ran passed and everything requested was measured");
    }

    private DemoReport BuildReport(DateTimeOffset startedAt, int exit, string meaning, string? runtimeError) => new()
    {
        Settings = _settings,
        Folder = _folder,
        StartedAt = startedAt,
        EndedAt = _now(),
        Config = _config,
        Readiness = _readiness,
        Stages = _stages.ToArray(),
        PromptTurn = _promptTurn,
        Benchmark = _benchmark,
        CaseSet = _caseSet,
        JudgeDescription = _judge.Description,
        Issues = _issueSink.Issues,
        WriteErrors = _console.LogError is null ? _folder.WriteErrors : [.. _folder.WriteErrors, _console.LogError],
        RuntimeError = runtimeError,
        ExitCode = exit,
        ExitMeaning = meaning,
        EvidenceFileOf = _turns.EvidenceFileOf,
    };

    /// <summary>Wraps a host's handler so no connection's disposal disposes it (readiness and the live agent each own a chain).</summary>
    private static HttpMessageHandler? Wrap(HttpMessageHandler? inner) => inner is null ? null : new HostOwnedHandler(inner);

    /// <summary>A pass-through handler that leaves its inner handler alive when the connection's chain is disposed.</summary>
    private sealed class HostOwnedHandler : DelegatingHandler
    {
        public HostOwnedHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        protected override void Dispose(bool disposing)
        {
            // The host owns the inner handler: release nothing of it (DelegatingHandler disposes its inner only when disposing).
            base.Dispose(disposing: false);
        }
    }
}
