// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>Parsed <c>--sut copilot-studio</c> options (grouped onto <c>RedTeamOptions.CopilotStudio</c>).</summary>
internal sealed record CopilotStudioTargetOptions
{
    /// <summary><c>--copilotstudio-config</c>: the MCS connection JSON.</summary>
    public FileInfo? ConfigFile { get; init; }

    /// <summary><c>--i-understand-live-side-effects</c>: consent to scan a LIVE agent that can fire real actions.</summary>
    public bool AckLiveSideEffects { get; init; }

    /// <summary><c>--max-credits</c>: Copilot Credit spend cap (0 = no cap). Enforced during a live scan.</summary>
    public int MaxCredits { get; init; }
}

/// <summary>
/// The <c>--sut copilot-studio</c> built-in red-team target: red-teams a LIVE Microsoft Copilot Studio agent at
/// text-only / <c>Verbal</c> fidelity, behind a ship-blocking safety gate. Owns the CS-specific CLI options,
/// validation, and construction — composing the reusable <see cref="CopilotStudioAgentFactory"/> — so none of it
/// bloats <c>RedTeamCommand</c>.
/// </summary>
/// <remarks>
/// The live connector is deferred (<see cref="CopilotStudioAgentFactory.BuildLive"/> throws a clear error until the
/// P0 live spike). The whole path is testable credential-free via the <c>sutOverride</c> seam.
/// </remarks>
internal sealed class CopilotStudioRedTeamTarget : IRedTeamBuiltInTarget
{
    private readonly Option<FileInfo?> _configOpt = new("--copilotstudio-config")
    {
        Description = "JSON file with the Copilot Studio connection (environmentId, schemaName, tenantId, appClientId; optional cloud, agentName). No secret — the token is acquired at run time. Required by --sut copilot-studio.",
    };

    private readonly Option<bool> _ackOpt = new("--i-understand-live-side-effects")
    {
        Description = "Required consent for --sut copilot-studio: acknowledge that scanning a LIVE agent can fire real connector/flow actions. Use a NON-PROD agent. Without this the command refuses before any network call.",
    };

    private readonly Option<int> _maxCreditsOpt = new("--max-credits")
    {
        DefaultValueFactory = _ => 0,
        Description = "Cap the Copilot Credits a live --sut copilot-studio scan may spend (0 = no cap). Hitting the cap stops the scan with exit 8 (BudgetExceeded). Every turn burns credits, and a reasoning turn costs substantially more than a scripted one.",
    };

    private CopilotStudioConfig? _config;   // memoized within one ExecuteAsync call (Validate -> ResolvedName -> Build)

    public string Sut => "copilot-studio";

    /// <summary>Live MCS responses can carry real PII, so raw evidence is never written to a report/log.</summary>
    public bool IncludeEvidence => false;

    public void AddOptionsTo(Command command)
    {
        command.Options.Add(_configOpt);
        command.Options.Add(_ackOpt);
        command.Options.Add(_maxCreditsOpt);
    }

    /// <summary>Bind the CS options from the parse result (called by RedTeamCommand's SetAction, unconditionally).</summary>
    public CopilotStudioTargetOptions BindOptions(ParseResult parseResult) => new()
    {
        ConfigFile = parseResult.GetValue(_configOpt),
        AckLiveSideEffects = parseResult.GetValue(_ackOpt),
        MaxCredits = parseResult.GetValue(_maxCreditsOpt),
    };

    public void Validate(RedTeamOptions opts)
    {
        var cs = opts.CopilotStudio ?? new CopilotStudioTargetOptions();

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

        // Live path — deferred: BuildLive throws a clear error until the P0 live spike wires the verified connector.
        return CopilotStudioAgentFactory.BuildLive(EnsureConfig(opts));
    }

    public void WritePostScanSummary(RedTeamResult result, AgentTrace trace, TextWriter err)
    {
        // No gate trace for a live conversational target — fidelity is reported per-verdict by the evaluators.
    }

    private CopilotStudioConfig EnsureConfig(RedTeamOptions opts)
    {
        if (_config is not null)
        {
            return _config;
        }

        var configFile = opts.CopilotStudio?.ConfigFile
            ?? throw new InvalidOperationException("--sut copilot-studio requires --copilotstudio-config <file.json>.");
        return _config = CopilotStudioConfig.Load(configFile);
    }
}
