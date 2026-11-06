// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using AgentEval.Microsoft365;

namespace AgentEval.Microsoft365Demo;

/// <summary>How much the screen shows. The run folder's <c>console.log</c> always holds the <see cref="Diagnostic"/> view.</summary>
internal enum DemoVerbosity
{
    /// <summary>The final table and errors (plus the sign-in prompt and the judge line before the benchmark, which the tester must see).</summary>
    Quiet,

    /// <summary>Default: stage lines (✓/✗/–), one line per turn, one line per issue.</summary>
    Normal,

    /// <summary>Normal plus every trace event (the provider's one-line format) and an issue detail block per issue.</summary>
    Detailed,

    /// <summary>Detailed plus each trace event's data payload and timing.</summary>
    Diagnostic,
}

/// <summary>The validated settings of one demo run, bound from the command line and the environment.</summary>
internal sealed record DemoSettings
{
    /// <summary>The Work IQ connection file (it may not exist: the host then explains how to create one).</summary>
    public required FileInfo ConfigFile { get; init; }

    /// <summary>Where <see cref="ConfigFile"/> came from: <c>--config</c>, <c>M365_CONFIG_PATH</c> or the default.</summary>
    public required string ConfigSource { get; init; }

    /// <summary><c>--agent-id</c>: overrides the config's <c>agentId</c>; null keeps the config's (null there = the default agent).</summary>
    public string? AgentId { get; init; }

    /// <summary>The screen verbosity.</summary>
    public DemoVerbosity Verbosity { get; init; } = DemoVerbosity.Normal;

    /// <summary>Whether the consent to drive the live agent was given (flag or environment variable).</summary>
    public bool Consent { get; init; }

    /// <summary>Where the consent came from, for the banner and the summary.</summary>
    public string ConsentSource { get; init; } = "not given";

    /// <summary><c>--readiness-only</c>.</summary>
    public bool ReadinessOnly { get; init; }

    /// <summary><c>--prompt</c>.</summary>
    public string Prompt { get; init; } = DemoCommandLine.DefaultPrompt;

    /// <summary><c>--dataset</c>; null uses the embedded five read-only cases.</summary>
    public FileInfo? DatasetFile { get; init; }

    /// <summary><c>--no-benchmark</c>.</summary>
    public bool NoBenchmark { get; init; }

    /// <summary><c>--no-judge</c>.</summary>
    public bool NoJudge { get; init; }

    /// <summary>The diagnostic profile from <c>--profile</c>.</summary>
    public DiagnosticProfile Profile { get; init; } = DiagnosticProfile.PublicAnswerOnly;

    /// <summary>The <c>--profile</c> value as typed (<c>public</c> or <c>native118</c>).</summary>
    public string ProfileName { get; init; } = DemoCommandLine.ProfilePublic;

    /// <summary><c>--out</c>; null uses <c>./m365-demo-runs/&lt;yyyyMMdd-HHmmss&gt;Z</c>.</summary>
    public DirectoryInfo? OutDirectory { get; init; }

    /// <summary><c>--capture-raw</c>.</summary>
    public bool CaptureRaw { get; init; }

    /// <summary><c>--zip</c>.</summary>
    public bool Zip { get; init; }

    /// <summary><c>--max-turns</c> (at least 1).</summary>
    public int MaxTurns { get; init; } = DemoCommandLine.DefaultMaxTurns;

    /// <summary><c>--timeout-seconds</c> (at least 1).</summary>
    public int TimeoutSeconds { get; init; } = DemoCommandLine.DefaultTimeoutSeconds;

    /// <summary>The content policy of the run: <see cref="EvidenceContentPolicy.FullEvidence"/> with <c>--capture-raw</c>, else the default.</summary>
    public EvidenceContentPolicy ContentPolicy => CaptureRaw ? EvidenceContentPolicy.FullEvidence : EvidenceContentPolicy.SelectedContent;
}

/// <summary>
/// The demo's command line (System.CommandLine, the package the <c>agenteval</c> CLI uses): the option set, the help text
/// and the binding into <see cref="DemoSettings"/>. Every value is validated here, before anything is created or sent:
/// a mistake is a usage error (exit 2).
/// </summary>
internal static class DemoCommandLine
{
    /// <summary>The default config file name, in the current directory.</summary>
    public const string DefaultConfigFileName = "workiq.json";

