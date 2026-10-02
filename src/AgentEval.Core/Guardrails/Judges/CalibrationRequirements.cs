// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Finds every gate that requires calibration inside a registered gate, looking through wrappers
/// (<see cref="IDelegatingGate"/>) at any depth. A judge wrapped in a <see cref="JudgeVerdictCache"/> (what every
/// stock judge factory returns by default), seated on a <see cref="ParallelJudgeFanOut"/> panel, or held by an
/// approval gate is still the judge whose verdict counts, so it still has to prove its calibration.
/// </summary>
internal static class CalibrationRequirements
{
    /// <summary>
    /// Returns each distinct <see cref="IRequiresCalibration"/> reachable from <paramref name="gate"/>: the gate itself
    /// when it requires calibration, then the gates it delegates to, outer before inner, in the order each wrapper
    /// lists them. A null gate yields nothing. A gate reached more than once is reported once, so a wrapper cycle
    /// cannot loop.
    /// </summary>
    internal static IReadOnlyList<IRequiresCalibration> FindIn(object? gate)
    {
        var found = new List<IRequiresCalibration>();
        if (gate is not null)
        {
            Visit(gate, found, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        return found;
    }

    private static void Visit(object gate, List<IRequiresCalibration> found, HashSet<object> visited)
    {
        if (!visited.Add(gate))
        {
            return;
        }

        if (gate is IRequiresCalibration calibratable)
        {
            found.Add(calibratable);
        }

        if (gate is IDelegatingGate wrapper)
        {
            foreach (var inner in wrapper.InnerGates)
            {
                if (inner is not null)
                {
                    Visit(inner, found, visited);
                }
            }
        }
    }
}
