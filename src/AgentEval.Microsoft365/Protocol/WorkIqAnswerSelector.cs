// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>The outcome of answer selection: the raw text (markers kept), its completeness, the candidates considered and the parts it came from.</summary>
/// <param name="RawText">The selected text; null when <paramref name="Completeness"/> is <see cref="OutputCompleteness.None"/> or <see cref="OutputCompleteness.Ambiguous"/>.</param>
/// <param name="Completeness">Whether an answer was assembled.</param>
/// <param name="CandidateArtifactIds">Every artifact classified as an answer candidate (all retained on ambiguity).</param>
/// <param name="Sources">The parts the text was assembled from.</param>
internal sealed record WorkIqAnswerSelection(
    string? RawText,
    OutputCompleteness Completeness,
    ImmutableArray<string> CandidateArtifactIds,
    ImmutableArray<SourceRef> Sources);

/// <summary>
/// Deterministic answer-artifact policy (version <see cref="PolicyVersion"/>): a direct message's text parts;
/// for a completed task the artifact classified as the answer (name <c>Answer</c>, or the single text artifact;
/// diagnostic artifacts excluded by media type/name); status-message text never merged; several candidates →
/// <see cref="OutputCompleteness.Ambiguous"/> with every candidate retained (never mixed); text chunks concatenated
/// without inserted newlines; an empty answer is <see cref="OutputCompleteness.Empty"/>, not a failure. Owned by lane A (M21).
/// </summary>
internal static class WorkIqAnswerSelector
{
    /// <summary>The versioned selection policy id stamped into <see cref="AnswerEvidence.SelectionPolicyVersion"/>.</summary>
    public const string PolicyVersion = "workiq-answer-selection/v1";

    /// <summary>Selects the answer of a task snapshot (only a <see cref="WorkIqTaskState.Completed"/> task can yield a final answer).</summary>
    /// <param name="task">The final task snapshot.</param>
    /// <param name="captureId">The capture the snapshot was read from (for source pointers).</param>
    public static WorkIqAnswerSelection SelectFromTask(WorkIqTaskSnapshot task, string captureId) =>
        throw new NotImplementedException("TODO(M21)");

    /// <summary>Selects the answer of a direct message reply.</summary>
    /// <param name="message">The reply message.</param>
    /// <param name="captureId">The capture the message was read from (for source pointers).</param>
    public static WorkIqAnswerSelection SelectFromMessage(WorkIqMessage message, string captureId) =>
        throw new NotImplementedException("TODO(M21)");
}
