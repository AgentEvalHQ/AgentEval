// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Parses citation markers out of the raw answer text: the Work IQ Unicode block
/// <c>cite(turn{X}search{Y})+</c> (every compound reference kept with both its turn and search
/// components, UTF-16 offsets recorded) and markdown numeric anchors (<c>[n]</c>, flagged
/// <c>MarkerFormat = markdown-numeric</c>). Parsing is a <see cref="EvidenceDerivation.ParserProjection"/>;
/// it never alters the raw text and never binds a marker to a source.
/// </summary>
internal static class CitationMarkerParser
{
    /// <summary>Parses the markers of one raw answer text.</summary>
    /// <param name="rawText">The answer text with its original markers.</param>
    /// <param name="answerSource">The source pointer of the answer text.</param>
    public static EvidenceSet<CitationMarker> Parse(string rawText, SourceRef answerSource) =>
        throw new NotImplementedException("TODO(M22)");
}
