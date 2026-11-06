// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using static AgentEval.Microsoft365.Evidence.Decoders.EvidenceJsonReader;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Decodes <c>metadata.attributions[]</c> (terminal status message, or direct message) into
/// <see cref="CitationSource"/> observations. Keeps every attribution object with its JSON pointer, distinguishes
/// absent vs null vs list vs malformed, keeps <c>attributionType</c> as sent (an attribution can be an annotation,
/// not a citation), and never renames a provider display name to a document title. The raw object stays in the
/// capture; each observation points at it (<c>…/metadata/attributions/{i}</c>). No provider reference key has been
/// observed in the sample shapes, so <see cref="CitationSource.ProviderReferenceKey"/> is always
/// <see cref="FieldState.Missing"/> in this version.
/// </summary>
internal static class AttributionDecoder
{
    /// <summary>The message property that holds the attributions.</summary>
    public const string MetadataKey = "metadata";

    /// <summary>The metadata property that holds the attribution list.</summary>
    public const string AttributionsKey = "attributions";

    /// <summary>Sample-observed item field: the link of the source.</summary>
    public const string UrlKey = "seeMoreWebUrl";

    /// <summary>Sample-observed item field: the provider display label (not a document title).</summary>
    public const string DisplayNameKey = "providerDisplayName";

    /// <summary>Sample-observed item field: the attribution type (for example <c>citation</c>).</summary>
    public const string TypeKey = "attributionType";

    /// <summary>Issue: the message metadata is not a JSON object.</summary>
    public const string MetadataNotObjectIssueCode = "citations.metadata_not_object";

    /// <summary>Issue: <c>attributions</c> is an explicit null.</summary>
    public const string AttributionsNullIssueCode = "citations.attributions_null";

    /// <summary>Issue: <c>attributions</c> is present but not an array.</summary>
    public const string AttributionsNotArrayIssueCode = "citations.attributions_not_array";

    /// <summary>Issue: an attribution item is not a JSON object.</summary>
    public const string AttributionNotObjectIssueCode = "citations.attribution_not_object";

    /// <summary>Issue: an attribution field has the wrong JSON type.</summary>
    public const string FieldInvalidIssueCode = "citations.attribution_field_invalid";

    /// <summary>Decodes the attributions of one message's metadata.</summary>
    /// <param name="metadata">The message metadata object, or null when the message carried none.</param>
    /// <param name="messageSource">The source pointer of the message the metadata belongs to.</param>
    public static EvidenceSet<CitationSource> Decode(JsonElement? metadata, SourceRef messageSource)
    {
        ArgumentNullException.ThrowIfNull(messageSource);

        if (metadata is null)
        {
            return EvidenceSet<CitationSource>.NotProvided(EvidenceScope.Response, One(messageSource));
        }

        var metadataSource = Child(messageSource, MetadataKey);
        var element = metadata.Value;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return EvidenceSet<CitationSource>.NotProvided(EvidenceScope.Response, One(metadataSource)) with
            {
                Issues = ImmutableArray.Create(Issue(
                    MetadataNotObjectIssueCode,
                    "The message metadata is not a JSON object, so it cannot carry attributions.",
                    metadataSource)),
            };
        }

        if (!element.TryGetProperty(AttributionsKey, out var attributions))
        {
            return EvidenceSet<CitationSource>.NotProvided(EvidenceScope.Response, One(metadataSource));
        }

        var listSource = Child(metadataSource, AttributionsKey);
        if (attributions.ValueKind == JsonValueKind.Null)
        {
            return Malformed(listSource, AttributionsNullIssueCode, "metadata.attributions is an explicit null, not a list of attributions.");
        }

        if (attributions.ValueKind != JsonValueKind.Array)
        {
            return Malformed(listSource, AttributionsNotArrayIssueCode, "metadata.attributions is present but is not a JSON array.");
        }

        var items = ImmutableArray.CreateBuilder<CitationSource>();
        var issues = new List<EvidenceIssue>();
        var interpreted = 0;
        var anyInvalid = false;
        var index = 0;
        foreach (var item in attributions.EnumerateArray())
        {
            var itemSource = Child(listSource, index);
            var id = ObservationId("citation-source", index);
            if (item.ValueKind == JsonValueKind.Object)
            {
                var url = ReadString(item, UrlKey, itemSource);
                var displayName = ReadString(item, DisplayNameKey, itemSource);
                var type = ReadString(item, TypeKey, itemSource);
                NoteInvalid(issues, FieldInvalidIssueCode, UrlKey, url);
                NoteInvalid(issues, FieldInvalidIssueCode, DisplayNameKey, displayName);
                NoteInvalid(issues, FieldInvalidIssueCode, TypeKey, type);
                anyInvalid |= IsInvalid(url) || IsInvalid(displayName) || IsInvalid(type);
                interpreted++;
                items.Add(new CitationSource(id, Observed<string>.Missing(), url, displayName, type, One(itemSource)));
            }
            else
            {
                anyInvalid = true;
                var invalid = Observed<string>.Invalid(One(itemSource));
                items.Add(new CitationSource(id, Observed<string>.Missing(), invalid, invalid, invalid, One(itemSource)));
                issues.Add(Issue(
                    AttributionNotObjectIssueCode,
                    FormattableString.Invariant($"Attribution item {index} is not a JSON object; its fields are Invalid and the item is kept."),
                    itemSource));
            }

            index++;
        }

        return EvidenceSet<CitationSource>.Provided(
            items.ToImmutable(),
            RowSetValidation(index, interpreted, anyInvalid),
            EvidenceCoverage.Unknown,
            EvidenceScope.Response,
            One(listSource),
            issues.ToImmutableArray());
    }

    private static EvidenceSet<CitationSource> Malformed(SourceRef listSource, string code, string message) =>
        MalformedSet<CitationSource>(listSource, ImmutableArray.Create(Issue(code, message, listSource))) with
        {
            Scope = EvidenceScope.Response,
        };
}
