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
/// for the calibrated axes the CLI's judge <c>inspect</c> and <c>calibrate</c> paths dispatch on. Deliberately
/// doesn't restate a count here (it drifts every time an axis is added) — see <see cref="Axes"/> for the live list.
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
        // Stage 3 (2026-07-16 marathon session) — all three cleared live calibration this session
        // (IsInlineReady == true, perfect decisive accuracy on their canonical gold sets; see
        // strategy/TODO.md for the numbers). Registered exactly like the four axes above: same
        // Create/KeywordBaseline/GoldSet/Rubric shape, no special-casing.
        ["intent-action-mismatch"] = new(
            (m, c) => IntentActionMismatchJudge.Create(m, null, c), IntentActionMismatchJudge.KeywordBaseline,
            IntentActionMismatchJudge.GoldSet, () => new IntentActionMismatchRubric()),
        ["goal-hijack-drift"] = new(
            (m, c) => GoalHijackDriftJudge.Create(m, null, c), GoalHijackDriftJudge.KeywordBaseline,
            GoalHijackDriftJudge.GoldSet, () => new GoalHijackDriftRubric()),
        ["inter-agent-outbound-goal-drift"] = new(
            CreateOutboundGoalDriftJudge, InterAgentBoundaryInjectionGate.OutboundKeywordBaseline,
            InterAgentBoundaryInjectionGate.OutboundGoldSet, () => new InterAgentOutboundGoalDriftRubric()),
        ["ungrounded-claim"] = new(
            (m, c) => UngroundedClaimJudge.Create(m, null, c), UngroundedClaimJudge.KeywordBaseline,
            UngroundedClaimJudge.GoldSet, () => new UngroundedClaimRubric()),
        // 2026-07-17 — the 9th calibrated axis. Registered exactly like the seven above (same Create/
        // KeywordBaseline/GoldSet/Rubric shape) so it is fully reachable through calibrate/inspect/list-gates
        // even though its ONLY live-wiring seam is the approval flow (ToolArgumentGoalCoherenceApprovalGate,
        // AgentEval.MAF), not a chat/run-gate seam like the others — the CLI bridge dispatches on IChatGate
        // regardless of which seam a caller ultimately wires the calibrated judge into.
        ["tool-argument-goal-coherence"] = new(
            (m, c) => ToolArgumentGoalCoherenceJudge.Create(m, null, c), ToolArgumentGoalCoherenceJudge.KeywordBaseline,
            ToolArgumentGoalCoherenceJudge.GoldSet, () => new ToolArgumentGoalCoherenceRubric()),
        // 2026-07-17 — the 10th calibrated axis. This registers only the calibratable STATELESS core (the
        // per-turn-shift question) via CrescendoTrajectoryTurnJudge. The multi-turn arm/no-arm behavior lives
        // in AgentEval.MAF.Gatekeeper.CrescendoTrajectoryJudge (an IShadowJudge with its own StateBag-backed
        // trajectory tracking and integration-test suite, out of this CLI registry's reach) — same split as
        // "tool-argument-goal-coherence" above, whose only live-wiring seam is also outside a chat/run-gate.
        ["crescendo-trajectory-turn-shift"] = new(
            (m, c) => CrescendoTrajectoryTurnJudge.Create(m, null, c), CrescendoTrajectoryTurnJudge.KeywordBaseline,
            CrescendoTrajectoryTurnJudge.GoldSet, () => new CrescendoTrajectoryRubric()),
        // NOTE: "hallucinated-citation" is deliberately NOT registered here. HallucinatedCitationJudge is a
        // bespoke hybrid IChatGate (deterministic citation-existence check + judge support-check) — it does
        // not implement IJudgeRubric, so it has no BuildPrompt/Parse pair for this registry's
        // parse-only-inspect path (ParseOnlyJudge.Evaluate(entry.Rubric(), ...) has nothing to call). It is
        // still fully usable directly (new HallucinatedCitationJudge(fastModel)) and fully calibrated (see
        // HallucinatedCitationJudge.CalibrationGoldSet + the live check) — just not through this CLI surface
        // without a registry shape change this session didn't scope.
    };

    private static IChatGate CreateOutboundGoalDriftJudge(IChatClient model, bool cache)
    {
        IChatGate gate = new CompositeJudgeGate<InterAgentOutboundGoalDriftRubric>(
            new InterAgentOutboundGoalDriftRubric(),
            model);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    public static JudgeAxisEntry? For(string axis) => Map.TryGetValue(axis, out var e) ? e : null;

    /// <summary>The calibrated axis ids — the single source of truth the gate registry derives from.</summary>
    public static IReadOnlyList<string> Axes { get; } = Map.Keys.ToList();
}
