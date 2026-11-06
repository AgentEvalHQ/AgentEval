// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// Parsed <c>--sut microsoft365</c> options under the shared <c>eval</c>/<c>bench</c> seam (carried on
/// <see cref="CommonTargetOptions.TargetOptions"/> under the key <c>microsoft365</c>). The consent flag is deliberately
/// NOT here: it is common to every live target and travels on <see cref="CommonTargetOptions.AckLiveSideEffects"/>,
/// registered once per verb by <see cref="SutTargetResolver.AddOptionsTo"/>.
/// </summary>
internal sealed record Microsoft365SutOptions : ISutTargetOptions
{
    /// <summary><c>--m365-config</c>: the Work IQ connection JSON (tenantId, appClientId; optional endpoint, agentId, scope, agentName, diagnosticProfile, timeZone, trustedHosts). It carries no secret.</summary>
    public FileInfo? ConfigFile { get; init; }

    /// <summary><c>--m365-max-turns</c>: cap on the agent turns a live run may make (0 = no cap). Turns are counted; consumption is not metered.</summary>
    public int MaxTurns { get; init; }

    /// <summary><c>--m365-profile</c>: <c>public</c> (answer only) or <c>native118</c> (experimental diagnostics), case-insensitive. Defaults to <see cref="Microsoft365CliOptions.DefaultProfile"/>.</summary>
    public string Profile { get; init; } = Microsoft365CliOptions.DefaultProfile;

    /// <summary><c>--m365-capture-raw</c>: retain the raw A2A request/response bytes and stream frames under this directory (opt-in; may contain tenant data).</summary>
    public DirectoryInfo? CaptureRaw { get; init; }

    /// <summary><c>--m365-verbose</c>: print every provider trace event and one line per turn result to stderr.</summary>
    public bool Verbose { get; init; }

    /// <summary><c>--m365-trace</c>: write the JSONL trace to this file instead of the default under <c>.agenteval/m365/</c>.</summary>
    public FileInfo? TraceFile { get; init; }
}

/// <summary>
/// The <c>--sut microsoft365</c> built-in target: evaluates a LIVE Microsoft 365 agent over Work IQ A2A 1.0 from
/// <c>eval</c> and <c>bench</c> through the shared <see cref="ISutTarget"/> seam. Owns the <c>--m365-*</c> options,
/// their validation (consent first, then config present, turn cap, profile, then the config file itself, all before any
/// network call) and construction, composing <see cref="Microsoft365AgentFactory.BuildLive"/> with a
/// <see cref="CliTurnSink"/>; the consent flag itself is the resolver's. Prints the honesty badge and the diagnostics
/// file paths to stderr before the live agent is returned. The subject is an agent; the run provenance is
/// <see cref="AefRunSettings"/>; an <c>eval</c> run's per-case evidence is <see cref="Microsoft365CaseEvidence"/>.
/// Fresh instance per command execution (the config is memoised across <c>Validate</c>, <c>ResolvedName</c> and
/// <c>Build</c> of ONE execution).
/// </summary>
internal sealed class Microsoft365SutTarget : ISutTarget
{
    /// <summary>The <c>--sut</c> value.</summary>
    public const string SutName = "microsoft365";

    /// <summary>The honesty badge printed once, to stderr, before a live run starts.</summary>
    public const string TargetBadge =
        "Note: --sut microsoft365 observes the agent's final answer and the evidence the tenant emits; tool and retrieval " +
        "evidence is reported as NotRequested / NotProvided / Provided per channel, never inferred.";

    /// <summary>The consent refusal (same shape as the Copilot Studio target's; the common flag is the resolver's).</summary>
    public const string ConsentMessage =
        "--sut microsoft365 drives a LIVE Microsoft 365 agent through Work IQ as the signed-in user; the delegated " +
        "WorkIQAgent.Ask permission covers reads AND writes, and a prompt can trigger real actions that cannot be " +
        "sandboxed. Re-run against a NON-PROD tenant and identity with --i-understand-live-side-effects to proceed " +
        "(nothing was sent).";

    private readonly Option<FileInfo?> _configOpt = Microsoft365CliOptions.Config("Required by --sut microsoft365.");

    private readonly Option<int> _maxTurnsOpt = new("--m365-max-turns")
    {
        DefaultValueFactory = _ => 0,
        Description = "Cap the number of agent turns a live --sut microsoft365 run may make (0 = no cap). Turns are COUNTED, consumption is not metered: Work IQ exposes no usage; a turn that would exceed the cap never fires and the run stops.",
    };

