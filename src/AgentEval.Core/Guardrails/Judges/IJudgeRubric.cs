// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A <b>single-axis</b> judge rubric — one question, asked of a fast model, parsed into a decisive
/// <see cref="JudgeVerdict"/>. The unit of "The Tribunal": a rubric is cheap to write (a prefilter + a prompt +
/// a parser) and a <c>CompositeJudgeGate&lt;TRubric&gt;</c> turns it into a runtime <see cref="IChatGate"/>.
/// <para><b>Invariant — one axis only.</b> A rubric asks exactly one question (e.g. "does this content instruct
/// the agent?"). A multi-criteria mega-judge collapses toward the noise floor — the repo's grading work proved
/// task-specific single-axis decomposition is the active ingredient. Compose many rubrics with a fan-out, don't
/// widen one.</para>
/// </summary>
public interface IJudgeRubric
{
    /// <summary>Stable axis id, e.g. <c>"indirect-injection"</c>. Becomes the gate's policy name (<c>judge:{Axis}</c>).</summary>
    string Axis { get; }

    /// <summary>
    /// Cheap deterministic pre-screen. Return <c>false</c> to skip the model entirely (the gate allows) — so most
    /// turns cost zero tokens. Must be conservative: return <c>true</c> whenever the model <i>might</i> be needed.
    /// </summary>
    bool Prefilter(string text);

    /// <summary>Builds the single-axis question sent to the fast model.</summary>
    string BuildPrompt(string text);

    /// <summary>Parses the model's reply into a decisive verdict. Return <see cref="JudgeVerdict.Inconclusive"/> on an unparseable reply.</summary>
    JudgeVerdict Parse(string modelReply);
}
