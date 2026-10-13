// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>
/// The decision already taken by a command, before its run is sealed ([GATE-1]). Recording it does not recompute
/// the command's policy from individual results: a regression gate can ship despite an unchanged failing case.
/// </summary>
public sealed record AefCommandGate
{
    /// <summary>The gate's typed reference.</summary>
    public required string GateId { get; init; }

    /// <summary>The command's decision rule.</summary>
    public required string Rule { get; init; }

    /// <summary>The outcome under that rule.</summary>
    public required AefGateOutcome Outcome { get; init; }

    /// <summary>The command's evaluation exit code, before an artifact write failure can override it.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Whether the comparison the gate needed was comparable.</summary>
    public AefComparability Comparability { get; init; } = AefComparability.NotApplicable;

    /// <summary>Additional decision facts, without content or local paths.</summary>
    public JsonObject? Ext { get; init; }

    internal void Write(AefRunWriter writer, string runId, DateTimeOffset endedAt, IReadOnlyList<string> results) =>
        writer.AddGate(new AefGateDecision
        {
            GateId = GateId,
            DecisionId = runId + "-gate",
            Rule = new AefGateRule(Rule),
            Inputs = new AefGateInputs { Results = results },
            Comparability = Comparability,
            Outcome = Outcome,
            ExitCode = ExitCode,
            DecidedAt = AefTime.FromDateTimeOffset(endedAt),
            Ext = Ext,
        });
}
