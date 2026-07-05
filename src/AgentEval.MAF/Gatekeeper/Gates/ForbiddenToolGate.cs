// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that blocks a live call to any tool on a deny-list (matched case-insensitively,
/// consistent with the repo's tool-name convention). The canonical M0 gate — the same object can later run as
/// a CI assertion and a red-team oracle.
/// </summary>
public sealed class ForbiddenToolGate : IToolGate
{
    private readonly HashSet<string> _forbidden;

    /// <inheritdoc/>
    public string PolicyName => "ForbiddenToolGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the gate over a deny-list of tool names.</summary>
    public ForbiddenToolGate(params string[] forbiddenNames)
    {
        ArgumentNullException.ThrowIfNull(forbiddenNames);
        _forbidden = new HashSet<string>(forbiddenNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var verdict = _forbidden.Contains(call.FunctionName)
            ? ToolGateVerdict.Block(PolicyName, $"tool '{call.FunctionName}' is on the forbidden list")
            : ToolGateVerdict.Allow(PolicyName);
        return new ValueTask<ToolGateVerdict>(verdict);
    }
}
