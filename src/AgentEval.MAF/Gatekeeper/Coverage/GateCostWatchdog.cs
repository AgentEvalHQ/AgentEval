// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>One gate whose measured hot-path latency persistently exceeds the ceiling for its declared <see cref="GateCost"/> class (Phase 5, P5-8).</summary>
/// <param name="PolicyName">The offending gate's policy name.</param>
/// <param name="DeclaredCost">The <see cref="GateCost"/> class it declared.</param>
/// <param name="MeasuredAverage">Its measured mean per-invocation latency (from <see cref="GateTelemetry"/>).</param>
/// <param name="Ceiling">The ceiling for its declared class.</param>
/// <param name="Invocations">How many invocations the measurement is over.</param>
public sealed record GateCostViolation(
    string PolicyName,
    GateCost DeclaredCost,
    TimeSpan MeasuredAverage,
    TimeSpan Ceiling,
    long Invocations);

/// <summary>
/// Compares each gate's measured latency (from <see cref="GateTelemetry"/>) to the ceiling for its declared
/// <see cref="GateCost"/> class (Phase 5, P5-8) — a gate that declares <see cref="GateCost.PureCode"/> but
/// persistently runs like a network call is a mis-declared cost contract that silently adds latency to the
/// tool-invocation hot path. Offline / periodic (it reads accumulated telemetry, never the hot path itself), so a
/// caller runs it after enough traffic and emits a <c>gate.warning.*.CostContractViolation</c> for each finding.
/// </summary>
public static class GateCostWatchdog
{
    /// <summary>The mean-latency ceiling for a cost class. Network/Llm have none (they never run inline).</summary>
    public static TimeSpan CeilingFor(GateCost cost) => cost switch
    {
        GateCost.PureCode => TimeSpan.FromMilliseconds(10),
        GateCost.Bounded => TimeSpan.FromMilliseconds(500),   // above the 300ms ReDoS-timeout ceiling, with headroom
        _ => TimeSpan.MaxValue,   // Network / Llm are rejected inline anyway — no inline latency contract to check
    };

    /// <summary>
    /// Returns the gates whose mean latency exceeds their class ceiling, considering only gates with at least
    /// <paramref name="minInvocations"/> samples (so a single slow cold-start can't trip the "persistent" check).
    /// </summary>
    public static IReadOnlyList<GateCostViolation> CheckViolations(
        IEnumerable<IToolGate> gates, GateTelemetry telemetry, int minInvocations = 20)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (minInvocations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minInvocations), "must be at least 1.");
        }

        var snapshots = telemetry.Snapshot().ToDictionary(s => s.PolicyName, StringComparer.Ordinal);
        var violations = new List<GateCostViolation>();

        foreach (var gate in gates)
        {
            if (!snapshots.TryGetValue(gate.PolicyName, out var snapshot) || snapshot.InvocationCount < minInvocations)
            {
                continue;   // not enough samples to call it a persistent violation
            }

            var ceiling = CeilingFor(gate.Cost);
            if (snapshot.AverageElapsed > ceiling)
            {
                violations.Add(new GateCostViolation(gate.PolicyName, gate.Cost, snapshot.AverageElapsed, ceiling, snapshot.InvocationCount));
            }
        }

        return violations;
    }
}
