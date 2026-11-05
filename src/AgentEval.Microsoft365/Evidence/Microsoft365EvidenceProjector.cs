// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// Projects a <see cref="Microsoft365TurnEvidence"/> document into the places the rest of AgentEval reads:
/// typed evaluation-context properties for metrics, and the JSON document stored under the AEF run's content
/// capture. The projection is deliberately narrower than the document; it never claims that existing generic
/// metrics understand the properties, and it never flattens diagnostics into answer text.
/// </summary>
public static class Microsoft365EvidenceProjector
{
    /// <summary>
    /// Projects the document into <c>EvaluationContext.Properties</c> / <c>EvalInput.Metadata</c> entries: the
    /// versioned evidence object under <see cref="Microsoft365Evidence.Key"/> plus metric-gating flags (channel
    /// availability, answer completeness) so metrics can report <c>NotMeasured</c> instead of grading absence.
    /// </summary>
    /// <param name="evidence">The evidence document.</param>
    public static IReadOnlyDictionary<string, object?> ToEvaluationContextProperties(Microsoft365TurnEvidence evidence) =>
        throw new NotImplementedException("TODO(M51)");

    /// <summary>
    /// Renders the document as the JSON text stored in <c>AefNativeContent.Document</c> when content capture is
    /// on, applying <paramref name="policy"/>: <see cref="EvidenceContentPolicy.MetadataOnly"/> strips answer text,
    /// URLs and passages (marking them <see cref="FieldState.Redacted"/>), <see cref="EvidenceContentPolicy.SelectedContent"/>
    /// keeps the allow-listed fields, <see cref="EvidenceContentPolicy.FullEvidence"/> keeps everything.
    /// </summary>
    /// <param name="evidence">The evidence document.</param>
    /// <param name="policy">The content policy of the run.</param>
    public static string ToAefDocument(Microsoft365TurnEvidence evidence, EvidenceContentPolicy policy) =>
        throw new NotImplementedException("TODO(M31)");
}
