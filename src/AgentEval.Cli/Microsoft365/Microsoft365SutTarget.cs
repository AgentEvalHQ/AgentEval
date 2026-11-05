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
using AgentEval.Microsoft365.Evidence;

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

    /// <summary><c>--m365-profile</c>: <c>public</c> (answer only) or <c>native118</c> (experimental diagnostics), case-insensitive. Defaults to <see cref="Microsoft365SutTarget.DefaultProfile"/>.</summary>
    public string Profile { get; init; } = Microsoft365SutTarget.DefaultProfile;

    /// <summary><c>--m365-capture-raw</c>: retain the raw A2A request/response bytes and stream frames under this directory (opt-in; may contain tenant data).</summary>
    public DirectoryInfo? CaptureRaw { get; init; }

    /// <summary><c>--m365-verbose</c>: print every provider trace event and one line per turn result to stderr.</summary>
    public bool Verbose { get; init; }

    /// <summary><c>--m365-trace</c>: write the JSONL trace to this file instead of the default under <c>.agenteval/m365/</c>.</summary>
    public FileInfo? TraceFile { get; init; }
}

/// <summary>
/// The <c>--sut microsoft365</c> built-in target (M30): evaluates a LIVE Microsoft 365 agent over Work IQ A2A 1.0 from
/// <c>eval</c> and <c>bench</c> through the shared <see cref="ISutTarget"/> seam. Owns the <c>--m365-*</c> options,
/// their validation (consent first, then config present, turn cap, profile, then the config file itself, all before any
/// network call) and construction, composing <see cref="Microsoft365AgentFactory.BuildLive"/> with a
/// <see cref="CliTurnSink"/>; the consent flag itself is the resolver's. Prints the HLD section 9 honesty badge and the
/// diagnostics file paths to stderr before the live agent is returned. Fresh instance per command execution (the config
/// is memoised across <c>Validate</c>, <c>ResolvedName</c> and <c>Build</c> of ONE execution).
/// </summary>
internal sealed class Microsoft365SutTarget : ISutTarget
{
    /// <summary>The <c>--sut</c> value.</summary>
    public const string SutName = "microsoft365";

    /// <summary>The <c>--m365-profile</c> value for <see cref="DiagnosticProfile.PublicAnswerOnly"/> (the default).</summary>
    public const string DefaultProfile = "public";

    /// <summary>The <c>--m365-profile</c> value for <see cref="DiagnosticProfile.Native118Diagnostics"/> (experimental).</summary>
    public const string Native118Profile = "native118";

    /// <summary>The honesty badge (HLD section 9) printed once, to stderr, before a live run starts.</summary>
    public const string TargetBadge =
        "Note: --sut microsoft365 observes the agent's final answer and the evidence the tenant emits; tool and retrieval " +
        "evidence is reported as NotRequested / NotProvided / Provided per channel, never inferred.";

    /// <summary>The consent refusal (same shape as the Copilot Studio target's; the common flag is the resolver's).</summary>
    public const string ConsentMessage =
        "--sut microsoft365 drives a LIVE Microsoft 365 agent through Work IQ as the signed-in user; the delegated " +
        "WorkIQAgent.Ask permission covers reads AND writes, and a prompt can trigger real actions that cannot be " +
        "sandboxed. Re-run against a NON-PROD tenant and identity with --i-understand-live-side-effects to proceed " +
        "(nothing was sent).";

    private readonly Option<FileInfo?> _configOpt = new("--m365-config")
    {
        Description = "JSON file with the Work IQ connection: tenantId, appClientId; optional endpoint, agentId (opaque; null = the default Work IQ agent), scope, agentName, diagnosticProfile (PublicAnswerOnly|Native118Diagnostics), timeZone (IANA), trustedHosts. No secret is stored; the token is acquired at run time via MSAL device-code auth + an OS-encrypted cache. Required by --sut microsoft365.",
    };

    private readonly Option<int> _maxTurnsOpt = new("--m365-max-turns")
    {
        DefaultValueFactory = _ => 0,
        Description = "Cap the number of agent turns a live --sut microsoft365 run may make (0 = no cap). Turns are COUNTED, consumption is not metered: Work IQ exposes no usage; a turn that would exceed the cap never fires and the run stops.",
    };

    private readonly Option<string> _profileOpt = new("--m365-profile")
    {
        DefaultValueFactory = _ => DefaultProfile,
        Description = "public = answer only (text/plain); native118 = EXPERIMENTAL, also requests the two diagnostic output modes observed in Microsoft's native evaluator 1.18.0 (tool and retrieval info); not a documented contract",
    };

