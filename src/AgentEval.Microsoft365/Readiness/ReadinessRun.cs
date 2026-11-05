// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>
/// One run of the readiness check behind <see cref="Microsoft365ReadinessCheck"/>: loads the config, opens the
/// run's diagnostics (JSONL trace, issue register behind <see cref="ExpectedIssueFilter"/>, the verbose console, an
/// in-memory list the stages read their own events from, and the caller's <see cref="ReadinessCheckOptions.TraceSinks"/>
/// and <see cref="ReadinessCheckOptions.IssueSinks"/>), runs the stages in order, traces each one on the
/// <see cref="WorkIqTraceStage.Doctor"/> stage (<c>doctor.&lt;stage&gt;.start</c>, then <c>.pass</c>, <c>.fail</c> or
/// <c>.skipped</c> with the evidence as data) and registers every failed stage as a <c>doctor.&lt;stage&gt;.failed</c>
/// issue. The stage implementations live in the <c>Stages</c> and <c>Turns</c> partials. Single-use.
/// </summary>
internal sealed partial class ReadinessRun : IAsyncDisposable
{
    /// <summary>The case id of the probe and census turns.</summary>
    internal const string ProbeCaseId = "doctor-probe";

    /// <summary>The turn id of the probe turn.</summary>
    internal const string ProbeTurnId = "doctor-probe-1";

    /// <summary>The turn id of the census's own native118 turn.</summary>
    internal const string CensusTurnId = "doctor-census-1";

    /// <summary>The evidence name of the probe prompt (kept out of the trace: the trace never carries prompts).</summary>
    internal const string ProbePromptKey = "prompt";

    /// <summary>The evidence name of the answer preview (kept out of the trace: the trace never carries answers).</summary>
    internal const string AnswerPreviewKey = "answer (first 80 characters)";

    /// <summary>How many characters of the probe's answer the report shows.</summary>
    internal const int AnswerPreviewLength = 80;

    /// <summary>How long a turn stage may outlive its turn deadline before the stage itself stops waiting.</summary>
    private static readonly TimeSpan TurnGrace = TimeSpan.FromSeconds(30);

    /// <summary>How long past the sign-in code's expiry a stage still waits (MSAL itself ends the wait with <c>code_expired</c>).</summary>
    private static readonly TimeSpan SignInGrace = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlySet<string> TraceExcludedKeys = new HashSet<string>(StringComparer.Ordinal) { ProbePromptKey, AnswerPreviewKey };

    private readonly Microsoft365Config? _configInput;
    private readonly FileInfo? _configFile;
    private readonly ReadinessCheckOptions _options;
    private readonly HttpMessageHandler? _innerHandler;
    private readonly IWorkIqTokenProvider? _tokenProviderOverride;
    private readonly TimeProvider _clock;
    private readonly TextWriter _console;
    private readonly IReadOnlySet<ReadinessStage> _requested;
    private readonly string _runId;
    private readonly WorkIqDiagnosticsPaths _paths;
    private readonly ListTraceSink _trace = new();
    private readonly List<WorkIqConversation> _conversations = [];

    private WorkIqDiagnostics _diagnostics = WorkIqDiagnostics.None;
    private ExpectedIssueFilter? _issueFilter;
    private Microsoft365Config? _config;
    private WorkIqOptions? _workIqOptions;
    private IWorkIqTokenProvider? _tokenProvider;
    private HttpClient? _http;
    private WorkIqA2AClient? _client;
    private ProbeTurn? _probe;
    private CancellationTokenSource? _activeStage;
    private DateTimeOffset _activeStageDeadline;
    private DateTimeOffset? _signInCodeExpiresOn;
    private int _disposed;

