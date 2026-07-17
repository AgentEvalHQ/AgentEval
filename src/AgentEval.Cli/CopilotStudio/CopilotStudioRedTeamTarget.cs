// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Core;
using AgentEval.Guardrails;
using AgentEval.MAF.CopilotStudio;
using AgentEval.RedTeam;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>Parsed <c>--sut copilot-studio</c> options (carried on <c>RedTeamOptions.TargetOptions["copilot-studio"]</c>).</summary>
internal sealed record CopilotStudioTargetOptions : IRedTeamTargetOptions
{
    /// <summary><c>--copilotstudio-config</c>: the MCS connection JSON.</summary>
    public FileInfo? ConfigFile { get; init; }

    /// <summary><c>--i-understand-live-side-effects</c>: consent to scan a LIVE agent that can fire real actions.</summary>
    public bool AckLiveSideEffects { get; init; }

    /// <summary><c>--max-credits</c>: Copilot Credit spend cap (0 = no cap). Enforced during a live scan.</summary>
    public int MaxCredits { get; init; }

    /// <summary>
    /// <c>--copilotstudio-save-config-baseline</c>: P6 item A. After the config resolves, write a trust-time
    /// agent-identity fingerprint pin to this path (opt-in, always overwrites — mirrors <c>--save-baseline</c>'s
    /// own "not silent-by-default, no if-missing conditional" convention). Redteam-only (not part of the shared
    /// eval/bench <c>--sut</c> seam).
    /// </summary>
    public FileInfo? SaveConfigBaseline { get; init; }

    /// <summary>
    /// <c>--copilotstudio-config-baseline</c>: P6 item A. Compare the resolved config's agent-identity
    /// fingerprint against this pinned baseline file; warns (default) or hard-stops
    /// (<see cref="FailOnConfigDrift"/>) on drift. Primary use case: a stale/wrong-agent
    /// <c>redteam --baseline</c> RESULT comparison is meaningless if the underlying agent's identity changed
    /// since that baseline was captured — this is a data-integrity safeguard, not a security gate.
    /// </summary>
    public FileInfo? ConfigBaseline { get; init; }

    /// <summary><c>--fail-on-config-drift</c>: the opt-in hard-stop variant of <see cref="ConfigBaseline"/>'s drift check (default: warn, never block).</summary>
    public bool FailOnConfigDrift { get; init; }
}

/// <summary>
/// Parsed <c>--sut copilot-studio</c> options under the shared <c>eval</c>/<c>bench</c> seam (Track 2) —
/// mirrors <see cref="CopilotStudioTargetOptions"/>'s 3 fields exactly. Deliberately duplicated rather than
/// shared: the two option TYPES (<see cref="IRedTeamTargetOptions"/> vs <see cref="ISutTargetOptions"/>)
/// are intentionally distinct marker interfaces so `eval`/`bench`'s option shape never implicitly couples
/// to redteam's — see the design doc's "accepted, deliberate duplication" note.
/// </summary>
internal sealed record CopilotStudioSutOptions : ISutTargetOptions
{
    public FileInfo? ConfigFile { get; init; }
    public bool AckLiveSideEffects { get; init; }
    public int MaxCredits { get; init; }
}

/// <summary>
/// The <c>--sut copilot-studio</c> built-in red-team target: red-teams a LIVE Microsoft Copilot Studio agent at
/// text-only / <c>Verbal</c> fidelity, behind a ship-blocking safety gate. Owns the CS-specific CLI options,
/// validation, and construction — composing the reusable <see cref="CopilotStudioAgentFactory"/> — so none of it
/// bloats <c>RedTeamCommand</c>.
/// </summary>
/// <remarks>
/// <see cref="CopilotStudioAgentFactory.BuildLive"/> now wires a real connector (see its own XML doc); the whole
/// path up to and including this method's gates is still testable credential-free via the <c>sutOverride</c> seam.
/// </remarks>
internal sealed class CopilotStudioRedTeamTarget : IRedTeamBuiltInTarget, ISutTarget
{
    private readonly Option<FileInfo?> _configOpt = new("--copilotstudio-config")
    {
        Description = "JSON file with the Copilot Studio connection (environmentId, schemaName, tenantId, appClientId; optional cloud, agentName). No secret is stored here; the token is acquired at run time via MSAL device-code auth + a persisted cache. Required by --sut copilot-studio.",
    };

