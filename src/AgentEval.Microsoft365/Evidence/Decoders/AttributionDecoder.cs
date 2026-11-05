// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Decodes <c>metadata.attributions[]</c> (terminal status message, or direct message) into
/// <see cref="CitationSource"/> observations. Keeps every attribution object with its JSON pointer, distinguishes
/// absent vs null vs list vs malformed, keeps <c>attributionType</c> as sent (an attribution can be an annotation,
/// not a citation), and never renames a provider display name to a document title.
/// </summary>
internal static class AttributionDecoder
{
    /// <summary>Decodes the attributions of one message's metadata.</summary>
    /// <param name="metadata">The message metadata object, or null when the message carried none.</param>
    /// <param name="messageSource">The source pointer of the message the metadata belongs to.</param>
    public static EvidenceSet<CitationSource> Decode(JsonElement? metadata, SourceRef messageSource) =>
        throw new NotImplementedException("TODO(M22)");
}