    private readonly Option<string> _profileOpt = Microsoft365CliOptions.Profile();

    private readonly Option<DirectoryInfo?> _captureRawOpt = Microsoft365CliOptions.CaptureRaw();

    private readonly Option<bool> _verboseOpt = Microsoft365CliOptions.Verbose("their paths are printed at start.");

    private readonly Option<FileInfo?> _traceOpt = Microsoft365CliOptions.Trace("under .agenteval/m365/");

    private readonly TextWriter _err;
    private Microsoft365Config? _config;        // memoised within one command execution (Validate -> ResolvedName -> Build)
    private DiagnosticProfile? _profile;        // memoised with it
    private CliTurnSink? _builtSink;            // the sink the live Build wired into the agent

    /// <summary>Creates the target writing its badge to <see cref="Console.Error"/>.</summary>
    public Microsoft365SutTarget()
        : this(null)
    {
    }

    /// <summary>Creates the target with the stderr destination injectable (tests).</summary>
    /// <param name="err">Where the badge and the verbose turn lines go; null means <see cref="Console.Error"/>.</param>
    internal Microsoft365SutTarget(TextWriter? err)
    {
        _err = err ?? Console.Error;
    }

    /// <inheritdoc/>
    public string Sut => SutName;

    /// <inheritdoc/>
    public IReadOnlySet<string> SupportedVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eval", "bench" };

    /// <summary>A Microsoft 365 agent is an agent, not a model: without <c>--subject</c> the run's subject is <c>agent:&lt;display name&gt;</c>.</summary>
    public AefSubjectKind SubjectKind => AefSubjectKind.Agent;

    /// <summary>
    /// The turn sink of the agent this target evaluates: the sink the live <see cref="Build"/> wired into the agent, else
    /// <see cref="TurnSinkForTests"/>, else null (then no turn result can be read back).
    /// </summary>
    internal CliTurnSink? TurnSink => _builtSink ?? TurnSinkForTests;

    /// <summary>
    /// Test seam of the <c>sutOverride</c> path (never set by the CLI): the sink the stand-in's <c>onTurn</c> callback
    /// feeds. <see cref="Build"/> returns a stand-in untouched and so wires no sink of its own.
    /// </summary>
    internal CliTurnSink? TurnSinkForTests { get; init; }

    /// <summary>
    /// Test seam of the live <see cref="Build"/> path (never set by the CLI): the token source handed to
    /// <c>BuildLive</c>; null uses the MSAL device-code provider.
    /// </summary>
    internal AgentEval.Microsoft365.Auth.IWorkIqTokenProvider? TokenProviderForTests { get; init; }

    /// <summary>Test seam of the live <see cref="Build"/> path: the innermost HTTP handler; null uses a socket handler.</summary>
    internal HttpMessageHandler? InnerHandlerForTests { get; init; }

    /// <summary>Test seam of the live <see cref="Build"/> path: the diagnostics directory; null resolves the workspace's <c>.agenteval/m365</c>.</summary>
    internal DirectoryInfo? DiagnosticsDirectoryForTests { get; init; }

    /// <summary>
    /// The run provenance this target adds to an AEF run's <c>config</c> settings: the provider's shared
    /// <see cref="Microsoft365RunSettings.For"/>, for the config and the profile <see cref="Validate"/> loaded (from
    /// <c>--m365-config</c> and <c>--m365-profile</c>), with <c>m365ContentPolicy</c> only when
    /// <paramref name="contentPolicy"/> is known. Null until <see cref="Validate"/> has loaded the config. With a
    /// <c>sutOverride</c> stand-in (a run recorded as mocked) it still describes the target the CLI validated.
    /// </summary>
    /// <param name="contentPolicy">The content policy of the agent's evidence documents, when known.</param>
    internal JsonObject? AefRunSettings(EvidenceContentPolicy? contentPolicy) =>
        _config is { } config ? Microsoft365RunSettings.For(config, _profile ?? config.ResolveProfile(), contentPolicy) : null;

    /// <summary>
    /// The run provenance of a run that writes no per-case evidence document (a <c>bench</c> run):
    /// <see cref="AefRunSettings"/> without <c>m365ContentPolicy</c>, since no projection policy applies. Null when the
    /// target loaded no config.
    /// </summary>
    public JsonObject? RunSettings() => AefRunSettings(contentPolicy: null);

