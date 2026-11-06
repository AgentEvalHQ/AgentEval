// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using Xunit;
using static AgentEval.Tests.Microsoft365.Evidence.EvidenceTestKit;

namespace AgentEval.Tests.Microsoft365.HarnessBridge;

/// <summary>
/// <see cref="Microsoft365EvidenceProjector.ToAefDocument"/>, the evidence document stored in an AEF run's content
/// capture. Under every policy it is the <see cref="Microsoft365EvidenceJson"/> form with the schema id inside; a removed
/// value becomes <c>Redacted</c> (never a substitute, never a missing field), and every state, availability and count
/// stays as observed. <c>FullEvidence</c> keeps everything; <c>SelectedContent</c> (the CLI default) keeps the answer and
/// the citations but not the payloads; <c>MetadataOnly</c> also drops the answer text and the citation URLs and names.
/// </summary>
public sealed class AefDocumentProjectionTests
{
    private const string AnswerFixture = "envelopes/send-task-completed-with-answer-and-attributions.json";
    private const string DevRetrievalFixture = "envelopes/send-task-completed-with-developer-and-retrieval-info.json";
    private const string ConfirmationFixture = "envelopes/send-task-input-required-confirmation.json";

    private static JsonNode Parse(string document) => JsonNode.Parse(document)!;

    private static string? StateOf(JsonNode? observed) => (string?)observed?["state"];

    // ---------------------------------------------------------------------------------------------------------
    // FullEvidence
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToAefDocument_FullEvidence_IsTheSerializedDocumentWithTheSchemaIdInside()
    {
        var evidence = Normalize(SnapshotFromEnvelope(DevRetrievalFixture));

        var document = Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.FullEvidence);