    /// <summary>Creates the run; nothing is read, opened or sent until <see cref="ExecuteAsync"/>.</summary>
    /// <param name="config">The config, when given in code (exactly one of this and <paramref name="configFile"/>).</param>
    /// <param name="configFile">The config file, when given by path.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="innerHandler">The innermost HTTP handler (tests); null uses a socket handler.</param>
    /// <param name="tokenProvider">The token source (tests or a host); null uses the MSAL device-code provider.</param>
    /// <param name="clock">The clock.</param>
    public ReadinessRun(
        Microsoft365Config? config,
        FileInfo? configFile,
        ReadinessCheckOptions options,
        HttpMessageHandler? innerHandler,
        IWorkIqTokenProvider? tokenProvider,
        TimeProvider clock)
    {
        _configInput = config;
        _configFile = configFile;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _innerHandler = innerHandler;
        _tokenProviderOverride = tokenProvider;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _console = options.Console ?? Console.Error;
        _requested = options.ResolveStages();
        _runId = WorkIqDiagnosticsFactory.NewRunId(clock);
        _paths = WorkIqDiagnosticsFactory.Describe(
            new WorkIqOptions { TraceFile = options.TraceFile, DiagnosticsDirectory = options.DiagnosticsDirectory, RunId = _runId },
            _runId);
    }

    /// <summary>The probe turn, kept for the census.</summary>
    /// <param name="Result">The turn result.</param>
    /// <param name="Conversation">The conversation that ran it (still open, for <c>CancelTask</c>).</param>
    /// <param name="Profile">The profile the turn ran under.</param>
    private sealed record ProbeTurn(WorkIqTurnResult Result, WorkIqConversation Conversation, DiagnosticProfile Profile);

    /// <summary>One evidence row of a stage trace event.</summary>
    private sealed record EvidenceEntry(string Name, string Value);

    /// <summary>The data of a <c>doctor.&lt;stage&gt;.pass|fail|skipped</c> event.</summary>
    private sealed record StageTraceData(
        string Result,
        bool Requested,
        string? SkipReason,
        bool SkippedForConsent,
        string? Meaning,
        string? Action,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<EvidenceEntry> Evidence,
        string? IssueCode,
        string? IssueCategory,
        string? AadstsCode);

    /// <summary>The data of the first event: where the run's files are.</summary>
    private sealed record ReadyData(string RunId, string TraceFile, string IssueFile, string Directory, bool Verbose, string? ConfigSha256, string? AdapterVersion, [property: JsonPropertyName("a2aVersion")] string A2AVersion);

