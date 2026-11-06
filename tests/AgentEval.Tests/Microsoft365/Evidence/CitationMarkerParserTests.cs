// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="CitationMarkerParser"/>: the Unicode block grammar with compound keys and UTF-16 offsets, repeated
/// markers as separate observations, markdown anchors flagged as a heuristic, malformed blocks reported and left
/// in place, and a display projection that strips only unresolved Unicode markers.
/// </summary>
public sealed class CitationMarkerParserTests
{
    private const string Cite = "cite";
    private const string Ref = "";
    private const string End = "";

    private static readonly SourceRef AnswerSource = new("cap-1", 0, EvidenceOrigin.A2AArtifact, "/artifacts/0/parts", "artifact-answer-1", 0);

    private static string Marker(params string[] keys) => Cite + string.Concat(keys.Select(k => Ref + k)) + End;

    [Fact]
    public void Parse_SingleMarker_RecordsKeyFormatOffsetsAndSources()
    {
        var text = "Hello." + Marker("turn0search1") + " Bye";

        var set = CitationMarkerParser.Parse(text, AnswerSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Valid, set.Validation);
        Assert.Equal(EvidenceScope.Response, set.Scope);
        Assert.Same(AnswerSource, Assert.Single(set.Sources));
        var marker = Assert.Single(set.Items);
        Assert.Equal("citation-marker-0", marker.ObservationId);
        Assert.Equal(CitationMarkerParser.UnicodeCiteFormat, marker.MarkerFormat);
        Assert.Equal(new[] { "turn0search1" }, marker.ReferenceKeys);
        Assert.Equal(6, marker.Utf16Start);
        Assert.Equal(Marker("turn0search1").Length, marker.Utf16Length);
        Assert.Equal(Marker("turn0search1"), marker.RawMarker);
        Assert.Equal(text.Substring(marker.Utf16Start, marker.Utf16Length), marker.RawMarker);
        var source = Assert.Single(marker.Sources);
        Assert.Equal(EvidenceOrigin.TextMarker, source.Origin);
        Assert.Equal(AnswerSource.JsonPointer, source.JsonPointer);
        Assert.Equal(AnswerSource.ArtifactId, source.ArtifactId);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void Parse_CompoundMarker_KeepsEveryKeyWithTurnAndSearch()
    {
        var text = "A " + Marker("turn0search1", "turn1search1") + ". B " + Marker("turn0search1") + ".";

        var set = CitationMarkerParser.Parse(text, AnswerSource);

        Assert.Equal(2, set.Items.Length);
        Assert.Equal(new[] { "turn0search1", "turn1search1" }, set.Items[0].ReferenceKeys);
        Assert.Equal(new[] { "turn0search1" }, set.Items[1].ReferenceKeys);
        Assert.Equal(2, set.Items[0].Utf16Start);
        Assert.Equal(text.IndexOf(Marker("turn0search1"), StringComparison.Ordinal), set.Items[1].Utf16Start);
    }

    [Fact]
    public void Parse_RepeatedIdenticalMarker_YieldsSeparateObservations()
    {
        var text = Marker("turn0search3") + Marker("turn0search3");

        var set = CitationMarkerParser.Parse(text, AnswerSource);

        Assert.Equal(2, set.Items.Length);
        Assert.NotEqual(set.Items[0].ObservationId, set.Items[1].ObservationId);
        Assert.Equal(0, set.Items[0].Utf16Start);
        Assert.Equal(Marker("turn0search3").Length, set.Items[1].Utf16Start);
    }

    [Fact]
    public void Parse_SurrogatePairBeforeMarker_OffsetsAreUtf16CodeUnits()
    {
        var text = "\U0001F600" + Marker("turn0search1");

        var set = CitationMarkerParser.Parse(text, AnswerSource);

        Assert.Equal(2, Assert.Single(set.Items).Utf16Start);
    }

    [Fact]
    public void Parse_MarkdownNumericAnchors_AreFlaggedHeuristic()
    {
        var set = CitationMarkerParser.Parse("See [1] and [12], not [a] or [].", AnswerSource);

        Assert.Equal(EvidenceValidation.Valid, set.Validation);
        Assert.Equal(2, set.Items.Length);
        Assert.All(set.Items, m => Assert.Equal(CitationMarkerParser.MarkdownNumericFormat, m.MarkerFormat));
        Assert.Equal(new[] { "1" }, set.Items[0].ReferenceKeys);
        Assert.Equal("[1]", set.Items[0].RawMarker);
        Assert.Equal(4, set.Items[0].Utf16Start);
        Assert.Equal(new[] { "12" }, set.Items[1].ReferenceKeys);
        Assert.Equal(CitationMarkerParser.MarkdownIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void Parse_MixedFormats_AreOrderedByOffset()
    {
        var text = "[2] then " + Marker("turn0search1") + " then [3]";

        var set = CitationMarkerParser.Parse(text, AnswerSource);

        Assert.Equal(3, set.Items.Length);
        Assert.Equal(CitationMarkerParser.MarkdownNumericFormat, set.Items[0].MarkerFormat);
        Assert.Equal(CitationMarkerParser.UnicodeCiteFormat, set.Items[1].MarkerFormat);
        Assert.Equal(CitationMarkerParser.MarkdownNumericFormat, set.Items[2].MarkerFormat);
        Assert.True(set.Items[0].Utf16Start < set.Items[1].Utf16Start && set.Items[1].Utf16Start < set.Items[2].Utf16Start);
    }

    [Theory]
    [InlineData("citeturn0")]
    [InlineData("citeturn0search")]
    [InlineData("cite")]
    [InlineData("citeturn0search1")]
    [InlineData("xciteturn0search1")]
    [InlineData("citeturn١search1")]
    public void Parse_MalformedBlock_IsReportedAndLeftUnparsed(string block)
    {
        var set = CitationMarkerParser.Parse("x " + block + " y", AnswerSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.Empty(set.Items);
        Assert.Equal(CitationMarkerParser.MalformedBlockIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void Parse_MalformedAndValidBlocks_IsPartial()
    {
        var set = CitationMarkerParser.Parse("citeturn0 ok " + Marker("turn1search2"), AnswerSource);

        Assert.Equal(EvidenceValidation.Partial, set.Validation);
        Assert.Equal(new[] { "turn1search2" }, Assert.Single(set.Items).ReferenceKeys);
        Assert.Contains(set.Issues, i => i.Code == CitationMarkerParser.MalformedBlockIssueCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No markers here.")]
    public void Parse_NoMarkers_IsValidAndObservedEmpty(string text)
    {
        var set = CitationMarkerParser.Parse(text, AnswerSource);

        Assert.True(set.IsObservedEmpty);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void Parse_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => CitationMarkerParser.Parse(null!, AnswerSource));
        Assert.Throws<ArgumentNullException>(() => CitationMarkerParser.Parse("x", null!));
    }

    [Fact]
    public void RenderDisplayText_Unresolved_StripsUnicodeMarkersAndKeepsMarkdownAnchors()
    {
        var text = "A" + Marker("turn0search1", "turn1search1") + " B [1] C" + Marker("turn0search2") + ".";
        var markers = CitationMarkerParser.Parse(text, AnswerSource).Items;

        var display = CitationMarkerParser.RenderDisplayText(text, markers, _ => Array.Empty<int>());

        Assert.Equal("A B [1] C.", display);
    }

    [Fact]
    public void RenderDisplayText_Resolved_RendersOneOrdinalPerBoundSource()
    {
        var text = "A" + Marker("turn0search1", "turn1search1") + " B" + Marker("turn0search2") + ".";
        var markers = CitationMarkerParser.Parse(text, AnswerSource).Items;

        var display = CitationMarkerParser.RenderDisplayText(text, markers, m => m.ReferenceKeys.Length == 2 ? new[] { 1, 2 } : Array.Empty<int>());

        Assert.Equal("A [1] [2] B.", display);
    }

    [Fact]
    public void RenderDisplayText_NoMarkers_ReturnsTheTextUnchanged()
    {
        Assert.Equal("plain", CitationMarkerParser.RenderDisplayText("plain", ImmutableArray<CitationMarker>.Empty, _ => Array.Empty<int>()));
        Assert.Equal("plain", CitationMarkerParser.RenderDisplayText("plain", default, _ => Array.Empty<int>()));
    }
}