    private readonly Option<bool> _ackOpt = new("--i-understand-live-side-effects")
    {
        Description = "Required consent for --sut copilot-studio: acknowledge that scanning a LIVE agent can fire real connector/flow actions. Use a NON-PROD agent. Without this the command refuses before any network call.",
    };

    private readonly Option<int> _maxCreditsOpt = new("--max-credits")
    {
        DefaultValueFactory = _ => 0,
        Description = "Cap the Copilot Credits a live --sut copilot-studio scan may spend (0 = no cap). NOT YET ENFORCED — the SDK exposes no credit-cost field to enforce against, so this is parsed/validated only; exit 8 (BudgetExceeded) stays reserved. Every turn burns credits, and a reasoning turn costs substantially more than a scripted one.",
    };

    // P6 item A (config-fingerprint drift) — redteam-ONLY, registered by AddOptionsTo (IRedTeamBuiltInTarget)
    // but deliberately NOT by the shared ISutTarget.AddOptionsTo (eval/bench don't get these flags).
    private readonly Option<FileInfo?> _saveConfigBaselineOpt = new("--copilotstudio-save-config-baseline")
    {
        Description = "After the Copilot Studio config resolves, write a trust-time agent-identity fingerprint pin (JSON) to this path. Opt-in; always overwrites (mirrors --save-baseline's convention).",
    };

    private readonly Option<FileInfo?> _configBaselineOpt = new("--copilotstudio-config-baseline")
    {
        Description = "Compare the resolved Copilot Studio config's agent-identity fingerprint against this pinned baseline file (from --copilotstudio-save-config-baseline). Drift WARNS by default (a stale/wrong-agent --baseline RESULT comparison would be meaningless); pass --fail-on-config-drift to hard-stop instead.",
    };

    private readonly Option<bool> _failOnConfigDriftOpt = new("--fail-on-config-drift")
    {
        Description = "With --copilotstudio-config-baseline: treat a detected agent-identity drift as a hard stop (refuse before any network call) instead of a warning.",
    };

    private CopilotStudioConfig? _config;   // memoized within one ExecuteAsync call (Validate -> ResolvedName -> Build)

    public string Sut => "copilot-studio";

    /// <summary>Live MCS responses can carry real PII, so raw evidence is never written to a report/log.</summary>
    public bool IncludeEvidence => false;

    public void AddOptionsTo(Command command)
    {
        AddCommonOptionsTo(command);
        // P6 item A: redteam-only — NOT added by ISutTarget.AddOptionsTo (see that explicit override below).
        command.Options.Add(_saveConfigBaselineOpt);
        command.Options.Add(_configBaselineOpt);
        command.Options.Add(_failOnConfigDriftOpt);
    }

    private void AddCommonOptionsTo(Command command)
    {
        command.Options.Add(_configOpt);
        command.Options.Add(_ackOpt);
        command.Options.Add(_maxCreditsOpt);
    }

    /// <summary>Bind the CS options from the parse result (called by RedTeamCommand for every built-in target, unconditionally).</summary>
    public IRedTeamTargetOptions? BindOptions(ParseResult parseResult) => new CopilotStudioTargetOptions
    {
        ConfigFile = parseResult.GetValue(_configOpt),
        AckLiveSideEffects = parseResult.GetValue(_ackOpt),
        MaxCredits = parseResult.GetValue(_maxCreditsOpt),
        SaveConfigBaseline = parseResult.GetValue(_saveConfigBaselineOpt),
        ConfigBaseline = parseResult.GetValue(_configBaselineOpt),
        FailOnConfigDrift = parseResult.GetValue(_failOnConfigDriftOpt),
    };

