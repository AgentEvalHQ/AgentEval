// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.RedTeam;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Cli.Commands.RedTeamTargets;

/// <summary>The <c>--sut gatekeeper-demo</c> flags.</summary>
/// <param name="Scripted">Run the scripted, fully compromised model even when a provider is configured.</param>
internal sealed record GatekeeperDemoTargetOptions(bool Scripted) : IRedTeamTargetOptions;

/// <summary>
/// The <c>--sut gatekeeper-demo</c> built-in target: a Gatekeeper-gated agent (built by <see cref="GatekeeperDemoSut"/>)
/// that demonstrates the attack-the-gate closed loop. It runs on the configured provider's model; it falls back to the
/// scripted, fully compromised model when no provider is configured, or on request (<c>--scripted</c>, for a
/// deterministic CI baseline), and says so. Through 0.42 the scripted model was the only one.
/// </summary>
internal sealed class GatekeeperDemoRedTeamTarget : IRedTeamBuiltInTarget
{
    private readonly Option<bool> _scripted = new("--scripted")
    {
        Description = "With --sut gatekeeper-demo or --attacks memory-poisoning: run the scripted, fully compromised model " +
                      "instead of a real one. Deterministic and free, so its result is stable; it shows the gates, not a model.",
    };

    private (IChatClient? Client, string? Model, string? Why)? _model;

    public string Sut => "gatekeeper-demo";

    /// <summary>
    /// The model the demo runs on, resolved once and only for a run that selected this target: the configured
    /// provider's, unless the run asked for the scripted model. <c>Client</c> is null for the scripted model, and
    /// <c>Why</c> says why.
    /// </summary>
    private (IChatClient? Client, string? Model, string? Why) Model(RedTeamOptions opts)
    {
        if (_model is { } resolved)
            return resolved;
        if (opts.TargetOptionsFor<GatekeeperDemoTargetOptions>(Sut) is { Scripted: true })
        {
            _model = (null, null, "--scripted was given");
            return _model.Value;
        }

        var (client, model, diagnostic) = ProviderChatClientFactory.TryCreate("gatekeeper demo agent", generousTimeout: true);
        _model = (client, model, client is null ? diagnostic ?? "no inference provider is configured" : null);
        return _model.Value;
    }

    /// <summary>The demo records its own gate.tool.* evidence into the trace; that is the point of the demo.</summary>
    public bool IncludeEvidence => true;

    public void AddOptionsTo(Command command) => command.Add(_scripted);

    public IRedTeamTargetOptions? BindOptions(ParseResult parseResult) =>
        new GatekeeperDemoTargetOptions(parseResult.GetValue(_scripted));

    /// <summary>
    /// Names the model the scan red-teamed. The agent carries the same name, so it reaches the saved report and the
    /// baseline: a scripted run and a real one can never be mistaken for each other.
    /// </summary>
    public string ResolvedName(RedTeamOptions opts) => Model(opts) is { Client: not null, Model: { } model }
        ? $"gatekeeper-demo (real model {model}@{ProviderChatClientFactory.Settings.ProviderTag})"
        : "gatekeeper-demo (scripted)";

    public void Validate(RedTeamOptions opts)
    {
        // The built-in demo shares one gate trace across probes — concurrent probes would race it, so reject
        // --parallelism > 1 rather than silently corrupt the results.
        if (opts.Parallelism > 1)
        {
            throw new InvalidOperationException(
                "--sut gatekeeper-demo runs at --parallelism 1 (the built-in demo shares one gate trace). Remove --parallelism or set it to 1.");
        }

        // The demo's run name is not a model id, so a judge/attacker would fall back to a name the endpoint rejects.
        // Require an explicit model.
        if (opts.JudgeEndpoint is not null && string.IsNullOrWhiteSpace(opts.JudgeModel))
        {
            throw new InvalidOperationException(
                "--sut gatekeeper-demo names no judge model; pass --judge-model <name> when using --judge.");
        }

        if (opts.AttackerEndpoint is not null && string.IsNullOrWhiteSpace(opts.AttackerModel))
        {
            throw new InvalidOperationException(
                "--sut gatekeeper-demo names no attacker model; pass --attacker-model <name> when using --attacker.");
        }

        // Flags that only apply to a real endpoint are ignored for the demo — say so rather than mislead.
        if (!opts.Quiet && (opts.Endpoint is not null || opts.Azure || opts.Model is not null
            || opts.DeploymentName is not null || !string.Equals(opts.SutTier, "text", StringComparison.OrdinalIgnoreCase)
            || opts.SystemPrompt is not null || opts.SystemPromptCanary is not null))
        {
            Console.Error.WriteLine(
                "  Note: --sut gatekeeper-demo is a built-in agent on the configured provider's model; --endpoint/--azure/" +
                "--model/--deployment-name/--sut-tier/--system-prompt/--system-prompt-canary are ignored.");
        }
    }

    public IEvaluableAgent Build(RedTeamOptions opts, IEvaluableAgent? sutOverride, AgentTrace trace)
    {
        if (sutOverride is not null)
            return sutOverride;

        var name = ResolvedName(opts);
        var (client, model, why) = Model(opts);
        if (client is null)
        {
            Console.Error.WriteLine(
                $"  SCRIPTED ({why}): the demo agent is a scripted, fully compromised model that calls the forbidden " +
                "tool on every turn. It shows the gate, not a model. Configure a provider (AI_INFERENCE_PROVIDER), " +
                "without --scripted, to red-team a real model behind the Gatekeeper.");
            return GatekeeperDemoSut.Build(trace, model: null, name);
        }

        Console.Error.WriteLine(
            $"  Gatekeeper demo: red-teaming the real model {model} behind the Gatekeeper, one fresh conversation per " +
            "probe; the forbidden tool is offered as a lure, and every call to it is blocked before it runs.");
        return GatekeeperDemoSut.Build(trace, client, name);
    }

    public void WritePostScanSummary(RedTeamResult result, AgentTrace trace, TextWriter err)
    {
        var blocks = AgentEval.Tracing.GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0;
        err.WriteLine($"  Gatekeeper: {blocks} forbidden tool call(s) blocked before execution (gate.tool.* evidence).");
    }
}