    /// <summary>
    /// The per-case evidence of an <c>eval</c> run against <paramref name="agent"/>: the run provenance with the agent's
    /// content policy, the not-measured rules (a turn with no selected answer, criteria no judge graded), and each case's
    /// evidence document (<see cref="Microsoft365CaseEvidence"/>).
    /// </summary>
    /// <param name="agent">The agent <see cref="Build"/> returned.</param>
    public ISutCaseEvidence? CaseEvidenceFor(IEvaluableAgent agent) => Microsoft365CaseEvidence.For(this, agent);

    /// <summary>
    /// The agent a <c>bench</c> family that grades answers drives: <paramref name="agent"/> behind
    /// <see cref="Microsoft365BenchAgent"/>, so a turn that completed with several answer candidates and none selected
    /// throws instead of handing its empty text to the grader. The family then records the case as an error or as
    /// inconclusive, never as a measurement of the agent, and the turn's warning line says the case is not measured.
    /// </summary>
    /// <param name="agent">The agent <see cref="Build"/> returned.</param>
    public IEvaluableAgent ForGradedBench(IEvaluableAgent agent)
    {
        if (TurnSink is { } sink)
        {
            sink.UnselectedAnswerIsNotMeasured = true;
        }

        return new Microsoft365BenchAgent(agent);
    }

    /// <summary>
    /// Adds the six <c>--m365-*</c> options. NEVER the consent flag: <see cref="SutTargetResolver.AddOptionsTo"/> registers
    /// <c>--i-understand-live-side-effects</c> once per verb, and a second live target declaring the same alias on one
    /// command would be a duplicate-alias error.
    /// </summary>
    /// <param name="command">The verb command.</param>
    public void AddOptionsTo(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Options.Add(_configOpt);
        command.Options.Add(_maxTurnsOpt);
        command.Options.Add(_profileOpt);
        command.Options.Add(_captureRawOpt);
        command.Options.Add(_verboseOpt);
        command.Options.Add(_traceOpt);
    }

    /// <inheritdoc/>
    public ISutTargetOptions? BindOptions(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return new Microsoft365SutOptions
        {
            ConfigFile = parseResult.GetValue(_configOpt),
            MaxTurns = parseResult.GetValue(_maxTurnsOpt),
            Profile = parseResult.GetValue(_profileOpt) ?? Microsoft365CliOptions.DefaultProfile,
            CaptureRaw = parseResult.GetValue(_captureRawOpt),
            Verbose = parseResult.GetValue(_verboseOpt),
            TraceFile = parseResult.GetValue(_traceOpt),
        };
    }

    /// <summary>
    /// Validates in this order, all before any network call: consent (the common flag), config present, turn cap not
    /// negative, profile recognised, then the config file loads and validates (fail fast, memoised).
    /// </summary>
    /// <param name="common">The common options (consent lives here).</param>
    /// <param name="own">This target's options; null means none were bound.</param>
    /// <exception cref="InvalidOperationException">A check failed; the message names the flag or config field.</exception>
    public void Validate(CommonTargetOptions common, ISutTargetOptions? own)
    {
        ArgumentNullException.ThrowIfNull(common);
        var m = own as Microsoft365SutOptions ?? new Microsoft365SutOptions();

        // 1. Consent gate: refuse BEFORE any build or network call.
        if (!common.AckLiveSideEffects)
        {
            throw new InvalidOperationException(ConsentMessage);
        }

        // 2. Config present.
        if (m.ConfigFile is null)
        {
            throw new InvalidOperationException(
                $"--sut microsoft365 requires --m365-config <file.json> ({Microsoft365CliOptions.ConfigFields}).");
        }

        // 3. Turn cap.
        if (m.MaxTurns < 0)
        {
            throw new InvalidOperationException("--m365-max-turns must be >= 0 (0 = no cap; turns are counted, consumption is not metered).");
        }

        // 4. Profile.
        _profile = Microsoft365CliOptions.ParseProfile(m.Profile);

        // 5. The config file itself: fail fast on a missing or bad field, memoised for ResolvedName/Build.
        _config = Microsoft365Config.Load(m.ConfigFile);
    }

    /// <inheritdoc/>
    public string ResolvedName(CommonTargetOptions common, ISutTargetOptions? own) => EnsureConfig(common, own).DisplayName;