    public void Validate(RedTeamOptions opts)
    {
        var cs = opts.TargetOptionsFor<CopilotStudioTargetOptions>(Sut) ?? new CopilotStudioTargetOptions();

        // Consent gate — refuse BEFORE any build/network call.
        if (!cs.AckLiveSideEffects)
        {
            throw new InvalidOperationException(
                "--sut copilot-studio drives a LIVE Copilot Studio agent whose connectors/flows can fire REAL " +
                "production actions and cannot be sandboxed. Re-run against a NON-PROD agent with " +
                "--i-understand-live-side-effects to proceed (nothing was sent).");
        }

        if (cs.ConfigFile is null)
        {
            throw new InvalidOperationException(
                "--sut copilot-studio requires --copilotstudio-config <file.json> (environmentId, schemaName, tenantId, appClientId).");
        }

        if (cs.MaxCredits < 0)
        {
            throw new InvalidOperationException("--max-credits must be >= 0 (0 = no cap).");
        }

        // A live MCS session is stateful/non-reentrant — concurrent probes would race it.
        if (opts.Parallelism > 1)
        {
            throw new InvalidOperationException(
                "--sut copilot-studio runs at --parallelism 1 (a live Copilot Studio session is stateful/non-reentrant).");
        }

        // No model of its own — a judge/attacker must name its own model rather than fall back to the SUT name.
        if (opts.JudgeEndpoint is not null && string.IsNullOrWhiteSpace(opts.JudgeModel))
        {
            throw new InvalidOperationException(
                "--sut copilot-studio has no model of its own; pass --judge-model <name> when using --judge.");
        }

        if (opts.AttackerEndpoint is not null && string.IsNullOrWhiteSpace(opts.AttackerModel))
        {
            throw new InvalidOperationException(
                "--sut copilot-studio has no model of its own; pass --attacker-model <name> when using --attacker.");
        }

        _config = CopilotStudioConfig.Load(cs.ConfigFile);   // fail fast on a bad config, before the scan; memoized

        // P6 item A: config-fingerprint drift — opt-in, redteam-only. Both still run before any network call.
        if (cs.SaveConfigBaseline is not null)
        {
            var baseline = CopilotStudioConfigBaseline.Capture(_config, notes: $"captured {DateTimeOffset.UtcNow:u}");
            File.WriteAllText(cs.SaveConfigBaseline.FullName, baseline.ToJson());
            if (!opts.Quiet)
            {
                Console.Error.WriteLine($"  Copilot Studio config baseline written to: {cs.SaveConfigBaseline.FullName}");
            }
        }

        if (cs.ConfigBaseline is not null)
        {
            if (!cs.ConfigBaseline.Exists)
            {
                throw new InvalidOperationException($"Copilot Studio config baseline file not found: {cs.ConfigBaseline.FullName}");
            }

            var pinned = CopilotStudioConfigBaseline.FromJson(File.ReadAllText(cs.ConfigBaseline.FullName));
            var drift = CopilotStudioConfigFingerprint.CheckDrift(_config, pinned)
                .Where(f => f.Kind != ManifestDriftKind.Unchanged)
                .ToList();

            if (drift.Count > 0)
            {
                var summary = string.Join("; ", drift.Select(f => $"{f.Key}: {f.Kind}"));
                var message = $"Copilot Studio config agent-identity drifted from the pinned baseline ({summary}). " +
                    "An agent's environmentId/schemaName/cloud changing is very likely intentional (pointing at a " +
                    "different/updated agent on purpose) — this is a data-integrity safeguard for --baseline RESULT " +
                    "comparisons, not a security gate. Re-run with --copilotstudio-save-config-baseline to re-pin " +
                    "the new identity, or without --fail-on-config-drift to proceed with just a warning.";

                if (cs.FailOnConfigDrift)
                {
                    throw new InvalidOperationException("--fail-on-config-drift: " + message);
                }

                if (!opts.Quiet)
                {
                    Console.Error.WriteLine("  Warning: " + message);
                }
            }
        }

        if (!opts.Quiet && (opts.Endpoint is not null || opts.Azure || opts.Model is not null || opts.DeploymentName is not null
            || !string.Equals(opts.SutTier, "text", StringComparison.OrdinalIgnoreCase)
            || opts.SystemPrompt is not null || opts.SystemPromptCanary is not null))
        {
            Console.Error.WriteLine(
                "  Note: --sut copilot-studio is a live built-in target at text-only/Verbal fidelity; " +
                "--endpoint/--azure/--model/--deployment-name/--sut-tier/--system-prompt/--system-prompt-canary are ignored.");
        }
    }

