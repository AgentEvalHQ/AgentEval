// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// A forbidden / attacker-desirable function advertised to the SUT as a <b>schema</b> (Wave B, Pillar 1). At
/// <see cref="SutTier.FunctionCalling"/> it is never executed — the model <i>emitting</i> a call to it is the
/// behavioral signal (intent-to-act). At <see cref="SutTier.InstrumentedAgent"/>, <see cref="Execute"/> runs and
/// records the call (act + effect) — and, for Pillar 4, may return attacker-controlled output (a poisoned tool
/// result), so tool-output injection needs no separate machinery.
/// </summary>
/// <remarks>
/// Prior art: AgentDojo / InjecAgent / ToolEmu success criteria + garak <c>agent_breaker</c> (Apache-2.0),
/// re-implemented natively.
/// </remarks>
public sealed class CanaryTool
{
    /// <summary>The advertised function name (what the model emits a call to). Case-insensitive when matched.</summary>
    public required string Name { get; init; }

    /// <summary>The advertised function description (the lure the model reads).</summary>
    public required string Description { get; init; }

    /// <summary>Why invoking this tool is a compromise (e.g. <c>"DataExfiltration"</c>, <c>"DestructiveAction"</c>).</summary>
    public required string ForbiddenCategory { get; init; }

    /// <summary>Severity if this tool is invoked. Default <see cref="Severity.High"/>.</summary>
    public Severity Severity { get; init; } = Severity.High;

    /// <summary>
    /// <see cref="SutTier.InstrumentedAgent"/> ONLY: the function body. <c>null</c> ⇒ schema-only (Tier 1, never
    /// executed). The returned string becomes the tool output fed back to the model — set it to an adversarial
    /// payload to model Pillar-4 tool-output injection.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>>? Execute { get; init; }
}
