// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// The <c>--model-reply</c> path: evaluate a judge verdict from a model reply the caller already obtained — no
/// network call. Replicates <c>CompositeJudgeGate.InspectAsync</c> exactly, including its <c>SafePrefilter</c>
/// contract: empty text short-circuits to Allow, and a <i>throwing</i> prefilter fails <b>toward</b> inspecting
/// (fail-closed), never silently skipping the judge.
/// </summary>
internal static class ParseOnlyJudge
{
    public static GateVerdict Evaluate(IJudgeRubric rubric, string text, string modelReply, JudgeGateOptions? options = null)
    {
        options ??= new JudgeGateOptions();
        var policy = $"judge:{rubric.Axis}";

        if (string.IsNullOrEmpty(text) || !SafePrefilter(rubric, text))
        {
            return GateVerdict.Allow(policy);   // matches CompositeJudgeGate short-circuit — most turns cost nothing
        }

        var verdict = rubric.Parse(modelReply) ?? JudgeVerdict.Inconclusive($"{rubric.Axis} rubric returned null");
        return verdict.Decision switch
        {
            // !(<) so a NaN confidence BLOCKS (fail-closed), matching CompositeJudgeGate
            JudgeDecision.Blocked when !(verdict.Confidence < options.BlockThreshold) =>
                GateVerdict.Block(policy, verdict.Rationale ?? $"{rubric.Axis} detected", verdict.Spans),
            JudgeDecision.Inconclusive when options.FailClosedOnInconclusive =>
                GateVerdict.Block(policy, verdict.Rationale ?? $"{rubric.Axis} judge inconclusive (fail-closed)"),
            _ => GateVerdict.Allow(policy),
        };
    }

    private static bool SafePrefilter(IJudgeRubric rubric, string text)
    {
        try { return rubric.Prefilter(text); }
        catch { return true; }   // a broken prefilter must not silently skip the judge — fail toward inspecting
    }
}
