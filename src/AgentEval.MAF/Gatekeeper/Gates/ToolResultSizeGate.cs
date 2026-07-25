// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper Hardening Phase 2, P0-3 — a built-in <see cref="IToolResultGate"/> that truncates an
/// oversized tool result before it reaches the model. Bounds two real risks a single tool call can otherwise
/// hit unchecked: context-window exhaustion (a runaway API/file/page fetch silently consuming most of the
/// model's context budget) and cost — every character a tool returns is tokens the caller pays for on every
/// subsequent turn of the conversation, not just this one.
/// <para>Always <see cref="ToolResultAction.Redact"/>s on an oversized result (never <see cref="ToolResultAction.Block"/>s)
/// — a large legitimate result (e.g. a big but harmless CSV) is still USEFUL truncated; it is not the kind of
/// finding a "deny the whole call" verdict fits. Truncation is applied regardless of <see cref="ToolGatePolicy"/>,
/// mirroring <see cref="ToolGateAction.Mutate"/>'s "always applied" precedent used elsewhere in this folder.
/// </para>
/// </summary>
public sealed class ToolResultSizeGate : IToolResultGate
{
    /// <summary>The default maximum character length before truncation — a conservative ~2K-token budget at a rough 4-chars-per-token estimate.</summary>
    public const int DefaultMaxLength = 8_000;

    private readonly int _maxLength;

    /// <summary>Creates the gate with the default 8,000-character limit, or a caller-supplied one.</summary>
    public ToolResultSizeGate(int maxLength = DefaultMaxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "maxLength must be positive.");
        }

        _maxLength = maxLength;
    }

    /// <inheritdoc/>
    public string PolicyName => "tool-result-size-limit";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public ValueTask<ToolResultVerdict> InspectAsync(GatedToolResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = result.ResultText;
        if (text.Length <= _maxLength)
        {
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
        }

        var dropped = text.Length - _maxLength;
        var truncated = text[.._maxLength] + $"…[truncated {dropped} of {text.Length} characters by Gatekeeper's {PolicyName} gate]";
        var verdict = ToolResultVerdict.Redact(
            PolicyName, truncated, $"tool '{result.FunctionName}' result was {text.Length} characters, exceeding the {_maxLength}-character limit — truncated {dropped} characters");
        return new ValueTask<ToolResultVerdict>(verdict);
    }
}