    /// <summary>The environment variable that names the config file when <c>--config</c> is not given.</summary>
    public const string ConfigPathVariable = "M365_CONFIG_PATH";

    /// <summary>The environment variable that gives the consent when set to <c>true</c>.</summary>
    public const string ConsentVariable = "M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS";

    /// <summary>The consent flag.</summary>
    public const string ConsentFlag = "--i-understand-live-side-effects";

    /// <summary>The default prompt of the prompt step.</summary>
    public const string DefaultPrompt = "In one sentence, what can you help me with?";

    /// <summary>The default turn cap.</summary>
    public const int DefaultMaxTurns = 12;

    /// <summary>The default bound of one readiness stage or one agent turn.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The folder default run folders are created in, under the current directory.</summary>
    public const string DefaultRunsFolderName = "m365-demo-runs";

    /// <summary>The <c>--profile</c> value of <see cref="DiagnosticProfile.PublicAnswerOnly"/>.</summary>
    public const string ProfilePublic = "public";

    /// <summary>The <c>--profile</c> value of <see cref="DiagnosticProfile.Native118Diagnostics"/>.</summary>
    public const string ProfileNative118 = "native118";

    /// <summary>The template the "no config" message points at, relative to the repository root.</summary>
    public const string TemplateRelativePath = "samples/datasets/m365-config.template.json";

    /// <summary>The command a tester types, as the help text and the messages spell it.</summary>
    public const string RunCommand = "dotnet run --project samples/AgentEval.Microsoft365Demo --";

    /// <summary>The help width, so the help text is the same in every terminal and when redirected.</summary>
    public const int HelpWidth = 120;

    /// <summary>The verbosity values <c>--verbosity</c> accepts.</summary>
    public static readonly IReadOnlyList<string> VerbosityValues = ["quiet", "normal", "detailed", "diagnostic"];

    /// <summary>The root description: what the demo does, the flow, the quick start, the files, the judge and the exit codes.</summary>
    public const string Description =
        "Microsoft 365 agent demo for AgentEval: checks a Microsoft 365 Copilot or declarative agent over Work IQ A2A 1.0 on your own tenant, " +
        "sends one prompt, runs a small benchmark through AgentEval's MAFEvaluationHarness, and saves every trace and issue in a run folder.\n" +
        "\n" +
        "Flow: banner (what will happen, which agent, which identity) -> readiness (config, sign-in, agent card, protocol handshake; no prompt is sent) " +
        "-> one prompt -> benchmark (5 read-only cases by default) -> summary. Everything after readiness needs " + ConsentFlag + ".\n" +
        "\n" +
        "Quick start (from the repository root):\n" +
        "  1. cp " + TemplateRelativePath + " ./workiq.json   (then fill in tenantId and appClientId)\n" +
        "  2. " + RunCommand + " --readiness-only\n" +
        "  3. " + RunCommand + " " + ConsentFlag + " --zip\n" +
        "  4. send back the .zip the run prints (read summary.md first)\n" +
        "\n" +
        "Run folder (always written, whatever --verbosity): console.log (everything printed, plus every trace event in full), trace.jsonl, issues.jsonl, " +
        "readiness.md, readiness.json, turns.jsonl, evidence/<case>.json, results.md, results.json, summary.md; raw/ with --capture-raw; " +
        "<run folder>.zip with --zip. No file contains a token, an auth code or the Authorization header.\n" +
        "\n" +
        "Judge: the benchmark's criteria are graded by the model AI_INFERENCE_PROVIDER selects (bitdeer, openai, azure, foundry or openai-compatible, " +
        "with that provider's variables, the same as the AgentEval samples). A judge call costs money on that provider. Without a judge, or with " +
        "--no-judge, every criterion is NOT MEASURED; the deterministic checks (turn completed, answer non-empty, response time recorded, " +
        "evidence availability recorded) always run.\n" +
        "\n" +
        "Exit codes: 0 every stage that ran passed and everything requested was measured (--readiness-only: readiness passed); " +
        "1 a stage failed (a readiness stage, or the prompt did not complete) or a case FAILED a check; 2 usage error; " +
        "3 runtime error, or a file could not be written (the run folder is still written); " +
        "11 nothing failed but something was not measured (no consent past readiness, no judge for the criteria, a turn that did not complete, " +
        "a case stopped by --max-turns, or no config file).";

