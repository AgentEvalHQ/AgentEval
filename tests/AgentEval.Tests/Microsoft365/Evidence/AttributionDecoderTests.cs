// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using Xunit;
using static AgentEval.Tests.Microsoft365.Evidence.EvidenceTestKit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="AttributionDecoder"/>: absent vs null vs list vs malformed are four different outcomes, every item is
/// kept with its JSON pointer, every field keeps its own state, and nothing is renamed or invented.
/// </summary>
public sealed class AttributionDecoderTests
{
    private static readonly SourceRef MessageSource = new("cap-1", 0, EvidenceOrigin.A2AMessage, "/status/message");

    private const string TwoCitations = """
        {
          "attributions": [
            { "attributionType": "citation", "attributionSource": "OneDriveAndSharePoint", "providerDisplayName": "Status report.docx", "seeMoreWebUrl": "https://sharepoint.example.invalid/a" },
            { "attributionType": "annotation", "attributionSource": "Outlook", "providerDisplayName": "RE: open items", "seeMoreWebUrl": "https://outlook.example.invalid/b" }
          ]
        }
        """;

    [Fact]
    public void Decode_NullMetadata_IsNotProvidedPointingAtTheMessage()
    {
        var set = AttributionDecoder.Decode(null, MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
        Assert.Equal(EvidenceValidation.NotApplicable, set.Validation);
        Assert.Equal(EvidenceCoverage.Unknown, set.Coverage);
        Assert.Equal(EvidenceScope.Response, set.Scope);
        Assert.Empty(set.Items);
        Assert.Equal("/status/message", Assert.Single(set.Sources).JsonPointer);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void Decode_MetadataWithoutAttributions_IsNotProvidedPointingAtTheMetadata()
    {
        var set = AttributionDecoder.Decode(ParseJson("""{"Location":{"timeZone":"Europe/Zurich"}}"""), MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
        Assert.Equal("/status/message/metadata", Assert.Single(set.Sources).JsonPointer);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void Decode_MetadataNotAnObject_IsNotProvidedWithAnIssue()
    {
        var set = AttributionDecoder.Decode(ParseJson("[1,2]"), MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
        Assert.Equal(AttributionDecoder.MetadataNotObjectIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void Decode_AttributionsNull_IsProvidedMalformedWithTheNullIssue()
    {
        var set = AttributionDecoder.Decode(ParseJson("""{"attributions":null}"""), MessageSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.Empty(set.Items);
        Assert.False(set.IsObservedEmpty);
        var issue = Assert.Single(set.Issues);
        Assert.Equal(AttributionDecoder.AttributionsNullIssueCode, issue.Code);
        Assert.Equal("/status/message/metadata/attributions", Assert.Single(issue.Sources).JsonPointer);
    }

    [Theory]
    [InlineData("""{"attributions":"x"}""")]
    [InlineData("""{"attributions":{"a":1}}""")]
    [InlineData("""{"attributions":42}""")]
    public void Decode_AttributionsNotAnArray_IsProvidedMalformed(string metadata)
    {
        var set = AttributionDecoder.Decode(ParseJson(metadata), MessageSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.False(set.IsObservedEmpty);
        Assert.Equal(AttributionDecoder.AttributionsNotArrayIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void Decode_EmptyArray_IsObservedEmpty()
    {
        var set = AttributionDecoder.Decode(ParseJson("""{"attributions":[]}"""), MessageSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Valid, set.Validation);
        Assert.True(set.IsObservedEmpty);
        Assert.Equal("/status/message/metadata/attributions", Assert.Single(set.Sources).JsonPointer);
    }

    [Fact]
    public void Decode_TwoItems_KeepsEveryFieldWithItsPointer()
    {
        var set = AttributionDecoder.Decode(ParseJson(TwoCitations), MessageSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Valid, set.Validation);
        Assert.Equal(EvidenceCoverage.Unknown, set.Coverage);
        Assert.Equal(2, set.Items.Length);
        Assert.Empty(set.Issues);

        var first = set.Items[0];
        Assert.Equal("citation-source-0", first.ObservationId);
        Assert.Equal(FieldState.Missing, first.ProviderReferenceKey.State);
        Assert.Equal("https://sharepoint.example.invalid/a", first.Url.Value);
        Assert.Equal(EvidenceDerivation.ProviderValue, first.Url.Derivation);
        Assert.Equal("Status report.docx", first.DisplayName.Value);
        Assert.Equal("citation", first.ProviderType.Value);
        Assert.Equal("/status/message/metadata/attributions/0", Assert.Single(first.Sources).JsonPointer);
        Assert.Equal("/status/message/metadata/attributions/0/seeMoreWebUrl", Assert.Single(first.Url.Sources).JsonPointer);
        Assert.Equal("/status/message/metadata/attributions/0/providerDisplayName", Assert.Single(first.DisplayName.Sources).JsonPointer);
        Assert.Equal("/status/message/metadata/attributions/0/attributionType", Assert.Single(first.ProviderType.Sources).JsonPointer);

        var second = set.Items[1];
        Assert.Equal("citation-source-1", second.ObservationId);
        Assert.Equal("annotation", second.ProviderType.Value);
        Assert.Equal("/status/message/metadata/attributions/1", Assert.Single(second.Sources).JsonPointer);
    }

    [Fact]
    public void Decode_ItemFieldStates_AreMissingNullPresentInvalid()
    {
        var metadata = ParseJson("""
            {"attributions":[
              {"attributionType":"citation"},
              {"attributionType":"citation","seeMoreWebUrl":null,"providerDisplayName":null},
              {"attributionType":7,"seeMoreWebUrl":"https://x.example.invalid","providerDisplayName":["no"]}
            ]}
            """);

        var set = AttributionDecoder.Decode(metadata, MessageSource);

        Assert.Equal(EvidenceValidation.Partial, set.Validation);
        Assert.Equal(3, set.Items.Length);
        Assert.Equal(FieldState.Missing, set.Items[0].Url.State);
        Assert.Equal(FieldState.Missing, set.Items[0].DisplayName.State);
        Assert.Equal(FieldState.Null, set.Items[1].Url.State);
        Assert.Equal(FieldState.Null, set.Items[1].DisplayName.State);
        Assert.Equal(FieldState.Present, set.Items[2].Url.State);
        Assert.Equal(FieldState.Invalid, set.Items[2].ProviderType.State);
        Assert.Equal(FieldState.Invalid, set.Items[2].DisplayName.State);
        Assert.Equal(2, set.Issues.Count(i => i.Code == AttributionDecoder.FieldInvalidIssueCode));
    }

    [Fact]
    public void Decode_NonObjectItem_IsKeptWithInvalidFieldsAndPartialValidation()
    {
        var set = AttributionDecoder.Decode(ParseJson("""{"attributions":[{"attributionType":"citation"},null,"junk"]}"""), MessageSource);

        Assert.Equal(EvidenceValidation.Partial, set.Validation);
        Assert.Equal(3, set.Items.Length);
        Assert.Equal(FieldState.Invalid, set.Items[1].Url.State);
        Assert.Equal(FieldState.Invalid, set.Items[2].DisplayName.State);
        Assert.Equal(FieldState.Missing, set.Items[1].ProviderReferenceKey.State);
        Assert.Equal("/status/message/metadata/attributions/2", Assert.Single(set.Items[2].Sources).JsonPointer);
        Assert.Equal(2, set.Issues.Count(i => i.Code == AttributionDecoder.AttributionNotObjectIssueCode));
    }

    [Fact]
    public void Decode_OnlyNonObjectItems_IsMalformedButKeepsTheRows()
    {
        var set = AttributionDecoder.Decode(ParseJson("""{"attributions":[1,2]}"""), MessageSource);

        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.Equal(2, set.Items.Length);
        Assert.False(set.IsObservedEmpty);
    }

    [Fact]
    public void Decode_DirectMessageRoot_PointersStartAtTheMessage()
    {
        var set = AttributionDecoder.Decode(ParseJson(TwoCitations), new SourceRef("cap-1", 0, EvidenceOrigin.A2AMessage, ""));

        Assert.Equal("/metadata/attributions", Assert.Single(set.Sources).JsonPointer);
        Assert.Equal("/metadata/attributions/1/seeMoreWebUrl", Assert.Single(set.Items[1].Url.Sources).JsonPointer);
    }

    [Fact]
    public void Decode_NullMessageSource_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AttributionDecoder.Decode(null, null!));
}