    /// <summary>Runs every stage and returns the report; the diagnostics files are closed when the run is disposed.</summary>
    /// <param name="cancellationToken">Stops the run; the stage in progress fails as canceled and the rest are skipped.</param>
    /// <exception cref="InvalidOperationException">The trace or issue file could not be opened (the message names the path).</exception>
    public async Task<ReadinessReport> ExecuteAsync(CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetUtcNow();

        // The config is pure (no network): load it first so the diagnostics carry its identity hash.
        var configResult = LoadConfig();
        OpenDiagnostics();
        TraceReady();
        _diagnostics.Trace(
            WorkIqTraceStage.Doctor,
            "doctor.started",
            $"m365 doctor run {_runId}: stages {string.Join(", ", ReadinessNames.AllStages.Where(_requested.Contains).Select(ReadinessNames.Of))}; " +
            $"consent {(_options.IUnderstandLiveSideEffects ? "given" : "not given")}; continue after failure {(_options.ContinueAfterFailure ? "yes" : "no")}; " +
            $"stage timeout {Seconds(_options.StageTimeout)} s; raw capture {(_options.RawCaptureDirectory is null ? "off" : "on")}");

        var results = new List<ReadinessStageResult>(ReadinessNames.AllStages.Length);
        string? stopReason = null;
        foreach (var stage in ReadinessNames.AllStages)
        {
            ReadinessStageResult result;
            if (!_requested.Contains(stage))
            {
                result = NotRequested(stage);
            }
            else if (stage == ReadinessStage.Config)
            {
                _diagnostics.Trace(WorkIqTraceStage.Doctor, "doctor.config.start", StartMessage(stage));
                result = configResult;
            }
            else if (_config is null)
            {
                result = Skipped(stage, "not run: the config did not load, and every stage needs it");
            }
            else if (stopReason is not null)
            {
                result = Skipped(stage, stopReason);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                result = Skipped(stage, "not run: the check was canceled");
            }
            else if (stage == ReadinessStage.Probe && !_options.IUnderstandLiveSideEffects)
            {
                result = Skipped(stage, ReadinessStageResult.ConsentSkipReason) with { SkippedForConsent = true };
            }
            else if (stage == ReadinessStage.Census && !_options.IUnderstandLiveSideEffects)
            {
                result = Skipped(stage, "needs the probe, which " + ReadinessStageResult.ConsentSkipReason) with { SkippedForConsent = true };
            }
            else if (stage == ReadinessStage.Census && !_requested.Contains(ReadinessStage.Probe))
            {
                result = Skipped(stage, "not run: the census works on the probe's turn; request the probe too (--probe)");
            }
            else if (stage == ReadinessStage.Census && _probe is null)
            {
                result = Skipped(stage, "not run: the probe produced no turn result to take the census of");
            }
            else
            {
                result = await RunStageAsync(stage, cancellationToken).ConfigureAwait(false);
            }

            Record(result);
            results.Add(result);

            if (result.Result == ReadinessResult.Fail && stopReason is null && !_options.ContinueAfterFailure)
            {
                stopReason = $"not run: the {ReadinessNames.Of(stage)} stage failed (--continue runs the remaining stages anyway)";
            }
        }

        var report = new ReadinessReport(results)
        {
            RunId = _runId,
            StartedAt = startedAt,
            EndedAt = _clock.GetUtcNow(),
            TraceFile = _paths.TraceFile.FullName,
            IssueFile = _paths.IssueFile.FullName,
            ConfigSha256 = _diagnostics.ConfigSha256,
            AdapterVersion = WorkIqDiagnostics.AdapterVersion,
        };

        _diagnostics.Trace(
            WorkIqTraceStage.Doctor,
            "doctor.finished",
            $"m365 doctor run {_runId}: {report.Verdict}: {results.Count(r => r.Result == ReadinessResult.Pass)} passed, " +
            $"{results.Count(r => r.Result == ReadinessResult.Fail)} failed, {results.Count(r => r.Result == ReadinessResult.Skipped)} skipped " +
            $"({results.Count(r => r.SkippedForConsent)} for consent); trace {_paths.TraceFile.FullName}; issues {_paths.IssueFile.FullName}",
            report.AnyFailed ? WorkIqTraceLevel.Error : report.AllPassed ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning);
        return report;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var conversation in _conversations)
        {
            await conversation.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        _diagnostics.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Stage dispatch
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ReadinessStageResult> RunStageAsync(ReadinessStage stage, CancellationToken cancellationToken)
    {
        _diagnostics.Trace(WorkIqTraceStage.Doctor, $"doctor.{ReadinessNames.Of(stage)}.start", StartMessage(stage));
        var started = _clock.GetTimestamp();
        ReadinessStageResult result;
        try
        {
            result = stage switch
            {
                ReadinessStage.Auth => await RunAuthAsync(cancellationToken).ConfigureAwait(false),
                ReadinessStage.Discovery => await RunDiscoveryAsync(cancellationToken).ConfigureAwait(false),
                ReadinessStage.Protocol => await RunProtocolAsync(cancellationToken).ConfigureAwait(false),
                ReadinessStage.Probe => await RunProbeAsync(cancellationToken).ConfigureAwait(false),
                ReadinessStage.Census => await RunCensusAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new UnreachableException($"Stage {stage} has no runner."),
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            result = Failed(
                stage,
                "stopped: the check was canceled while this stage was running",
                "The run was stopped (Ctrl+C or the caller's token) before this stage finished; its outcome is unknown.",
                "Run the doctor again and let it finish.",
                WorkIqIssue.Create(UnderlyingStage(stage), WorkIqIssueCategory.Budget, "doctor.canceled", $"the {ReadinessNames.Of(stage)} stage was canceled", WorkIqIssueClassifier.CauseLocalCanceled),
                new ReadinessEvidence());
        }
        catch (Exception ex)
        {
            result = Unexpected(stage, ex, new ReadinessEvidence());
        }

        return result.ElapsedMs is null ? result with { ElapsedMs = Elapsed(started) } : result;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------------------------------------------

    private void OpenDiagnostics()
    {
        var traceSinks = new List<IWorkIqTraceSink> { _trace };
        JsonlTraceSink? jsonl = null;
        JsonlIssueSink? issues;
        try
        {
            jsonl = new JsonlTraceSink(_paths.TraceFile);
            traceSinks.Add(jsonl);
            if (_options.Verbose)
            {
                traceSinks.Add(new ConsoleTraceSink(_console));
            }

            issues = new JsonlIssueSink(_paths.IssueFile);
        }
        catch (Exception)
        {
            jsonl?.Dispose();
            throw;
        }

        _issueFilter = new ExpectedIssueFilter(issues);

        // The caller's own sinks come last and see exactly what the files see: every event after the same stamping and
        // redaction, every issue behind the same expected-reply filter. They are never disposed with the run.
        CallerOwnedTraceSink.AppendTo(traceSinks, _options.TraceSinks);
        var issueSinks = new List<IWorkIqIssueSink> { _issueFilter };
        foreach (var extra in _options.IssueSinks ?? [])
        {
            issueSinks.Add(_issueFilter.Filtering(extra));
        }

        var configSha256 = _config is null ? null : WorkIqDiagnosticsFactory.ComputeConfigSha256(_config);
        _diagnostics = new WorkIqDiagnostics(_runId, configSha256, traceSinks, issueSinks, _clock);
    }

    private void TraceReady()
    {
        _diagnostics.Trace(new WorkIqTraceEvent(
            default,
            WorkIqTraceStage.Config,
            WorkIqTraceLevel.Info,
            WorkIqDiagnosticsFactory.ReadyEventCode,
            $"Diagnostics ready for m365 doctor run {_runId}: trace file {_paths.TraceFile.FullName}; issue file {_paths.IssueFile.FullName} " +
            $"(verbose console {(_options.Verbose ? "on" : "off")}). Send both files back with the readiness report; neither contains a token.")
        {
            Data = JsonSerializer.SerializeToElement(
                new ReadyData(
                    _runId,
                    _paths.TraceFile.FullName,
                    _paths.IssueFile.FullName,
                    _paths.Directory.FullName,
                    _options.Verbose,
                    _diagnostics.ConfigSha256,
                    WorkIqDiagnostics.AdapterVersion,
                    WorkIqDiagnostics.A2AVersion),
                WorkIqDiagnosticsJson.Options),
        });
    }

    /// <summary>Traces a stage's verdict with its evidence as data, and registers a failed stage in the issue register.</summary>
    private void Record(ReadinessStageResult result)
    {
        var name = ReadinessNames.Of(result.Stage);
        var suffix = result.Result switch
        {
            ReadinessResult.Pass => "pass",
            ReadinessResult.Fail => "fail",
            _ => "skipped",
        };
        var level = result.Result == ReadinessResult.Fail
            ? WorkIqTraceLevel.Error
            : result.SkippedForConsent || result.Warnings.Count > 0 ? WorkIqTraceLevel.Warning : WorkIqTraceLevel.Info;
        var message = result.Result == ReadinessResult.Skipped
            ? $"{name} SKIPPED: {result.SkipReason}"
            : $"{name} {ReadinessNames.Of(result.Result)}: {result.Summary}";
        var data = new StageTraceData(
            ReadinessNames.Of(result.Result),
            result.Requested,
            result.SkipReason,
            result.SkippedForConsent,
            NullIfEmpty(result.Meaning),
            NullIfEmpty(result.Action),
            result.Warnings.Count == 0 ? null : result.Warnings,
            result.Evidence.Where(e => !TraceExcludedKeys.Contains(e.Key)).Select(e => new EvidenceEntry(e.Key, e.Value)).ToArray(),
            result.Issue?.Code,
            result.Issue?.Category.ToString(),
            result.Issue?.AadstsCode);

        _diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Doctor, level, $"doctor.{name}.{suffix}", message)
        {
            TaskId = result.Issue?.TaskId,
            ContextId = result.Issue?.ContextId,
            RequestId = result.Issue?.RequestId,
            HttpStatus = result.Issue?.HttpStatus,
            RpcErrorCode = result.Issue?.RpcErrorCode,
            A2AState = result.Issue?.A2AState,
            ElapsedMs = result.ElapsedMs,
            Data = JsonSerializer.SerializeToElement(data, WorkIqDiagnosticsJson.Options),
        });

        if (result.Result != ReadinessResult.Fail)
        {
            return;
        }

        var issue = result.Issue;
        _diagnostics.Issue(new WorkIqIssue(
            default,
            WorkIqTraceStage.Doctor,
            issue?.Category ?? WorkIqIssueCategory.Unknown,
            $"doctor.{name}.failed",
            $"m365 doctor stage {name} FAILED: {result.Summary}" + (issue is null ? "" : $" [{issue.Code}]"),
            string.IsNullOrWhiteSpace(result.Action) ? result.Meaning : $"{result.Meaning} What to do: {result.Action}")
        {
            CaseId = issue?.CaseId,
            TurnId = issue?.TurnId,
            HttpStatus = issue?.HttpStatus,
            RpcErrorCode = issue?.RpcErrorCode,
            AadstsCode = issue?.AadstsCode,
            A2AState = issue?.A2AState,
            RequestId = issue?.RequestId,
            TaskId = issue?.TaskId,
            ContextId = issue?.ContextId,
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Transport (built lazily: the config stage never needs it)
    // ---------------------------------------------------------------------------------------------------------

    private Microsoft365Config Config => _config ?? throw new InvalidOperationException("The config stage did not succeed.");

    private WorkIqOptions WorkIqOptions => _workIqOptions ??= new WorkIqOptions
    {
        SendTimeout = _options.StageTimeout,
        TurnDeadline = _options.StageTimeout,
        ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,
        ProfileOverride = _options.Profile,
        ContentPolicy = _options.RawCaptureDirectory is null ? EvidenceContentPolicy.SelectedContent : EvidenceContentPolicy.FullEvidence,
        RawCaptureDirectory = _options.RawCaptureDirectory,
        Verbose = _options.Verbose,
        TraceFile = _paths.TraceFile,
        IssueFile = _paths.IssueFile,
        DiagnosticsDirectory = _paths.Directory,
        RunId = _runId,
    };

    /// <summary>The profile the stages run under: <see cref="ReadinessCheckOptions.Profile"/> over the config's.</summary>
    private DiagnosticProfile EffectiveProfile => _options.Profile ?? Config.ResolveProfile();

    private IWorkIqTokenProvider TokenProvider
    {
        get
        {
            if (_tokenProvider is not null)
            {
                return _tokenProvider;
            }

            var provider = _tokenProviderOverride
                ?? new WorkIqDeviceCodeTokenProvider(Config.TenantId, Config.AppClientId, Config.Scope, _console) { Diagnostics = _diagnostics };
            if (provider is IWorkIqSignInWaitSource source)
            {
                // A device-code sign-in is bounded by its code's own expiry, not by the stage timeout meant for network calls.
                source.SignInPrompted = OnSignInPrompted;
            }

            return _tokenProvider = provider;
        }
    }

    /// <summary>
    /// A device-code sign-in started inside the running stage: the stage now waits until the code expires (plus
    /// <see cref="SignInGrace"/>) when that is longer than what the stage timeout leaves, and the trace says so.
    /// </summary>
    /// <param name="expiresOn">When the sign-in code expires.</param>
    private void OnSignInPrompted(DateTimeOffset expiresOn)
    {
        _signInCodeExpiresOn = expiresOn;
        var source = Volatile.Read(ref _activeStage);
        if (source is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var wait = expiresOn - now + SignInGrace;
        if (wait <= _activeStageDeadline - now)
        {
            return;
        }

        try
        {
            source.CancelAfter(wait);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _activeStageDeadline = now + wait;
        _diagnostics.Trace(
            WorkIqTraceStage.Doctor,
            "doctor.sign_in_wait",
            $"a device-code sign-in started: this stage now waits until the code expires at {expiresOn.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC " +
            $"(up to {Seconds(wait)} s) instead of the {Seconds(_options.StageTimeout)} s stage timeout, which bounds network calls only");
    }

    private WorkIqA2AClient Client
    {
        get
        {
            if (_client is not null)
            {
                return _client;
            }

            var (http, client) = Microsoft365AgentFactory.CreateTransport(Config, WorkIqOptions, TokenProvider, _diagnostics, _clock, _innerHandler);
            _http = http;
            _client = client;
            return client;
        }
    }

    private WorkIqConversation NewConversation(WorkIqOptions options)
    {
        var conversation = new WorkIqConversation(Client, Config, options, _clock, _diagnostics);
        _conversations.Add(conversation);
        return conversation;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Result builders
    // ---------------------------------------------------------------------------------------------------------

    private static ReadinessStageResult Passed(ReadinessStage stage, string summary, string meaning, string action, ReadinessEvidence evidence, IReadOnlyList<string>? warnings = null) =>
        new(stage, ReadinessResult.Pass, Redact(summary))
        {
            Meaning = Redact(meaning),
            Action = Redact(action),
            Evidence = evidence.ToList(),
            Warnings = warnings is null || warnings.Count == 0 ? Array.Empty<string>() : warnings.Select(Redact).ToArray(),
        };

    private ReadinessStageResult Failed(ReadinessStage stage, string summary, string meaning, string action, WorkIqIssue issue, ReadinessEvidence evidence) =>
        new(stage, ReadinessResult.Fail, Redact(summary))
        {
            Meaning = Redact(meaning),
            Action = Redact(action),
            Evidence = evidence.ToList(),
            Issue = Stamp(issue),
        };

    private static ReadinessStageResult Skipped(ReadinessStage stage, string reason) =>
        new(stage, ReadinessResult.Skipped, reason) { SkipReason = reason };

    private ReadinessStageResult NotRequested(ReadinessStage stage)
    {
        var reason = stage switch
        {
            ReadinessStage.Probe => "not requested: the probe sends one real prompt to the agent; ask for it with --probe (it also needs --i-understand-live-side-effects)",
            ReadinessStage.Census => "not requested: the census runs after the probe (--probe --i-understand-live-side-effects)",
            _ => "not requested: only the selected stages run",
        };
        return Skipped(stage, reason) with { Requested = false };
    }

    /// <summary>A failure nobody anticipated: the exception is classified and the stage fails with what is known.</summary>
    private ReadinessStageResult Unexpected(ReadinessStage stage, Exception ex, ReadinessEvidence evidence)
    {
        var issue = WorkIqIssueClassifier.FromException(ex, UnderlyingStage(stage));
        evidence.Add("error", issue.Message);
        return Failed(
            stage,
            $"unexpected {ex.GetType().Name}: {FirstLine(ex.Message)}",
            issue.SuggestedCause,
            "Send the report folder back; the trace has the details.",
            issue,
            evidence);
    }

    private WorkIqIssue Stamp(WorkIqIssue issue) => issue with
    {
        At = issue.At == default ? _clock.GetUtcNow() : issue.At,
        RunId = issue.RunId ?? _runId,
        ConfigSha256 = issue.ConfigSha256 ?? _diagnostics.ConfigSha256,
        AdapterVersion = issue.AdapterVersion ?? WorkIqDiagnostics.AdapterVersion,
        A2AVersion = issue.A2AVersion ?? WorkIqDiagnostics.A2AVersion,
        Message = Redact(issue.Message),
        SuggestedCause = Redact(issue.SuggestedCause),
    };

    // ---------------------------------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------------------------------

    private static string StartMessage(ReadinessStage stage) =>
        $"stage {ReadinessNames.Ordinal(stage)}/{ReadinessNames.AllStages.Length} {ReadinessNames.Of(stage)}: " + stage switch
        {
            ReadinessStage.Config => "load and validate the connection config (no network)",
            ReadinessStage.Auth => "acquire a delegated token (silent from the cache first, device-code sign-in otherwise)",
            ReadinessStage.Discovery => "fetch the agent card and select the A2A 1.0 JSON-RPC interface on a trusted host",
            ReadinessStage.Protocol => "GetTask for a random task id; the expected answer is JSON-RPC TaskNotFound (-32001)",
            ReadinessStage.Probe => "one real turn with the fixed probe prompt",
            ReadinessStage.Census => "what the tenant does with the experimental native118 profile and with CancelTask on a finished task",
            _ => stage.ToString(),
        };

    /// <summary>The provider pipeline stage an issue of a readiness stage is attributed to.</summary>
    private static WorkIqTraceStage UnderlyingStage(ReadinessStage stage) => stage switch
    {
        ReadinessStage.Config => WorkIqTraceStage.Config,
        ReadinessStage.Auth => WorkIqTraceStage.Auth,
        ReadinessStage.Discovery => WorkIqTraceStage.Discovery,
        ReadinessStage.Protocol => WorkIqTraceStage.GetTask,
        _ => WorkIqTraceStage.Outcome,
    };

    private double Elapsed(long started) => _clock.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>
    /// The token of one stage's wait: the caller's token plus <paramref name="timeout"/>. It becomes the active stage
    /// source, so a device-code sign-in that starts inside it can extend it to the code's expiry (<see cref="OnSignInPrompted"/>).
    /// </summary>
    private CancellationTokenSource StageTokenSource(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeStageDeadline = _clock.GetUtcNow() + timeout;
        _signInCodeExpiresOn = null;   // a sign-in code is reported only by the stage that started it
        Volatile.Write(ref _activeStage, source);
        source.CancelAfter(timeout);
        return source;
    }

    /// <summary>The trace events this run recorded since <paramref name="mark"/> (an earlier <see cref="ListTraceSink.Count"/>).</summary>
    private IEnumerable<WorkIqTraceEvent> EventsSince(int mark) => _trace.Events.Skip(mark);

    private static string Redact(string text) => WorkIqRedaction.Redact(text);

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Ms(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no message)";
        }

        var trimmed = text.Trim();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end];
    }
}

/// <summary>
/// The evidence of one stage, as unique name/value pairs in display order. Every value is folded to one line and
/// redacted (<see cref="WorkIqRedaction"/>); a null value is recorded as <c>not reported</c>, never as an empty
/// string or a zero.
/// </summary>
internal sealed class ReadinessEvidence
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    /// <summary>How many rows were added.</summary>
    public int Count => _items.Count;

    /// <summary>Adds a row; a repeated name gets a <c> (2)</c>, <c> (3)</c>… suffix.</summary>
    /// <param name="name">The evidence name.</param>
    /// <param name="value">The value; null is recorded as <c>not reported</c>.</param>
    public ReadinessEvidence Add(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = name;
        for (var n = 2; _items.Any(i => string.Equals(i.Key, key, StringComparison.Ordinal)); n++)
        {
            key = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
        }

        _items.Add(new KeyValuePair<string, string>(key, WorkIqRedaction.Redact(OneLine(value ?? "not reported"))));
        return this;
    }

    /// <summary>Adds a row with an integer value.</summary>
    /// <param name="name">The evidence name.</param>
    /// <param name="value">The value.</param>
    public ReadinessEvidence Add(string name, long value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A snapshot of the rows.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToList() => _items.ToArray();

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
}