    /// <summary>The options of the command, created once per parse.</summary>
    internal sealed class Definition
    {
        /// <summary>Creates the option set and the root command.</summary>
        public Definition()
        {
            Config = new Option<FileInfo?>("--config")
            {
                HelpName = "file",
                Description = "The Work IQ connection file (JSON: tenantId and appClientId; optional endpoint, agentId, scope, agentName, diagnosticProfile, " +
                              "timeZone, trustedHosts). It holds no secret. Default: ./workiq.json, or the path in " + ConfigPathVariable + ". " +
                              "Create it from " + TemplateRelativePath + " (or workiq.example.json in this demo's folder).",
            };
            AgentId = new Option<string?>("--agent-id")
            {
                HelpName = "id",
                Description = "The agent to test, as its opaque id (prefix and dot included; overrides the config's agentId). Without it, the config's " +
                              "agentId is used; when that is null too, the default Work IQ agent (Microsoft 365 Copilot) is tested.",
            };
            Verbosity = new Option<string>("--verbosity", "-v")
            {
                HelpName = "quiet|normal|detailed|diagnostic",
                DefaultValueFactory = _ => "normal",
                Description = "What the screen shows. quiet: the final table and errors, plus the sign-in prompt and which judge grades the benchmark. " +
                              "normal: stage lines (✓/✗/–), one line per turn, one line per issue. " +
                              "detailed: also every trace event and an issue detail block (stage, category, code, AADSTS/HTTP/JSON-RPC codes, request-id, " +
                              "task/context ids, message, cause, action). diagnostic: also each event's data and timings. console.log in the run folder " +
                              "always holds the diagnostic view.",
            };
            Verbosity.AcceptOnlyFromAmong([.. VerbosityValues]);
            Consent = new Option<bool>(ConsentFlag)
            {
                Description = "Required for anything after readiness: the prompt and the benchmark drive the LIVE agent as the signed-in user. The delegated " +
                              "WorkIQAgent.Ask permission covers reads AND writes of that user's Microsoft 365 data, and a prompt can trigger real actions that " +
                              "cannot be sandboxed: use a non-production tenant and identity. Also given by " + ConsentVariable + "=true. Without it the demo " +
                              "runs readiness only and exits 11.",
            };
            ReadinessOnly = new Option<bool>("--readiness-only")
            {
                Description = "Run the readiness check only (config, sign-in, agent card, protocol handshake); no prompt reaches the agent and no consent " +
                              "is needed. Exit 0 when every stage passed.",
            };
            Prompt = new Option<string>("--prompt")
            {
                HelpName = "text",
                DefaultValueFactory = _ => DefaultPrompt,
                Description = "The one prompt of the prompt step. Keep it read-only.",
            };
            Dataset = new Option<FileInfo?>("--dataset")
            {
                HelpName = "file",
                Description = "The benchmark cases: a JSON array of objects with id and input, and optional evaluation_criteria (strings), passing_score " +
                              "(0-100, default 70), category and tags: the format of samples/datasets/m365-smoke.json. Ids use letters, digits, '.', '_' " +
                              "or '-'. 'expected' is accepted and ignored, and the run says so (the agenteval CLI uses it as a substring check when a case " +
                              "is not graded by a judge); any other field is ignored as well, and the run names it per case. Default: the 5 read-only cases embedded in the demo (from " +
                              "m365-smoke.json, without 'expected').",
            };
            NoBenchmark = new Option<bool>("--no-benchmark")
            {
                Description = "Skip the benchmark: readiness and the one prompt only.",
            };
            NoJudge = new Option<bool>("--no-judge")
            {
                Description = "Do not grade the criteria even when a judge model is configured (no judge calls, no judge cost). The criteria are then " +
                              "NOT MEASURED (exit 11 when nothing failed).",
            };
            Profile = new Option<string>("--profile")
            {
                HelpName = "public|native118",
                DefaultValueFactory = _ => ProfilePublic,
                Description = "public: answer only (text/plain). native118: EXPERIMENTAL, also requests the two diagnostic output modes observed in " +
                              "Microsoft's native evaluator 1.18.0 (tool and retrieval info); not a documented contract.",
            };
            Profile.AcceptOnlyFromAmong(ProfilePublic, ProfileNative118);
            Out = new Option<DirectoryInfo?>("--out")
            {
                HelpName = "dir",
                Description = "The run folder. Default: ./m365-demo-runs/<yyyyMMdd-HHmmss>Z (UTC). It must not exist yet, or be empty.",
            };
            CaptureRaw = new Option<bool>("--capture-raw")
            {
                Description = "Also keep the raw A2A requests, responses and stream frames under raw/ (authorization headers and tokens are removed). " +
                              "They contain tenant content (answers, document names, people): read them before you send them.",
            };
            Zip = new Option<bool>("--zip")
            {
                Description = "Also write <run folder>.zip next to the run folder, ready to send back.",
            };
            MaxTurns = new Option<int>("--max-turns")
            {
                HelpName = "n",
                DefaultValueFactory = _ => DefaultMaxTurns,
                Description = "The most agent turns this run may send: the prompt plus every case (at least 1). The run stops before a turn that " +
                              "would cross it; the cases it did not run are NOT MEASURED. Turns are counted; cost is not metered.",
            };
            TimeoutSeconds = new Option<int>("--timeout-seconds")
            {
                HelpName = "n",
                DefaultValueFactory = _ => DefaultTimeoutSeconds,
                Description = "The most one readiness stage or one agent turn may take, in seconds (at least 1). The device-code sign-in happens " +
                              "in readiness, which waits for the sign-in code's own expiry (about 15 minutes) instead; the prompt and the cases then " +
                              "reuse the cached token.",
            };

            Root = new RootCommand(Description);
            foreach (var option in new Option[] { Config, AgentId, Verbosity, Consent, ReadinessOnly, Prompt, Dataset, NoBenchmark, NoJudge, Profile, Out, CaptureRaw, Zip, MaxTurns, TimeoutSeconds })
            {
                Root.Options.Add(option);
            }

            if (Root.Options.OfType<HelpOption>().FirstOrDefault()?.Action is HelpAction help)
            {
                help.MaxWidth = HelpWidth;
            }
        }

