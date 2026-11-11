// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.RedTeam;

internal static class ProbeJudgeProvenance
{
    internal static IReadOnlyList<ProbeJudgeInstrument>? Observed(IEnumerable<EvaluationResult> results)
    {
        var instruments = results.SelectMany(r => r.JudgeInstruments ?? []).ToArray();
        return instruments.Length == 0 ? null : Array.AsReadOnly(instruments);
    }

    internal static string? CommonGraphDigest(IEnumerable<EvaluationResult> results)
    {
        var identities = results.Select(r => r.JudgeGraphDigest).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return identities.Length == 1 ? identities[0] : null;
    }

    internal static EvaluationResult Retain(EvaluationResult aggregate, IEnumerable<EvaluationResult> children)
    {
        var instruments = Observed(children);
        // An executed leaf list is not the identity of a whole graph. Do not manufacture an aggregate
        // RubricDigest from just the leaves that happened to run (AND gates may short-circuit).
        return instruments is null ? aggregate : aggregate with { JudgeInstruments = instruments };
    }

    internal static IReadOnlyList<ProbeJudgeInstrument>? WithoutContent(IReadOnlyList<ProbeJudgeInstrument>? instruments) =>
        instruments is null ? null : Array.AsReadOnly(instruments.Select(i => i with { Prompt = i.Prompt with { Content = null } }).ToArray());
}
