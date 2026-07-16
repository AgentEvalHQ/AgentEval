// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Gatekeeper §D — guards the memory/vector-store <b>WRITE</b> side. Every other injection judge
/// (<see cref="IndirectInjectionJudge"/>) guards the READ side (content coming IN to the agent); this gate
/// inspects content going OUT to persistent memory BEFORE it is written, catching a poisoned memory write
/// that would otherwise sit dormant and compromise a LATER session/run/user that reads it back — a
/// time-delayed, cross-session, cross-user blast radius no read-side gate can see (the poison isn't
/// dangerous until it's later retrieved and re-injected into a different context).
/// </summary>
/// <remarks>
/// <b>Reuses the calibrated <see cref="IndirectInjectionRubric"/> verbatim</b> — same class, same prompt,
/// same axis (<c>indirect-injection</c>) — rather than authoring a new rubric, per the design backlog's own
/// "reuses the IndirectInjection rubric pattern at a new seam" guidance. The semantic question ("does this
/// text try to instruct/manipulate the agent?") is axis-invariant to WHERE the text came from; a memory
/// write is textually closer to the rubric's original retrieved-document training surface than the Skills
/// Phase 3 system-prompt surface was (which did NOT converge — see
/// <c>AgentEval.Guardrails.Judges.Rubrics.SkillInjectionGoldSet</c>'s live-calibration finding). This class
/// still requires its OWN calibration proof via <see cref="CalibrateAsync"/> before an inline claim — reuse
/// of the rubric is not a substitute for verifying it on this seam's actual traffic shape.
/// </remarks>
public static class MemoryWritePoisoningGate
{
    /// <summary>The axis id — the SAME as <see cref="IndirectInjectionJudge.Axis"/> (verbatim rubric reuse).</summary>
    public const string Axis = IndirectInjectionJudge.Axis;

    /// <summary>
    /// Builds the gate as an <see cref="IChatGate"/> over a fast model, ready to inspect a value BEFORE it
    /// is committed to a memory/vector-store write tool's arguments.
    /// </summary>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
        => IndirectInjectionJudge.Create(fastModel, options, cache);

    /// <summary>The canonical deterministic baseline this gate must beat.</summary>
    public static IChatGate KeywordBaseline() => IndirectInjectionJudge.KeywordBaseline();

    /// <summary>
    /// The gold set for THIS seam. Reuses the canonical <see cref="IndirectInjectionRubric.CalibrationGoldSet"/>
    /// as-is: the rubric's original retrieved-document cases are a reasonable proxy for memory-write content
    /// (both are "text ingested from an external/persisted source, about to influence the agent"). Extend
    /// with your own memory-write-specific traffic before trusting this inline on a materially different shape.
    /// </summary>
    public static JudgeGoldSet GoldSet() => IndirectInjectionJudge.GoldSet();

    /// <summary>Calibrate the gate against the gold set and keyword-oracle baseline with a zero-missed-attacks bar.</summary>
    public static Task<CalibrationReport> CalibrateAsync(
        IChatClient fastModel,
        JudgeGoldSet? goldSet = null,
        IChatGate? baseline = null,
        int maxDangerousErrors = 0,
        JudgeGateOptions? options = null,
        CancellationToken cancellationToken = default)
        => IndirectInjectionJudge.CalibrateAsync(fastModel, goldSet ?? GoldSet(), baseline, maxDangerousErrors, options, cancellationToken);
}