        /// <summary>The root command.</summary>
        public RootCommand Root { get; }

        /// <summary><c>--config</c>.</summary>
        public Option<FileInfo?> Config { get; }

        /// <summary><c>--agent-id</c>.</summary>
        public Option<string?> AgentId { get; }

        /// <summary><c>--verbosity</c> / <c>-v</c>.</summary>
        public Option<string> Verbosity { get; }

        /// <summary><c>--i-understand-live-side-effects</c>.</summary>
        public Option<bool> Consent { get; }

        /// <summary><c>--readiness-only</c>.</summary>
        public Option<bool> ReadinessOnly { get; }

        /// <summary><c>--prompt</c>.</summary>
        public Option<string> Prompt { get; }

        /// <summary><c>--dataset</c>.</summary>
        public Option<FileInfo?> Dataset { get; }

        /// <summary><c>--no-benchmark</c>.</summary>
        public Option<bool> NoBenchmark { get; }

        /// <summary><c>--no-judge</c>.</summary>
        public Option<bool> NoJudge { get; }

        /// <summary><c>--profile</c>.</summary>
        public Option<string> Profile { get; }

        /// <summary><c>--out</c>.</summary>
        public Option<DirectoryInfo?> Out { get; }

        /// <summary><c>--capture-raw</c>.</summary>
        public Option<bool> CaptureRaw { get; }

        /// <summary><c>--zip</c>.</summary>
        public Option<bool> Zip { get; }

        /// <summary><c>--max-turns</c>.</summary>
        public Option<int> MaxTurns { get; }

        /// <summary><c>--timeout-seconds</c>.</summary>
        public Option<int> TimeoutSeconds { get; }
    }

    /// <summary>
    /// Binds a parse result without errors into <see cref="DemoSettings"/> and validates every value. Nothing is created
    /// and nothing is read except the presence of <c>--out</c>'s folder.
    /// </summary>
    /// <param name="parse">The parse result (no errors).</param>
    /// <param name="definition">The option set it was parsed with.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <param name="settings">The settings when valid.</param>
    /// <param name="error">The usage error when not.</param>
    public static bool TryBind(
        ParseResult parse,
        Definition definition,
        Func<string, string?> getEnvironmentVariable,
        out DemoSettings? settings,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        settings = null;

        // ── the config file: --config, else M365_CONFIG_PATH, else ./workiq.json ──
        FileInfo configFile;
        string configSource;
        var configOption = parse.GetValue(definition.Config);
        var configVariable = getEnvironmentVariable(ConfigPathVariable);
        if (configOption is not null)
        {
            configFile = configOption;
            configSource = "--config";
        }
        else if (!string.IsNullOrWhiteSpace(configVariable))
        {
            try
            {
                configFile = new FileInfo(configVariable.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"{ConfigPathVariable} is not a usable file path ({ex.Message}).";
                return false;
            }

            configSource = ConfigPathVariable;
        }
        else
        {
            configFile = new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigFileName));
            configSource = "default ./" + DefaultConfigFileName;
        }

