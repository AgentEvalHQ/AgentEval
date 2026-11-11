// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.RedTeam;

internal static class ProbeJudgeProvenance
{
    internal static EvaluationResult Retain(EvaluationResult aggregate, IEnumerable<EvaluationResult> children)
    {
        var instruments = children.SelectMany(c => c.JudgeInstruments ?? []).ToArray();
        // An executed leaf list is not the identity of a whole graph. Do not manufacture an aggregate
        // RubricDigest from just the leaves that happened to run (AND gates may short-circuit).
        return instruments.Length == 0 ? aggregate : aggregate with { JudgeInstruments = Array.AsReadOnly(instruments) };
    }

    internal static IReadOnlyList<ProbeJudgeInstrument>? WithoutContent(IReadOnlyList<ProbeJudgeInstrument>? instruments) =>
        instruments is null ? null : Array.AsReadOnly(instruments.Select(i => i with { Prompt = i.Prompt with { Content = null } }).ToArray());
}
