// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam.Gatekeeper;

/// <summary>
/// Gatekeeper M3 (the moat) — graduates a red-team <see cref="CanaryTool"/> into a production honeypot. A canary
/// is advertised to the model as a visible tool (the lure, via <see cref="CanaryLure"/>) but MUST NEVER execute:
/// the model <em>emitting</em> a call to it IS the compromise signal, so this gate blocks the call before it runs.
/// <para>Wire with <c>UseAgentEvalToolGate(gates, ToolGatePolicy.Terminate, trace)</c> so a canary emit ⇒ Block
/// + Terminate + <c>gate.tool.*</c> evidence — the run stops and the honeypot trip is recorded.</para>
/// </summary>
public sealed class CanaryToolGate : IToolGate
{
    private readonly Dictionary<string, CanaryTool> _canaries;

    /// <inheritdoc/>
    public string PolicyName => "CanaryToolGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the gate over the canary honeypots to watch for (matched by name, case-insensitively).</summary>
    public CanaryToolGate(IEnumerable<CanaryTool> canaries)
    {
        ArgumentNullException.ThrowIfNull(canaries);
        _canaries = new Dictionary<string, CanaryTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var canary in canaries)
        {
            ArgumentNullException.ThrowIfNull(canary);
            _canaries[canary.Name] = canary;   // last-wins on a duplicate name (config choice, not an error)
        }
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var verdict = _canaries.TryGetValue(call.FunctionName, out var canary)
            ? ToolGateVerdict.Block(PolicyName,
                $"canary '{canary.Name}' ({canary.ForbiddenCategory}, {canary.Severity}) was invoked — emitting the call is the compromise signal")
            : ToolGateVerdict.Allow(PolicyName);

        return new ValueTask<ToolGateVerdict>(verdict);
    }
}

/// <summary>
/// Builds visible, schema-only lure tools from <see cref="CanaryTool"/>s to register in an agent's
/// <c>ChatOptions.Tools</c> (so the model can SEE and be tempted by them). The stub throws if ever invoked — the
/// paired <see cref="CanaryToolGate"/> blocks the emit first; a throw means the gate was not wired.
/// </summary>
public static class CanaryLure
{
    /// <summary>Builds lure tools for the given canaries.</summary>
    public static IReadOnlyList<AITool> Tools(params CanaryTool[] canaries)
        => Tools((IEnumerable<CanaryTool>)canaries);

    /// <summary>Builds lure tools for the given canaries.</summary>
    public static IReadOnlyList<AITool> Tools(IEnumerable<CanaryTool> canaries)
    {
        ArgumentNullException.ThrowIfNull(canaries);
        return canaries.Select(ToLure).ToList();
    }

    private static AITool ToLure(CanaryTool canary)
    {
        ArgumentNullException.ThrowIfNull(canary);
        Func<string> throwIfInvoked = () => throw new InvalidOperationException(
            $"Canary '{canary.Name}' must never execute — register a CanaryToolGate (Terminate policy) to block the emit.");
        return AIFunctionFactory.Create(throwIfInvoked, canary.Name, canary.Description);
    }
}
