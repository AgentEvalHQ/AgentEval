// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Guardrails.Judges;

internal static class JudgeGateInstrumentation
{
    internal static IEnumerable<JudgeGateInstrument> Observed(GateProvenance? provenance)
    {
        if (provenance is null) yield break;
        var pending = new Stack<GateProvenance>();
        var visited = new HashSet<GateProvenance>(ReferenceEqualityComparer.Instance);
        pending.Push(provenance);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current.JudgeInstrument is { } instrument) yield return instrument;
            foreach (var child in current.Contributing ?? [])
                if (child is not null) pending.Push(child);
        }
    }
}