    private readonly Option<DirectoryInfo?> _captureRawOpt = new("--m365-capture-raw")
    {
        Description = "Retain the raw A2A request/response bytes and stream frames under this directory (opt-in; may contain tenant data; keep it out of source control).",
    };

    private readonly Option<bool> _verboseOpt = new("--m365-verbose")
    {
        Description = "Print every provider trace event to stderr (stage, code, ids, HTTP status, JSON-RPC code, A2A state, elapsed). The JSONL trace and issue files are always written; their paths are printed at start.",
    };

    private readonly Option<FileInfo?> _traceOpt = new("--m365-trace")
    {
        Description = "Write the JSONL trace to this file instead of the default under .agenteval/m365/.",
    };

    private readonly TextWriter _err;
    private Microsoft365Config? _config;        // memoised within one command execution (Validate -> ResolvedName -> Build)
    private DiagnosticProfile? _profile;        // memoised with it

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

    /// <summary>The turn sink of the live agent built by <see cref="Build"/>; null until a live build, and with a <c>sutOverride</c>.</summary>
    internal CliTurnSink? TurnSink { get; private set; }

    /// <summary>
    /// Test seam of the live <see cref="Build"/> path (never set by the CLI): the token source handed to
    /// <c>BuildLive</c>; null uses the MSAL device-code provider.
    /// </summary>
    internal AgentEval.Microsoft365.Auth.IWorkIqTokenProvider? TokenProviderForTests { get; init; }

    /// <summary>Test seam of the live <see cref="Build"/> path: the innermost HTTP handler; null uses a socket handler.</summary>
    internal HttpMessageHandler? InnerHandlerForTests { get; init; }

    /// <summary>Test seam of the live <see cref="Build"/> path: the diagnostics directory; null resolves the workspace's <c>.agenteval/m365</c>.</summary>
    internal DirectoryInfo? DiagnosticsDirectoryForTests { get; init; }

    /// <summary>AEF run setting (HLD D15): the A2A protocol version the provider speaks.</summary>
    internal const string A2AVersionSetting = "a2aVersion";

    /// <summary>AEF run setting (HLD D15): the diagnostic profile in effect (the <see cref="DiagnosticProfile"/> member name).</summary>
    internal const string ProfileSetting = "m365Profile";

    /// <summary>AEF run setting (HLD D15): the evidence normaliser version (<see cref="Microsoft365Evidence.NormalizerVersion"/>).</summary>
    internal const string NormalizerVersionSetting = "normalizerVersion";

    /// <summary>AEF run setting (HLD D15): the provider profile / decoder version (<see cref="Microsoft365Evidence.ProviderProfileVersion"/>).</summary>
    internal const string ProviderProfileVersionSetting = "providerProfileVersion";

    /// <summary>AEF run setting (HLD D15): the config's identity hash (<see cref="Microsoft365Config.ComputeIdentitySha256"/>).</summary>
    internal const string ConfigSha256Setting = "m365ConfigSha256";

    /// <summary>AEF run setting (HLD D15): the opaque agent id, or <see cref="DefaultAgentIdSettingValue"/>.</summary>
    internal const string AgentIdSetting = "m365AgentId";

    /// <summary>The <see cref="AgentIdSetting"/> value when the config targets the default Work IQ agent (no agent id).</summary>
    internal const string DefaultAgentIdSettingValue = "default";

    /// <summary>AEF run setting (HLD D15): the host of the configured endpoint (where the agent card is discovered).</summary>
    internal const string EndpointHostSetting = "m365EndpointHost";

    /// <summary>AEF run setting: the <see cref="EvidenceContentPolicy"/> the per-case evidence documents are projected under, when known.</summary>
    internal const string ContentPolicySetting = "m365ContentPolicy";

    /// <summary>
    /// The turn sink of <paramref name="agent"/>: the sink <see cref="Build"/> wired into the live agent, else the sink a
    /// caller attached to a <c>sutOverride</c> stand-in (<see cref="CliTurnSink.AttachTo"/>), else null — then no turn
    /// result can be read back and no per-case evidence document is written.
    /// </summary>
    /// <param name="agent">The agent the command evaluates (what <see cref="Build"/> returned).</param>
    internal CliTurnSink? SinkFor(IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return TurnSink ?? CliTurnSink.AttachedTo(agent);
    }

