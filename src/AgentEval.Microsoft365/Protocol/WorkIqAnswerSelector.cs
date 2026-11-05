// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
/// <remarks>
/// The selector is state-agnostic: it classifies and assembles whatever the snapshot holds, and the conversation
/// decides whether the text may become an answer (only a <see cref="WorkIqTaskState.Completed"/> task or a direct
/// message can). Source pointers are RFC 6901 pointers relative to the task object (<c>/artifacts/{i}/parts/{p}/text</c>)
/// or to the direct message (<c>/parts/{p}/text</c>), the same convention the evidence normaliser uses.
/// </remarks>
internal static class WorkIqAnswerSelector
{
    /// <summary>The versioned selection policy id stamped into <see cref="AnswerEvidence.SelectionPolicyVersion"/>.</summary>
    public const string PolicyVersion = "workiq-answer-selection/v1";

    /// <summary>The artifact name the native evaluator gives the answer.</summary>
    public const string AnswerArtifactName = "Answer";

    /// <summary>Artifact names that identify a diagnostic artifact (compared case-insensitively, trimmed).</summary>
    public static readonly ImmutableArray<string> DiagnosticArtifactNames = ["Developer Info", "Retrieval Info"];

    /// <summary>Media types that identify a diagnostic artifact (compared case-insensitively, trimmed).</summary>
    public static readonly ImmutableArray<string> DiagnosticMediaTypes = [WorkIqA2AClient.DeveloperInfoMediaType, WorkIqA2AClient.RetrievalInfoMediaType];

    /// <summary>Selects the answer of a task snapshot (only a <see cref="WorkIqTaskState.Completed"/> task can yield a final answer).</summary>
    /// <param name="task">The final task snapshot.</param>
    /// <param name="captureId">The capture the snapshot was read from (for source pointers).</param>
    public static WorkIqAnswerSelection SelectFromTask(WorkIqTaskSnapshot task, string captureId)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var candidates = new List<(WorkIqArtifact Artifact, int Index)>();
        for (var i = 0; i < task.Artifacts.Length; i++)
        {
            var artifact = task.Artifacts[i];
            if (!IsDiagnosticArtifact(artifact))
            {
                candidates.Add((artifact, i));
            }
        }

        if (candidates.Count == 0)
        {
            return new WorkIqAnswerSelection(null, OutputCompleteness.None, ImmutableArray<string>.Empty, ImmutableArray<SourceRef>.Empty);
        }

        var candidateIds = candidates.Select(c => c.Artifact.ArtifactId).ToImmutableArray();
        var chosen = candidates;
        if (candidates.Select(c => c.Artifact.ArtifactId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            var named = candidates.Where(c => IsAnswerName(c.Artifact.Name)).ToList();
            chosen = named.Count > 0 ? named : candidates;
        }

        if (chosen.Count > 1)
        {
            return new WorkIqAnswerSelection(null, OutputCompleteness.Ambiguous, candidateIds, ImmutableArray<SourceRef>.Empty);
        }

        var (selected, index) = chosen[0];
        var text = new StringBuilder();
        var sources = ImmutableArray.CreateBuilder<SourceRef>();
        for (var p = 0; p < selected.Parts.Length; p++)
        {
            var part = selected.Parts[p];
            if (part.Kind != WorkIqPartKind.Text || part.Text is null)
            {
                continue;
            }

            text.Append(part.Text);
            sources.Add(new SourceRef(
                captureId,
                0,
                EvidenceOrigin.A2AArtifact,
                string.Create(CultureInfo.InvariantCulture, $"/artifacts/{index}/parts/{p}/text"),
                selected.ArtifactId,
                selected.Revision,
                p));
        }

        var completeness = sources.Count == 0 || text.Length == 0 ? OutputCompleteness.Empty : OutputCompleteness.Complete;
        return new WorkIqAnswerSelection(text.ToString(), completeness, candidateIds, sources.ToImmutable());
    }

    /// <summary>Selects the answer of a direct message reply.</summary>
    /// <param name="message">The reply message.</param>
    /// <param name="captureId">The capture the message was read from (for source pointers).</param>
    public static WorkIqAnswerSelection SelectFromMessage(WorkIqMessage message, string captureId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var text = new StringBuilder();
        var sources = ImmutableArray.CreateBuilder<SourceRef>();
        for (var p = 0; p < message.Parts.Length; p++)
        {
            var part = message.Parts[p];
            if (part.Kind != WorkIqPartKind.Text || part.Text is null)
            {
                continue;
            }

            text.Append(part.Text);
            sources.Add(new SourceRef(
                captureId,
                0,
                EvidenceOrigin.A2AMessage,
                string.Create(CultureInfo.InvariantCulture, $"/parts/{p}/text"),
                PartIndex: p));
        }

        var completeness = sources.Count == 0 || text.Length == 0 ? OutputCompleteness.Empty : OutputCompleteness.Complete;
        return new WorkIqAnswerSelection(text.ToString(), completeness, ImmutableArray<string>.Empty, sources.ToImmutable());
    }

    /// <summary>Whether an artifact is a diagnostic artifact (by name or by the media type of any of its parts), never an answer candidate.</summary>
    /// <param name="artifact">The artifact.</param>
    public static bool IsDiagnosticArtifact(WorkIqArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (IsDiagnosticName(artifact.Name))
        {
            return true;
        }

        foreach (var part in artifact.Parts)
        {
            if (IsDiagnosticMediaType(part.MediaType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="name"/> is one of <see cref="DiagnosticArtifactNames"/>.</summary>
    /// <param name="name">The artifact name.</param>
    public static bool IsDiagnosticName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DiagnosticArtifactNames.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="mediaType"/> is one of <see cref="DiagnosticMediaTypes"/>.</summary>
    /// <param name="mediaType">The part media type.</param>
    public static bool IsDiagnosticMediaType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) && DiagnosticMediaTypes.Any(m => string.Equals(m, mediaType.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="name"/> is the <c>Answer</c> artifact name (case-insensitive, trimmed).</summary>
    /// <param name="name">The artifact name.</param>
    public static bool IsAnswerName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && string.Equals(name.Trim(), AnswerArtifactName, StringComparison.OrdinalIgnoreCase);
}