        Assert.Equal(Microsoft365EvidenceJson.Serialize(evidence), document);
        Assert.Equal(Microsoft365Evidence.SchemaVersion, (string?)Parse(document)["schemaVersion"]);
        Assert.Contains("Project Atlas status report", document, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EvidenceContentPolicy.FullEvidence)]
    [InlineData(EvidenceContentPolicy.SelectedContent)]
    [InlineData(EvidenceContentPolicy.MetadataOnly)]
    public void ToAefDocument_AnyPolicy_IsDeterministicParsesBackAndKeepsTheVersions(EvidenceContentPolicy policy)
    {
        var evidence = Normalize(SnapshotFromEnvelope(DevRetrievalFixture));

        var first = Microsoft365EvidenceProjector.ToAefDocument(evidence, policy);
        var second = Microsoft365EvidenceProjector.ToAefDocument(Normalize(SnapshotFromEnvelope(DevRetrievalFixture)), policy);

        Assert.Equal(first, second);
        var back = Microsoft365EvidenceJson.Deserialize(first);
        Assert.Equal(Microsoft365Evidence.SchemaVersion, back.SchemaVersion);
        Assert.Equal(Microsoft365Evidence.NormalizerVersion, back.NormalizerVersion);
        Assert.Equal(Microsoft365Evidence.ProviderProfileVersion, back.ProviderProfileVersion);
        Assert.Equal(evidence.Identity, back.Identity);
        Assert.Equal(evidence.TaskState, back.TaskState);
        Assert.Equal(evidence.ToolSelections.Items.Length, back.ToolSelections.Items.Length);
        Assert.Equal(evidence.ToolExecutions.Items.Length, back.ToolExecutions.Items.Length);
        Assert.Equal(evidence.RetrievalExecutions.Availability, back.RetrievalExecutions.Availability);
        Assert.Equal(evidence.CitationSources.Items.Length, back.CitationSources.Items.Length);
        Assert.Equal(evidence.CitationMarkers.Items.Length, back.CitationMarkers.Items.Length);
        Assert.Equal(evidence.Issues.Select(i => i.Code), back.Issues.Select(i => i.Code));
    }

    // ---------------------------------------------------------------------------------------------------------
    // SelectedContent
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToAefDocument_SelectedContent_KeepsAnswerAndCitationsButRedactsRetrievalPayloads()
    {
        var evidence = Normalize(SnapshotFromEnvelope(DevRetrievalFixture));

        var document = Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.SelectedContent);
        var back = Microsoft365EvidenceJson.Deserialize(document);

        // Allow-listed: the answer, the citations, the tool names, the statuses and counts.
        Assert.Equal(evidence.Answer!.RawText.Value, back.Answer!.RawText.Value);
        Assert.Equal(evidence.Answer.DisplayText.Value, back.Answer.DisplayText.Value);
        Assert.Equal(evidence.CitationSources.Items.Select(s => s.Url.Value), back.CitationSources.Items.Select(s => s.Url.Value));
        Assert.Equal("ReadDemoFile", Assert.Single(back.ToolExecutions.Items).Tool.FunctionDisplayName.Value);
        Assert.Equal(200, back.ToolExecutions.Items[0].ResponseStatus.Value);

        // Payloads: query text, hit URLs, passages and resource metadata are Redacted, never dropped or substituted.
        var retrieval = Assert.Single(back.RetrievalExecutions.Items);
        Assert.Equal("Success", retrieval.ProviderStatus.Value);
        Assert.Equal(42, retrieval.TotalResultCount.Value);
        Assert.Equal(FieldState.Redacted, Assert.Single(retrieval.Queries.Items).QueryText.State);
        Assert.Equal(2, retrieval.Hits.Items.Length);
        Assert.All(retrieval.Hits.Items, hit => Assert.Equal(FieldState.Redacted, hit.WebUrl.State));
        Assert.Equal(new[] { 1, 2 }, retrieval.Hits.Items.Select(hit => hit.Rank.Value));
        Assert.All(retrieval.Hits.Items.SelectMany(h => h.Passages.Items), p => Assert.Equal(FieldState.Redacted, p.Text.State));
        Assert.Equal(FieldState.Redacted, retrieval.Hits.Items[0].ResourceMetadata.State);
        Assert.Equal(evidence.RetrievalExecutions.Items[0].Hits.Items[1].ResourceMetadata.State, retrieval.Hits.Items[1].ResourceMetadata.State);
        Assert.NotEqual(FieldState.Redacted, retrieval.Hits.Items[1].ResourceMetadata.State);   // never sent: stays as observed
        Assert.Equal(evidence.RetrievalExecutions.Items[0].Hits.Items[0].Passages.Items.Length, retrieval.Hits.Items[0].Passages.Items.Length);

        // The removed text is gone from the bytes.
        Assert.DoesNotContain("Project Atlas status report", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Pilot start confirmed", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Risk%20register.xlsx", document, StringComparison.Ordinal);
        Assert.DoesNotContain("lastModifiedDateTime", document, StringComparison.Ordinal);
        Assert.Null(Parse(document)["retrievalExecutions"]!["items"]![0]!["queries"]!["items"]![0]!["queryText"]!["value"]);
    }

    [Fact]
    public void ToAefDocument_SelectedContent_RedactsTheProposedApprovalArgumentsButKeepsTheProposal()
    {
        var evidence = Normalize(SnapshotFromEnvelope(ConfirmationFixture), profile: DiagnosticProfile.PublicAnswerOnly);

        var document = Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.SelectedContent);
        var intent = Assert.Single(Microsoft365EvidenceJson.Deserialize(document).ApprovalIntents.Items);

        Assert.Equal(FieldState.Redacted, intent.ProposedArgumentsRawText.State);
        Assert.Equal(evidence.ApprovalIntents.Items[0].ProposedArguments.IsPresent ? FieldState.Redacted : evidence.ApprovalIntents.Items[0].ProposedArguments.State, intent.ProposedArguments.State);
        Assert.Equal("CreateDemoEvent", intent.Tool.FunctionDisplayName.Value);
        Assert.Equal("call-0a1b2c3d4e5f", intent.ProposedProviderInvocationId.Value);
        Assert.Equal("Pending", intent.Decision);
        Assert.Equal(evidence.ApprovalIntents.Items[0].Sources.Length, intent.Sources.Length);
        Assert.DoesNotContain("Atlas pilot kickoff", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ToAefDocument_PresentToolArgumentsAndResult_RedactedBelowFullEvidenceAndMissingStaysMissing()
    {
        var source = ImmutableArray.Create(new SourceRef(CaptureId, 0, EvidenceOrigin.A2AArtifact, "/artifacts/1/parts/0/data/plugins/executions/0"));
        var execution = new ToolExecutionObservation(
            "tool-execution-1",
            Observed<string>.Missing(),
            ToolIdentity.FromReportedNames(Observed<string>.Present("Demo Files", source), Observed<string>.Present("ReadDemoFile", source), source),
            Observed<JsonElement>.Present(ParseJson("""{"path":"secret-argument.docx"}"""), source),
            Observed<JsonElement>.Present(ParseJson("""{"content":"secret-result-body"}"""), source),
            Observed<int>.Present(200, source),
            Observed<ExecutionOutcome>.Missing(),
            Observed<DateTimeOffset>.Missing(),
            Observed<DateTimeOffset>.Missing(),
            Observed<double>.Missing(),
            source);
        var evidence = Normalize(SnapshotFromEnvelope(AnswerFixture)) with
        {
            ToolExecutions = EvidenceSet<ToolExecutionObservation>.Provided(
                ImmutableArray.Create(execution), EvidenceValidation.Valid, EvidenceCoverage.Unknown, EvidenceScope.Unknown, source),
        };

        var full = Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.FullEvidence);
        var selected = Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.SelectedContent);

        Assert.Contains("secret-argument.docx", full, StringComparison.Ordinal);
        Assert.Contains("secret-result-body", full, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-argument.docx", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-result-body", selected, StringComparison.Ordinal);
        var row = Assert.Single(Microsoft365EvidenceJson.Deserialize(selected).ToolExecutions.Items);
        Assert.Equal(FieldState.Redacted, row.Arguments.State);
        Assert.Equal(FieldState.Redacted, row.Result.State);
        Assert.Equal(source.ToArray(), row.Arguments.Sources.ToArray());   // element-wise: ImmutableArray equality is by reference
        Assert.Equal(FieldState.Missing, row.Outcome.State);
        Assert.Equal(FieldState.Missing, row.DurationMilliseconds.State);
        Assert.Equal(200, row.ResponseStatus.Value);
        Assert.Equal(EvidenceAvailability.Provided, Microsoft365EvidenceJson.Deserialize(selected).ToolExecutions.Availability);
    }

    // ---------------------------------------------------------------------------------------------------------
    // MetadataOnly
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToAefDocument_MetadataOnly_RemovesAnswerTextAndCitationUrlsAndNamesButKeepsTheStates()
    {
        var evidence = Normalize(SnapshotFromEnvelope(AnswerFixture), profile: DiagnosticProfile.PublicAnswerOnly);

        var document = Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.MetadataOnly);
        var node = Parse(document);
        var back = Microsoft365EvidenceJson.Deserialize(document);

        Assert.Equal(Microsoft365Evidence.SchemaVersion, (string?)node["schemaVersion"]);
        Assert.Equal("Redacted", StateOf(node["answer"]!["rawText"]));
        Assert.Equal("Redacted", StateOf(node["answer"]!["displayText"]));
        Assert.Null(node["answer"]!["rawText"]!["value"]);
        Assert.True(back.Answer!.IsFinal);
        Assert.Equal(evidence.Answer!.Sources.ToArray(), back.Answer.Sources.ToArray());

        Assert.Equal(EvidenceAvailability.Provided, back.CitationSources.Availability);
        Assert.Equal(2, back.CitationSources.Items.Length);
        Assert.All(back.CitationSources.Items, s => Assert.Equal(FieldState.Redacted, s.Url.State));
        Assert.All(back.CitationSources.Items, s => Assert.Equal(FieldState.Redacted, s.DisplayName.State));
        Assert.All(back.CitationSources.Items, s => Assert.Equal("citation", s.ProviderType.Value));
        Assert.Equal(evidence.CitationMarkers.Items.Length, back.CitationMarkers.Items.Length);
        Assert.Equal(evidence.CitationLinks.Length, back.CitationLinks.Length);
        Assert.Equal("TASK_STATE_COMPLETED", back.TaskState);
        Assert.Equal(evidence.ToolExecutions.Availability, back.ToolExecutions.Availability);

        Assert.DoesNotContain("Project Atlas is on schedule", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Two risks stay open", document, StringComparison.Ordinal);
        Assert.DoesNotContain("sharepoint.example.invalid", document, StringComparison.Ordinal);
        Assert.DoesNotContain("outlook.example.invalid", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Status report 2026-11-03.docx", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Atlas vendor contract", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ToAefDocument_MetadataOnly_NoAnswerStaysNoAnswer()
    {
        var evidence = Normalize(SnapshotFromEnvelope(ConfirmationFixture));

        var back = Microsoft365EvidenceJson.Deserialize(Microsoft365EvidenceProjector.ToAefDocument(evidence, EvidenceContentPolicy.MetadataOnly));

        Assert.Null(evidence.Answer);
        Assert.Null(back.Answer);
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", back.TaskState);
        Assert.Equal(FieldState.Redacted, Assert.Single(back.ApprovalIntents.Items).ProposedArgumentsRawText.State);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Guards
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToAefDocument_NullEvidence_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Microsoft365EvidenceProjector.ToAefDocument(null!, EvidenceContentPolicy.FullEvidence));

    [Fact]
    public void ToAefDocument_UndefinedPolicy_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Microsoft365EvidenceProjector.ToAefDocument(Normalize(SnapshotFromEnvelope(AnswerFixture)), (EvidenceContentPolicy)42));
}
