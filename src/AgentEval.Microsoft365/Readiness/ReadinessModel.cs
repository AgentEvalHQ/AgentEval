// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>The stages of the Microsoft 365 readiness check (<c>agenteval m365 doctor</c>), in the order they run.</summary>
public enum ReadinessStage
{
    /// <summary>Loads and validates the connection config (no network).</summary>
    Config,

    /// <summary>Acquires a delegated token: silent from the cache first, device-code sign-in otherwise.</summary>
    Auth,

    /// <summary>Fetches the agent card and selects the A2A 1.0 JSON-RPC interface on a trusted host.</summary>
    Discovery,

    /// <summary>Sends <c>GetTask</c> for a random task id; the JSON-RPC TaskNotFound answer proves the endpoint speaks A2A 1.0 and accepts the token.</summary>
    Protocol,

    /// <summary>Opt-in, needs consent: one real turn, <c>Reply with the single word READY.</c></summary>
    Probe,

    /// <summary>After a probe, needs consent: what the tenant does with the experimental native118 profile and with <c>CancelTask</c> on a finished task.</summary>
    Census,
}

/// <summary>The verdict of one readiness stage.</summary>
public enum ReadinessResult
{
    /// <summary>The stage ran and its check succeeded.</summary>
    Pass,

    /// <summary>The stage ran and its check failed; <see cref="ReadinessStageResult.Issue"/> says why.</summary>
    Fail,

    /// <summary>The stage did not run; <see cref="ReadinessStageResult.SkipReason"/> says why. A skip is never a pass.</summary>
    Skipped,
}

/// <summary>
/// The outcome of one readiness stage: the verdict, a one-line summary, the evidence it was reached from (name/value
/// pairs in display order), what the verdict means and what to do next, the classified issue of a failure, the
/// elapsed time and, for a skipped stage, why it did not run. No value ever carries a token, an auth code or the
/// <c>Authorization</c> header.
/// </summary>
/// <param name="Stage">The stage.</param>
/// <param name="Result">The verdict.</param>
/// <param name="Summary">One line: what was observed.</param>
public sealed record ReadinessStageResult(ReadinessStage Stage, ReadinessResult Result, string Summary)
{
    /// <summary>The <see cref="SkipReason"/> of a probe that was requested without the consent flag.</summary>
    public const string ConsentSkipReason = "needs --i-understand-live-side-effects";

    /// <summary>The evidence, as name/value pairs in display order (names are unique within a stage).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Evidence { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>What the verdict means, in plain words; empty for a skipped stage.</summary>
    public string Meaning { get; init; } = "";

    /// <summary>What to do next; empty when nothing is needed.</summary>
    public string Action { get; init; } = "";

    /// <summary>The classified issue of a failed stage (category, code, AADSTS code, HTTP status, JSON-RPC code, cause); null otherwise.</summary>
    public WorkIqIssue? Issue { get; init; }

    /// <summary>How long the stage took, in milliseconds; null for a stage that did not run.</summary>
    public double? ElapsedMs { get; init; }

    /// <summary>Why a <see cref="ReadinessResult.Skipped"/> stage did not run; null otherwise.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Whether the stage was requested but skipped only because the consent flag was missing.</summary>
    public bool SkippedForConsent { get; init; }

    /// <summary>Whether the stage was requested (<see cref="ReadinessCheckOptions.Stages"/>, or the default set); the config stage always is.</summary>
    public bool Requested { get; init; } = true;

    /// <summary>Observations that do not change the verdict but deserve a look (for example a task returned for a random id).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>The machine the readiness check ran on (no user or host name).</summary>
/// <param name="OperatingSystem">The OS description.</param>
/// <param name="DotNet">The .NET runtime description.</param>
/// <param name="MachineTimeZone">The machine's local time zone id.</param>
public sealed record ReadinessEnvironment(string OperatingSystem, string DotNet, string MachineTimeZone)
{
    /// <summary>The environment of the current process.</summary>
    public static ReadinessEnvironment Current() =>
        new(RuntimeInformation.OSDescription.Trim(), RuntimeInformation.FrameworkDescription.Trim(), TimeZoneInfo.Local.Id);
}

/// <summary>
/// The readiness report of one <c>agenteval m365 doctor</c> run: every stage in order (requested or not, with the
/// verdict or the reason it did not run), the run id that stamps the trace and the issue register, where those two
/// files are, the config identity hash, the adapter version and the machine. Rendered by
/// <see cref="ReadinessReportRenderer"/> to <c>readiness.md</c>, <c>readiness.json</c> and the console.
/// </summary>
/// <param name="Stages">Every stage, in <see cref="ReadinessStage"/> order.</param>
public sealed record ReadinessReport(IReadOnlyList<ReadinessStageResult> Stages)
{
    /// <summary>The schema id of the JSON rendering.</summary>
    public const string SchemaVersion = "agenteval.m365.readiness/v1";

