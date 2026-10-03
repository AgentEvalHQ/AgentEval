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

/// <summary>
/// The <c>--sut gatekeeper-demo</c> built-in target: a Gatekeeper-gated agent (built by <see cref="GatekeeperDemoSut"/>)
/// that demonstrates the attack-the-gate closed loop. It runs on the configured provider's model; only when no
/// provider is configured does it fall back to the scripted, fully compromised model, and it says so. Through 0.42 the
/// scripted model was the only one.
/// </summary>
internal sealed class GatekeeperDemoRedTeamTarget : IRedTeamBuiltInTarget
{
    private (IChatClient? Client, string? Model)? _model;

    public string Sut => "gatekeeper-demo";

    /// <summary>The configured provider's model, resolved once and only for a run that selected this target.</summary>
    private (IChatClient? Client, string? Model) Model()
    {
        if (_model is { } resolved)
            return resolved;
        var (client, model, _) = ProviderChatClientFactory.TryCreate("gatekeeper demo agent", generousTimeout: true);
        _model = (client, model);
        return _model.Value;
    }

    /// <summary>The demo records its own gate.tool.* evidence into the trace; that is the point of the demo.</summary>
    public bool IncludeEvidence => true;

    public void AddOptionsTo(Command command) { /* no options of its own */ }

    /// <summary>No flags of its own — nothing to bind.</summary>
    public IRedTeamTargetOptions? BindOptions(ParseResult parseResult) => null;

    /// <summary>Names the model the scan red-teamed, so a scripted run and a real one can never be mistaken for each other.</summary>
    public string ResolvedName(RedTeamOptions opts) => Model() is { Client: not null, Model: { } model }
        ? $"gatekeeper-demo (real model {model}@{ProviderChatClientFactory.Settings.ProviderTag})"
        : "gatekeeper-demo (scripted)";

    public void Validate(RedTeamOptions opts)
    {
        // The built-in demo agent is stateful and single-threaded — a shared session/trace would be raced under
        // concurrent probes, so reject --parallelism > 1 rather than silently corrupt the results.
        if (opts.Parallelism > 1)
        {
            throw new InvalidOperationException(
                "--sut gatekeeper-demo runs at --parallelism 1 (the built-in demo agent is stateful). Remove --parallelism or set it to 1.");
        }

        // The demo has no model of its own, so a judge/attacker would fall back to the literal name "gatekeeper-demo"
        // — a non-existent model the real endpoint rejects. Require an explicit model.
        if (opts.JudgeEndpoint is not null && string.IsNullOrWhiteSpace(opts.JudgeModel))
        {
            throw new InvalidOperationException(
                "--sut gatekeeper-demo has no model of its own; pass --judge-model <name> when using --judge.");
        }

        if (opts.AttackerEndpoint is not null && string.IsNullOrWhiteSpace(opts.AttackerModel))
        {
            throw new InvalidOperationException(
                "--sut gatekeeper-demo has no model of its own; pass --attacker-model <name> when using --attacker.");
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

        var (client, model) = Model();
        if (client is null)
        {
            Console.Error.WriteLine(
                "  SCRIPTED: no inference provider is configured, so the demo agent is a scripted, fully compromised " +
                "model that calls the forbidden tool on every turn. It shows the gate, not a model. Configure a " +
                "provider (AI_INFERENCE_PROVIDER) to red-team a real model behind the Gatekeeper.");
            return GatekeeperDemoSut.Build(trace);
        }

        Console.Error.WriteLine(
            $"  Gatekeeper demo: red-teaming the real model {model} behind the Gatekeeper; the forbidden tool is offered " +
            "as a lure, and every call to it is blocked before it runs.");
        return GatekeeperDemoSut.Build(trace, client);
    }

    public void WritePostScanSummary(RedTeamResult result, AgentTrace trace, TextWriter err)
    {
        var blocks = AgentEval.Tracing.GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0;
        err.WriteLine($"  Gatekeeper: {blocks} forbidden tool call(s) blocked before execution (gate.tool.* evidence).");
    }
}
