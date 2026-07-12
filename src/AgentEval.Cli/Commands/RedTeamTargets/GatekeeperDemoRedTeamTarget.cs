// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Cli.Commands.RedTeamTargets;

/// <summary>
/// The <c>--sut gatekeeper-demo</c> built-in target: a credential-free, deterministic Gatekeeper-gated agent (built by
/// <see cref="GatekeeperDemoSut"/>) that demonstrates the attack-the-gate closed loop with no API key. Ported verbatim
/// from the former inline <c>RedTeamCommand</c> branch — behaviour is unchanged.
/// </summary>
internal sealed class GatekeeperDemoRedTeamTarget : IRedTeamBuiltInTarget
{
    public string Sut => "gatekeeper-demo";

    /// <summary>The demo records its own gate.tool.* evidence into the trace; that is the point of the demo.</summary>
    public bool IncludeEvidence => true;

    public void AddOptionsTo(Command command) { /* no options of its own */ }

    /// <summary>No flags of its own — nothing to bind.</summary>
    public IRedTeamTargetOptions? BindOptions(ParseResult parseResult) => null;

    public string ResolvedName(RedTeamOptions opts) => "gatekeeper-demo";

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
                "  Note: --sut gatekeeper-demo is a built-in agent; --endpoint/--azure/--model/--deployment-name/" +
                "--sut-tier/--system-prompt/--system-prompt-canary are ignored.");
        }
    }

    public IEvaluableAgent Build(RedTeamOptions opts, IEvaluableAgent? sutOverride, AgentTrace trace)
        => sutOverride ?? GatekeeperDemoSut.Build(trace);

    public void WritePostScanSummary(RedTeamResult result, AgentTrace trace, TextWriter err)
    {
        var blocks = AgentEval.Tracing.GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0;
        err.WriteLine($"  Gatekeeper: {blocks} forbidden tool call(s) blocked before execution (gate.tool.* evidence).");
    }
}