    public string ResolvedName(RedTeamOptions opts) => EnsureConfig(opts).DisplayName;

    public IEvaluableAgent Build(RedTeamOptions opts, IEvaluableAgent? sutOverride, AgentTrace trace)
    {
        if (sutOverride is not null)
        {
            return sutOverride;   // test seam: a pre-built (fake) SUT drives the whole scan credential-free
        }

        // Live path: builds a real connector (see BuildLive's own XML doc for what's live-verified vs. not).
        return CopilotStudioAgentFactory.BuildLive(EnsureConfig(opts));
    }

    public void WritePostScanSummary(RedTeamResult result, AgentTrace trace, TextWriter err)
    {
        // No gate trace for a live conversational target — nothing to render there.
        //
        // P6 item C1 (narrow cut — strategy/CopilotStudio/Copilot-Studio-P6-Connector-Health-and-Resilience-Design.md
        // §1C): EvidenceFidelity is already stamped per-verdict on every ProbeResult (RC-1), but that's easy to
        // miss unless a caller inspects the raw report. This surfaces the AGGREGATE fidelity breakdown once, in
        // THIS target's own summary line — not a change to the shared RedTeam report renderers (that's C2, a
        // separate, larger, all-targets change explicitly out of scope here). --sut copilot-studio caps at
        // text-only fidelity: it never observes whether a live MCS agent's connectors/flows actually fired, only
        // what the agent's reply SAID — so a "Succeeded" verdict here is proof of what was SAID, never of what
        // was DONE.
        var byFidelity = result.AttackResults
            .SelectMany(a => a.ProbeResults)
            .GroupBy(p => p.Fidelity)
            .ToDictionary(g => g.Key, g => g.Count());

        var total = byFidelity.Values.Sum();
        if (total == 0)
        {
            return;
        }

        var verbal = byFidelity.GetValueOrDefault(EvidenceFidelity.Verbal);
        var intentToAct = byFidelity.GetValueOrDefault(EvidenceFidelity.IntentToAct);
        var behavioral = byFidelity.GetValueOrDefault(EvidenceFidelity.Behavioral);

        err.WriteLine(
            $"  Evidence fidelity (--sut copilot-studio is text-only — a live agent's real connector/flow " +
            $"actions are not observable): Verbal {verbal}/{total} · IntentToAct {intentToAct}/{total} · " +
            $"Behavioral {behavioral}/{total}.");
    }

    private CopilotStudioConfig EnsureConfig(RedTeamOptions opts)
    {
        if (_config is not null)
        {
            return _config;
        }

        var configFile = opts.TargetOptionsFor<CopilotStudioTargetOptions>(Sut)?.ConfigFile
            ?? throw new InvalidOperationException("--sut copilot-studio requires --copilotstudio-config <file.json>.");
        return _config = EnsureConfigFromFile(configFile);
    }

