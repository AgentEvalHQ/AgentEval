// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>Per-axis judge dispatch: build the gate, its keyword baseline, its gold set, and its rubric.</summary>
internal sealed record JudgeAxisEntry(
    Func<IChatClient, bool, IChatGate> Create,   // (fastModel, cache) → judge gate
    Func<IChatGate> KeywordBaseline,
    Func<JudgeGoldSet> GoldSet,
    Func<IJudgeRubric> Rubric);

/// <summary>
/// The hand-authored judge-axis registry (no central registry exists in the codebase) — the single source of truth
/// for the four calibrated axes the CLI's judge <c>inspect</c> and <c>calibrate</c> paths dispatch on.
/// </summary>
internal static class JudgeAxisRegistry
{
    private static readonly Dictionary<string, JudgeAxisEntry> Map = new(StringComparer.Ordinal)
    {
        ["indirect-injection"] = new(
            (m, c) => IndirectInjectionJudge.Create(m, null, c), IndirectInjectionJudge.KeywordBaseline,
            IndirectInjectionJudge.GoldSet, () => new IndirectInjectionRubric()),
        ["exfiltration-intent"] = new(
            (m, c) => ExfiltrationIntentJudge.Create(m, null, c), ExfiltrationIntentJudge.KeywordBaseline,
            ExfiltrationIntentJudge.GoldSet, () => new ExfiltrationIntentRubric()),
        ["system-prompt-extraction"] = new(
            (m, c) => SystemPromptExtractionJudge.Create(m, null, c), SystemPromptExtractionJudge.KeywordBaseline,
            SystemPromptExtractionJudge.GoldSet, () => new SystemPromptExtractionRubric()),
        ["over-refusal"] = new(
            (m, c) => OverRefusalJudge.Create(m, null, c), OverRefusalJudge.KeywordBaseline,
            OverRefusalJudge.GoldSet, () => new OverRefusalRubric()),
    };

    public static JudgeAxisEntry? For(string axis) => Map.TryGetValue(axis, out var e) ? e : null;
}