    /// <summary>The run id stamped on every trace event and issue of this check.</summary>
    public string RunId { get; init; } = "";

    /// <summary>When the check started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the check ended.</summary>
    public DateTimeOffset EndedAt { get; init; }

    /// <summary>The JSONL trace file of the run (every event).</summary>
    public string? TraceFile { get; init; }

    /// <summary>The JSONL issue register the run appended to (every anomaly with its cause).</summary>
    public string? IssueFile { get; init; }

    /// <summary>The folder the caller wrote the report into (<c>readiness.md</c>, <c>readiness.json</c>); null when the caller writes none.</summary>
    public string? ReportDirectory { get; init; }

    /// <summary>SHA-256 of the config's canonical identity (endpoint, agent, tenant, app, scope, profile); null when the config did not load.</summary>
    public string? ConfigSha256 { get; init; }

    /// <summary>The provider's informational version.</summary>
    public string? AdapterVersion { get; init; }

    /// <summary>The A2A protocol version this connector speaks.</summary>
    public string A2AVersion { get; init; } = WorkIqDiagnostics.A2AVersion;

    /// <summary>The machine the check ran on.</summary>
    public ReadinessEnvironment Environment { get; init; } = ReadinessEnvironment.Current();

    /// <summary>Whether every requested stage ran and passed (and at least one did).</summary>
    public bool AllPassed =>
        Stages.Any(s => s.Requested) && Stages.Where(s => s.Requested).All(s => s.Result == ReadinessResult.Pass);

    /// <summary>Whether any stage failed.</summary>
    public bool AnyFailed => Stages.Any(s => s.Result == ReadinessResult.Fail);

    /// <summary>Whether a requested stage was skipped only because the consent flag was missing.</summary>
    public bool AnySkippedForConsent => Stages.Any(s => s.SkippedForConsent);

    /// <summary><c>FAIL</c> when a stage failed, <c>PASS</c> when every requested stage passed, <c>INCOMPLETE</c> otherwise (a requested stage did not run).</summary>
    public string Verdict => AnyFailed ? "FAIL" : AllPassed ? "PASS" : "INCOMPLETE";

    /// <summary>The result of <paramref name="stage"/>, or null when the report does not list it.</summary>
    /// <param name="stage">The stage.</param>
    public ReadinessStageResult? Find(ReadinessStage stage) => Stages.FirstOrDefault(s => s.Stage == stage);
}

/// <summary>
/// What a readiness check runs and how. Every default is the safe one: no probe, no consent, stop at the first
/// failure, 120 s per stage, verbose console tracing on (the doctor's default).
/// </summary>
public sealed record ReadinessCheckOptions
{
    /// <summary>The default bound of one stage.</summary>
    public static readonly TimeSpan DefaultStageTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The stages to run; null means <see cref="ReadinessStage.Config"/>, <see cref="ReadinessStage.Auth"/>,
    /// <see cref="ReadinessStage.Discovery"/> and <see cref="ReadinessStage.Protocol"/>, plus
    /// <see cref="ReadinessStage.Probe"/> and <see cref="ReadinessStage.Census"/> when <see cref="Probe"/> is set.
    /// <see cref="ReadinessStage.Config"/> always runs (every other stage needs the config). A network stage that is
    /// not requested still happens implicitly inside a later one (a requested discovery acquires its own token).
    /// </summary>
    public IReadOnlySet<ReadinessStage>? Stages { get; init; }

    /// <summary>Adds the probe and the census to the default stage set (ignored when <see cref="Stages"/> is set).</summary>
    public bool Probe { get; init; }

    /// <summary>
    /// Consent that the probe and the census drive the LIVE agent as the signed-in user (the delegated permission
    /// covers reads and writes; a prompt can trigger real actions). Without it those stages are skipped and nothing
    /// is sent to the agent.
    /// </summary>
    public bool IUnderstandLiveSideEffects { get; init; }

    /// <summary>Runs the remaining stages after a failure instead of stopping (a stage that needs the config still cannot run without it).</summary>
    public bool ContinueAfterFailure { get; init; }

    /// <summary>
    /// The most one stage may take for its network calls (also the HTTP timeout and the turn deadline of the probe); must
    /// be positive. A device-code sign-in that starts inside a stage is bounded by its code's own expiry instead (the
    /// stage's wait is extended to it and the trace says so), so a person completing MFA is not cut off by a network bound.
    /// </summary>
    public TimeSpan StageTimeout { get; init; } = DefaultStageTimeout;

    /// <summary>Overrides the config's diagnostic profile for the auth, discovery, protocol and probe stages; null keeps the config's.</summary>
    public DiagnosticProfile? Profile { get; init; }

    /// <summary>Retains the raw A2A exchanges under this folder (content policy <see cref="EvidenceContentPolicy.FullEvidence"/>; may contain tenant data).</summary>
    public DirectoryInfo? RawCaptureDirectory { get; init; }