    /// <summary>
    /// The run provenance this target adds to an AEF run's <c>config</c> settings (HLD D15, M31), all of it content-free:
    /// <c>a2aVersion</c> (the only protocol version spoken), <c>m365Profile</c> (the diagnostic profile in effect, from
    /// <c>--m365-profile</c>), <c>normalizerVersion</c> and <c>providerProfileVersion</c> (the normaliser and decoder
    /// versions that shape the evidence documents), <c>m365ConfigSha256</c> (the config's identity hash, the same
    /// <c>configSha256</c> every Work IQ issue carries; the config holds no secret), <c>m365AgentId</c> (the opaque agent
    /// id, or <c>default</c>), <c>m365EndpointHost</c> (the configured endpoint's host, where the card is discovered) and,
    /// when <paramref name="contentPolicy"/> is known, <c>m365ContentPolicy</c>. Null until <see cref="Validate"/> has loaded
    /// the config. It describes the target the CLI validated: with a <c>sutOverride</c> stand-in (a run recorded as mocked)
    /// the identity and profile are those of <c>--m365-config</c> and <c>--m365-profile</c>.
    /// </summary>
    /// <param name="contentPolicy">The content policy of the agent's evidence documents, when known.</param>
    internal JsonObject? AefRunSettings(EvidenceContentPolicy? contentPolicy)
    {
        var config = _config;
        if (config is null)
        {
            return null;
        }

        var settings = new JsonObject
        {
            [A2AVersionSetting] = Microsoft365Evidence.A2AVersion,
            [ProfileSetting] = (_profile ?? config.ResolveProfile()).ToString(),
            [NormalizerVersionSetting] = Microsoft365Evidence.NormalizerVersion,
            [ProviderProfileVersionSetting] = Microsoft365Evidence.ProviderProfileVersion,
            [ConfigSha256Setting] = config.ComputeIdentitySha256(),
            [AgentIdSetting] = string.IsNullOrWhiteSpace(config.AgentId) ? DefaultAgentIdSettingValue : config.AgentId.Trim(),
            [EndpointHostSetting] = config.ResolveEndpointUri().Host,
        };
        if (contentPolicy is { } policy)
        {
            settings[ContentPolicySetting] = policy.ToString();
        }

        return settings;
    }

    /// <summary>
    /// The run provenance a <c>bench</c> command adds to its AEF run when <paramref name="sut"/> is <c>microsoft365</c>:
    /// <see cref="AefRunSettings"/> of the validated target among <paramref name="targets"/> (the instance the resolver
    /// validated and built), without <c>m365ContentPolicy</c> — a bench run writes no per-case evidence document, so no
    /// projection policy is claimed. Null for any other <c>--sut</c>, or when the target loaded no config.
    /// </summary>
    /// <param name="targets">The targets the bench command resolved <c>--sut</c> against.</param>
    /// <param name="sut">The parsed <c>--sut</c> value.</param>
    internal static JsonObject? BenchRunSettings(IEnumerable<ISutTarget> targets, string? sut)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!string.Equals(sut?.Trim(), SutName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return targets.OfType<Microsoft365SutTarget>().FirstOrDefault()?.AefRunSettings(contentPolicy: null);
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
            Profile = parseResult.GetValue(_profileOpt) ?? DefaultProfile,
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
                "--sut microsoft365 requires --m365-config <file.json> (tenantId, appClientId; optional endpoint, agentId, " +
                "scope, agentName, diagnosticProfile, timeZone, trustedHosts).");
        }

        // 3. Turn cap.
        if (m.MaxTurns < 0)
        {
            throw new InvalidOperationException("--m365-max-turns must be >= 0 (0 = no cap; turns are counted, consumption is not metered).");
        }

        // 4. Profile.
        _profile = ParseProfile(m.Profile);

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
        var profile = _profile ?? ParseProfile(m.Profile);
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
        TurnSink = sink;

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

    /// <summary>Maps a <c>--m365-profile</c> value (case-insensitive, trimmed) to the profile, or throws naming the valid values.</summary>
    /// <param name="value">The flag value.</param>
    /// <exception cref="InvalidOperationException">The value is not <c>public</c> or <c>native118</c>.</exception>
    internal static DiagnosticProfile ParseProfile(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            DefaultProfile => DiagnosticProfile.PublicAnswerOnly,
            Native118Profile => DiagnosticProfile.Native118Diagnostics,
            _ => throw new InvalidOperationException(
                $"--m365-profile must be '{DefaultProfile}' (answer only) or '{Native118Profile}' (EXPERIMENTAL diagnostics, " +
                $"observed in the native evaluator 1.18.0, not a documented contract); got: '{value}'."),
        };
    }

    /// <summary>
    /// The one stderr line printed before a live run: the honesty badge, the trace and issue file paths, and (only when
    /// they apply) the experimental-profile and turn-cap statements of HLD section 9.
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