    private CopilotStudioConfig EnsureConfigFromFile(FileInfo configFile) => CopilotStudioConfig.Load(configFile);

    // ────────────────────────────────────────────────────────────────────────────────────────────
    // ISutTarget — Track 2 (Bench-Eval-Integration-and-Live-Connector-Plan.md §3): the shared --sut
    // seam reachable from `eval`/`bench`, via EXPLICIT interface implementation so this class's
    // IRedTeamBuiltInTarget members above stay byte-for-byte unchanged. Deliberately OMITS the
    // redteam-only checks (Parallelism > 1, judge/attacker-model-required) — those concepts don't
    // exist in eval's/bench's option shapes today.
    // ────────────────────────────────────────────────────────────────────────────────────────────

    private CopilotStudioConfig? _sutConfig;   // separate memoization slot from _config (IRedTeamBuiltInTarget's),
                                                // since a single instance is never driven through both interfaces
                                                // in practice (SutTargetResolver.BuiltInTargets() and
                                                // RedTeamCommand.BuildBuiltInTargets() each mint fresh instances),
                                                // but keeping the slots separate avoids any accidental cross-talk.

    IReadOnlySet<string> ISutTarget.SupportedVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eval", "bench" };

    // P6 item A's config-baseline options are redteam-ONLY (design doc's explicit scope) — this calls
    // AddCommonOptionsTo, NOT the public AddOptionsTo (which also registers those 3 extra flags for redteam).
    void ISutTarget.AddOptionsTo(Command command) => AddCommonOptionsTo(command);

    ISutTargetOptions? ISutTarget.BindOptions(ParseResult parseResult) => new CopilotStudioSutOptions
    {
        ConfigFile = parseResult.GetValue(_configOpt),
        AckLiveSideEffects = parseResult.GetValue(_ackOpt),
        MaxCredits = parseResult.GetValue(_maxCreditsOpt),
    };

    void ISutTarget.Validate(CommonTargetOptions common, ISutTargetOptions? own)
    {
        var cs = own as CopilotStudioSutOptions ?? new CopilotStudioSutOptions();

        if (!cs.AckLiveSideEffects)
        {
            throw new InvalidOperationException(
                "--sut copilot-studio drives a LIVE Copilot Studio agent whose connectors/flows can fire REAL " +
                "production actions and cannot be sandboxed. Re-run against a NON-PROD agent with " +
                "--i-understand-live-side-effects to proceed (nothing was sent).");
        }

        if (cs.ConfigFile is null)
        {
            throw new InvalidOperationException(
                "--sut copilot-studio requires --copilotstudio-config <file.json> (environmentId, schemaName, tenantId, appClientId).");
        }

        if (cs.MaxCredits < 0)
        {
            throw new InvalidOperationException("--max-credits must be >= 0 (0 = no cap).");
        }

        _sutConfig = EnsureConfigFromFile(cs.ConfigFile);   // fail fast on a bad config, before the run; memoized
    }

    string ISutTarget.ResolvedName(CommonTargetOptions common, ISutTargetOptions? own) => EnsureSutConfig(common, own).DisplayName;

    IEvaluableAgent ISutTarget.Build(CommonTargetOptions common, ISutTargetOptions? own, IEvaluableAgent? sutOverride)
        => sutOverride ?? CopilotStudioAgentFactory.BuildLive(EnsureSutConfig(common, own));

    private CopilotStudioConfig EnsureSutConfig(CommonTargetOptions common, ISutTargetOptions? own)
    {
        if (_sutConfig is not null)
        {
            return _sutConfig;
        }

        var configFile = (own as CopilotStudioSutOptions)?.ConfigFile
            ?? common.TargetOptionsFor<CopilotStudioSutOptions>(Sut)?.ConfigFile
            ?? throw new InvalidOperationException("--sut copilot-studio requires --copilotstudio-config <file.json>.");
        return _sutConfig = EnsureConfigFromFile(configFile);
    }
}