    /// <summary>Writes every trace event to <see cref="Console"/> as it happens (default on for the doctor).</summary>
    public bool Verbose { get; init; } = true;

    /// <summary>Where the JSONL trace goes; null puts <c>m365-trace-&lt;runId&gt;.jsonl</c> in <see cref="DiagnosticsDirectory"/>.</summary>
    public FileInfo? TraceFile { get; init; }

    /// <summary>The folder of the trace and issue files; null resolves to the workspace's <c>.agenteval/m365/</c>, else <c>&lt;temp&gt;/agenteval-m365</c>.</summary>
    public DirectoryInfo? DiagnosticsDirectory { get; init; }

    /// <summary>Where the device-code sign-in message and the verbose trace go; null means <see cref="System.Console.Error"/>.</summary>
    public TextWriter? Console { get; init; }

    /// <summary>
    /// Extra trace sinks, next to the JSONL trace file and the verbose console. Each receives every event the JSONL
    /// trace file receives (the stages' <c>doctor.*</c> events and every seam's own), in the same order and after the
    /// same redaction. The caller owns their lifetime: the check never disposes them. A sink that throws is reported
    /// once on stderr and dropped. Null or empty means none.
    /// </summary>
    public IReadOnlyList<IWorkIqTraceSink>? TraceSinks { get; init; }

    /// <summary>
    /// Extra issue sinks, next to the JSONL issue register. Each receives every issue the issue file receives — the
    /// two replies the doctor provokes on purpose (TaskNotFound on the random task id, TaskNotCancelable on the finished
    /// task) are held back from them exactly as from the file. The caller owns their lifetime (never disposed by the
    /// check). Null or empty means none.
    /// </summary>
    public IReadOnlyList<IWorkIqIssueSink>? IssueSinks { get; init; }

    /// <summary>Throws when <see cref="StageTimeout"/> is not positive, or a sink list has a null entry.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="StageTimeout"/> is zero or negative.</exception>
    /// <exception cref="ArgumentException"><see cref="TraceSinks"/> or <see cref="IssueSinks"/> contains null.</exception>
    public void Validate()
    {
        if (StageTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StageTimeout), StageTimeout, "The stage timeout must be positive.");
        }

        if (TraceSinks?.Any(s => s is null) == true)
        {
            throw new ArgumentException("TraceSinks must not contain a null entry.", nameof(TraceSinks));
        }

        if (IssueSinks?.Any(s => s is null) == true)
        {
            throw new ArgumentException("IssueSinks must not contain a null entry.", nameof(IssueSinks));
        }
    }

    /// <summary>The stages that run: <see cref="Stages"/> or the default set, always with <see cref="ReadinessStage.Config"/>.</summary>
    internal IReadOnlySet<ReadinessStage> ResolveStages()
    {
        var set = new HashSet<ReadinessStage>();
        if (Stages is null)
        {
            set.UnionWith([ReadinessStage.Config, ReadinessStage.Auth, ReadinessStage.Discovery, ReadinessStage.Protocol]);
            if (Probe)
            {
                set.UnionWith([ReadinessStage.Probe, ReadinessStage.Census]);
            }
        }
        else
        {
            set.UnionWith(Stages);
        }

        set.Add(ReadinessStage.Config);
        return set;
    }
}

/// <summary>The lower-case display names of the stages and verdicts.</summary>
internal static class ReadinessNames
{
    /// <summary>Every stage in run order.</summary>
    public static readonly ReadinessStage[] AllStages =
    [
        ReadinessStage.Config, ReadinessStage.Auth, ReadinessStage.Discovery,
        ReadinessStage.Protocol, ReadinessStage.Probe, ReadinessStage.Census,
    ];

    /// <summary>The stage name as the CLI spells it (<c>config</c>, <c>auth</c>, …).</summary>
    /// <param name="stage">The stage.</param>
    public static string Of(ReadinessStage stage) => stage switch
    {
        ReadinessStage.Config => "config",
        ReadinessStage.Auth => "auth",
        ReadinessStage.Discovery => "discovery",
        ReadinessStage.Protocol => "protocol",
        ReadinessStage.Probe => "probe",
        ReadinessStage.Census => "census",
        _ => stage.ToString().ToLowerInvariant(),
    };

    /// <summary>The verdict as printed (<c>PASS</c>, <c>FAIL</c>, <c>SKIPPED</c>).</summary>
    /// <param name="result">The verdict.</param>
    public static string Of(ReadinessResult result) => result switch
    {
        ReadinessResult.Pass => "PASS",
        ReadinessResult.Fail => "FAIL",
        _ => "SKIPPED",
    };

    /// <summary>The 1-based position of a stage in the run order.</summary>
    /// <param name="stage">The stage.</param>
    public static int Ordinal(ReadinessStage stage) => Array.IndexOf(AllStages, stage) + 1;
}