    /// <summary>
    /// Returns <paramref name="sutOverride"/> untouched when given (the credential-free test seam: no badge, no sink, no
    /// network). Otherwise builds the live agent: a fresh context per invocation, the turn cap, the profile, raw capture
    /// when a directory was given (content policy <see cref="EvidenceContentPolicy.FullEvidence"/>; else
    /// <see cref="EvidenceContentPolicy.SelectedContent"/>), verbose tracing and the trace file; prints the honesty badge
    /// with the diagnostics paths to stderr; and hands every turn result to the <see cref="CliTurnSink"/>.
    /// </summary>
    /// <param name="common">The common options.</param>
    /// <param name="own">This target's options.</param>
    /// <param name="sutOverride">A pre-built stand-in, returned as-is.</param>
    public IEvaluableAgent Build(CommonTargetOptions common, ISutTargetOptions? own, IEvaluableAgent? sutOverride)
    {
        ArgumentNullException.ThrowIfNull(common);
        if (sutOverride is not null)
        {
            return sutOverride;
        }

        var m = own as Microsoft365SutOptions ?? common.TargetOptionsFor<Microsoft365SutOptions>(Sut) ?? new Microsoft365SutOptions();
        var config = EnsureConfig(common, own);
        var profile = _profile ?? Microsoft365CliOptions.ParseProfile(m.Profile);
        var options = new WorkIqOptions
        {
            ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,
            MaxTurns = m.MaxTurns,
            ProfileOverride = profile,
            ContentPolicy = m.CaptureRaw is null ? EvidenceContentPolicy.SelectedContent : EvidenceContentPolicy.FullEvidence,
            RawCaptureDirectory = m.CaptureRaw,
            Verbose = m.Verbose,
            TraceFile = m.TraceFile,
            DiagnosticsDirectory = DiagnosticsDirectoryForTests,
            // Minted here so the printed trace path is the exact file the run writes (no {runId} placeholder).
            RunId = Microsoft365Diagnostics.NewRunId(),
        };

        var sink = new CliTurnSink(m.Verbose, _err);
        _builtSink = sink;

        // The badge goes out BEFORE the agent exists, so a tester sees the trace/issue paths even if the build fails.
        _err.WriteLine(FormatBadge(Microsoft365Diagnostics.Describe(options), profile, m.MaxTurns));
        _err.Flush();

        // iUnderstandLiveSideEffects: true is safe here: Validate (above) already enforced the common consent flag before
        // SutTargetResolver reaches this Build call.
        return Microsoft365AgentFactory.BuildLive(
            config,
            iUnderstandLiveSideEffects: true,
            options,
            tokenProvider: TokenProviderForTests,
            onTurn: sink.Record,
            innerHandler: InnerHandlerForTests);
    }

    /// <summary>
    /// The one stderr line printed before a live run: the honesty badge, the trace and issue file paths, and (only when
    /// they apply) the experimental-profile and turn-cap statements.
    /// </summary>
    /// <param name="paths">The resolved diagnostics paths.</param>
    /// <param name="profile">The profile in effect.</param>
    /// <param name="maxTurns">The turn cap (0 = none).</param>
    internal static string FormatBadge(Microsoft365DiagnosticsPaths paths, DiagnosticProfile profile, int maxTurns)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var line = new StringBuilder(TargetBadge)
            .Append(" Trace: ").Append(paths.TraceFile.FullName)
            .Append(" · Issues: ").Append(paths.IssueFile.FullName);

        if (profile == DiagnosticProfile.Native118Diagnostics)
        {
            line.Append(" Profile native118: EXPERIMENTAL (observed in the native evaluator 1.18.0, not a documented contract).");
        }

        if (maxTurns > 0)
        {
            line.Append(" Turn cap: ").Append(maxTurns.ToString(CultureInfo.InvariantCulture)).Append(" (turns counted, consumption not metered).");
        }

        return line.ToString();
    }

    private Microsoft365Config EnsureConfig(CommonTargetOptions common, ISutTargetOptions? own)
    {
        if (_config is not null)
        {
            return _config;
        }

        var configFile = (own as Microsoft365SutOptions)?.ConfigFile
            ?? common.TargetOptionsFor<Microsoft365SutOptions>(Sut)?.ConfigFile
            ?? throw new InvalidOperationException("--sut microsoft365 requires --m365-config <file.json>.");
        return _config = Microsoft365Config.Load(configFile);
    }
}
