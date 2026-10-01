// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Optional companion to <see cref="IEvaluator"/>: a judge that can state which prompt it actually sends, so an
/// eval built on it can fingerprint its instrument.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: <c>AtomicLlmEval</c> sees only <c>(input, output, criteria)</c>. The system prompt and the
/// user-prompt template live inside the evaluator, so an eval could not tell — and so could not record — that
/// two runs were graded by different instructions. Its <c>PromptHash</c> was <see langword="null"/> at every
/// production site, which disabled the run-comparison axis built to catch exactly that.
/// </para>
/// <para>
/// Implement it when the evaluator controls its own prompt. An evaluator that does not implement it is
/// fingerprinted by type name only, which still pins the criteria and the framing but cannot see a prompt change
/// inside the evaluator; that limitation is the reason this interface exists.
/// </para>
/// </remarks>
public interface IJudgePromptSource
{
    /// <summary>
    /// A stable, human-readable identifier for the system prompt this evaluator sends (for example
    /// <c>agenteval.judge.default-system.v1</c>, or the name of an embedded prompt file), or <see langword="null"/>
    /// when it cannot name it. Recorded as the result's <c>PromptId</c>: it must name what is SENT, never a prompt
    /// that merely exists.
    /// </summary>
    string? SystemPromptId { get; }

    /// <summary>
    /// The exact material that, together with each call's input, output and criteria, determines the prompt: the
    /// system prompt text and a version marker for the user-prompt template. Hashed into the result's
    /// <c>PromptHash</c>; any change to either must change this string.
    /// </summary>
    string PromptMaterial { get; }
}