        // ── the agent id ──
        var agentId = parse.GetValue(definition.AgentId);
        if (agentId is not null && string.IsNullOrWhiteSpace(agentId))
        {
            error = "--agent-id must not be blank (leave the option out to use the config's agentId or the default Work IQ agent).";
            return false;
        }

        if (agentId is not null && agentId.Trim().Any(c => c is '/' or '\\' or '?' or '#' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = "--agent-id must be a single opaque path segment: no '/', '\\', '?', '#', whitespace or control characters (pass the id exactly as the agent's card URL uses it).";
            return false;
        }

        // ── verbosity and profile (both restricted by the parser; mapped here) ──
        var verbosity = (parse.GetValue(definition.Verbosity) ?? "normal").Trim().ToLowerInvariant() switch
        {
            "quiet" => DemoVerbosity.Quiet,
            "detailed" => DemoVerbosity.Detailed,
            "diagnostic" => DemoVerbosity.Diagnostic,
            _ => DemoVerbosity.Normal,
        };
        var profileName = (parse.GetValue(definition.Profile) ?? ProfilePublic).Trim().ToLowerInvariant();
        var profile = profileName == ProfileNative118 ? DiagnosticProfile.Native118Diagnostics : DiagnosticProfile.PublicAnswerOnly;

        // ── consent: the flag, else M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true ──
        var consentFlag = parse.GetValue(definition.Consent);
        var consentVariable = getEnvironmentVariable(ConsentVariable);
        var consentFromVariable = string.Equals(consentVariable?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var consentSource = consentFlag ? ConsentFlag : consentFromVariable ? ConsentVariable + "=true" : "not given";

        // ── prompt, dataset, numbers ──
        var prompt = parse.GetValue(definition.Prompt) ?? DefaultPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = "--prompt must not be blank.";
            return false;
        }

        var dataset = parse.GetValue(definition.Dataset);
        if (dataset is not null && !dataset.Exists)
        {
            error = $"--dataset file not found: {dataset.FullName}";
            return false;
        }

        var maxTurns = parse.GetValue(definition.MaxTurns);
        if (maxTurns < 1)
        {
            error = $"--max-turns must be at least 1 (got {maxTurns.ToString(CultureInfo.InvariantCulture)}); the demo always runs with a turn cap.";
            return false;
        }

        var timeoutSeconds = parse.GetValue(definition.TimeoutSeconds);
        if (timeoutSeconds < 1)
        {
            error = $"--timeout-seconds must be at least 1 (got {timeoutSeconds.ToString(CultureInfo.InvariantCulture)}).";
            return false;
        }

        // ── the run folder: new, or empty ──
        var outDirectory = parse.GetValue(definition.Out);
        if (outDirectory is not null)
        {
            outDirectory.Refresh();
            if (File.Exists(outDirectory.FullName))
            {
                error = $"--out names a file, not a folder: {outDirectory.FullName}";
                return false;
            }

            if (outDirectory.Exists && outDirectory.EnumerateFileSystemInfos().Any())
            {
                error = $"--out folder is not empty: {outDirectory.FullName}. Choose a new or empty folder (a run never mixes its files with another run's).";
                return false;
            }
        }

        settings = new DemoSettings
        {
            ConfigFile = configFile,
            ConfigSource = configSource,
            AgentId = agentId?.Trim(),
            Verbosity = verbosity,
            Consent = consentFlag || consentFromVariable,
            ConsentSource = consentSource,
            ReadinessOnly = parse.GetValue(definition.ReadinessOnly),
            Prompt = prompt,
            DatasetFile = dataset,
            NoBenchmark = parse.GetValue(definition.NoBenchmark),
            NoJudge = parse.GetValue(definition.NoJudge),
            Profile = profile,
            ProfileName = profileName,
            OutDirectory = outDirectory,
            CaptureRaw = parse.GetValue(definition.CaptureRaw),
            Zip = parse.GetValue(definition.Zip),
            MaxTurns = maxTurns,
            TimeoutSeconds = timeoutSeconds,
        };
        error = "";
        return true;
    }
}
