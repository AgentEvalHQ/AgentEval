// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// A single tool/function invocation captured as part of an eval input.
/// <para>
/// Within <see cref="EvalInput.ToolCalls"/> these are ordered <b>chronologically</b> by
/// invocation (earliest first); the record itself carries no timestamp, so the list position
/// <i>is</i> the time order. See the ordering contract on <see cref="EvalInput.ToolCalls"/>.
/// </para>
/// </summary>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object>? Arguments, string? Result);

/// <summary>Definition of a tool that was available to the agent during the evaluated interaction.</summary>
public sealed record ToolDefinition(string Name, string? Description, IReadOnlyDictionary<string, object>? Parameters);

/// <summary>An expected agentic action used to verify tool-use behaviour.</summary>
public sealed record ExpectedAction(string Description, IReadOnlyList<string>? RequiredTools);

/// <summary>Intentionally permissive input container shared across all eval types.</summary>
/// <remarks>
/// <b>ToolCalls ordering contract:</b> <see cref="ToolCalls"/> is ordered <b>chronologically</b>
/// by invocation (earliest first). This is a contract, not a convenience: order-sensitive
/// evaluators rely on the list position as the call's time order — the tool-sequence assertions
/// (<c>WithArgument</c>/<c>BeforeTool</c>/<c>AfterTool</c>, <c>MustConfirmBefore</c>) and the
/// prohibited-actions approval gate (which treats an approval as valid only if it appears at an
/// earlier index than the sensitive call). Callers MUST populate <see cref="ToolCalls"/>
/// chronologically; supplying it out of order yields undefined safety-gate results (BUG-37).
/// </remarks>
public sealed record EvalInput(
    string Query,
    string? Response = null,
    string? Context = null,
    string? GroundTruth = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    IReadOnlyList<ToolDefinition>? ToolDefinitions = null,
    IReadOnlyList<ExpectedAction>? ExpectedActions = null,
    string? SystemMessage = null,
    IReadOnlyDictionary<string, object>? Metadata = null)
{
    /// <summary>
    /// Canonical <see cref="Metadata"/> key under which a Glass Box <c>AgentTrace</c> may be stashed so
    /// trace-aware evaluators (Glass Box Part 2) can read what actually happened at the chat/tool boundary.
    /// Kept as a string-keyed <see cref="Metadata"/> entry — not a typed field — because this Abstractions
    /// assembly cannot reference the Core trace type. Set via <c>WithTrace</c> and read via <c>GetTrace</c>
    /// (both in <c>AgentEval.Core</c>, namespace <c>AgentEval.Evals</c>).
    /// </summary>
    public const string TraceMetadataKey = "__agentTrace__";

    /// <summary>
    /// Stable identity for the case this input represents. Optional, non-positional and init-only,
    /// so every existing construction site and every deconstruction is unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-030 §4.7, Slice 1.6 (defect D11). The unit of analysis for a floor, a paired comparison
    /// or a shuffled-gold control is the CASE, and none of them is implementable without a stable
    /// per-case key. There was none: <see cref="EvalInput"/> had no identity at all, and the
    /// flagship sample joined on <c>$"{c.Id} — {c.Group}"</c> — a formatted <i>display string</i>
    /// used as a join key, which silently re-points the moment anyone edits the label.
    /// </para>
    /// <para>
    /// Deliberately a plain nullable string with no generated default. An id that the library
    /// invents is an id that changes between runs, and a join key that changes between runs is
    /// worse than an absent one because it fails silently. <see langword="null"/> means "this
    /// producer has not declared case identity" and the meta layer must say so rather than guess.
    /// </para>
    /// </remarks>
    public string? CaseId { get; init; }

    /// <summary>
    /// The model the SUBJECT ran on, when the producer knows it. Optional, non-positional and
    /// init-only, so every existing construction site and every deconstruction is unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-031 §0.1's <c>judgeIsSubjectModel</c> follow-on. It exists for exactly one question:
    /// <b>is the judge the same model as the thing it is grading?</b> That is the
    /// gate-self-examination failure at its purest — the artifact under test supplying the
    /// measurement — and it cannot be answered from the result alone, because an
    /// <c>EvalProvenance</c> records the JUDGE's model and nothing about the subject's.
    /// </para>
    /// <para>
    /// ⚠ <see langword="null"/> yields <see cref="AgentEval.Output.JudgeSubjectRelation.Unknown"/>,
    /// never <c>DifferentModel</c>. "Nobody declared the subject's model" and "the judge is a
    /// different model" are different facts, and collapsing them answers the self-examination
    /// question with a reassuring "no" that nobody checked.
    /// </para>
    /// </remarks>
    public string? SubjectModel { get; init; }
}
