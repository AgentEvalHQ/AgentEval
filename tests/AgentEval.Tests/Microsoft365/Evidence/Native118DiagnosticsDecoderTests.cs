// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using Xunit;
using static AgentEval.Tests.Microsoft365.Evidence.EvidenceTestKit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="Native118DiagnosticsDecoder"/>: artifacts found by media type or name, the first object data part,
/// conflicts never resolved last-wins, every list row kept (invalid rows with Invalid fields), integers that are
/// never bools, a missing retrieval status that is never success, unranked hits kept, total vs returned counts
/// kept apart, identical rows kept apart, unknown fields counted, and a native import that names its losses.
/// </summary>
public sealed class Native118DiagnosticsDecoderTests
{
    private const string DevRetrievalFixture = "envelopes/send-task-completed-with-developer-and-retrieval-info.json";
    private const string DevType = Native118DiagnosticsDecoder.DeveloperInfoMediaType;
    private const string RetType = Native118DiagnosticsDecoder.RetrievalInfoMediaType;

    private static WorkIqArtifact DeveloperInfo(string json, string id = "artifact-dev-1", string? name = Native118DiagnosticsDecoder.DeveloperInfoName, string? mediaType = DevType) =>
        Artifact(id, name, Data(json, mediaType));

    private static WorkIqArtifact RetrievalInfo(string json, string id = "artifact-ret-1", string? name = Native118DiagnosticsDecoder.RetrievalInfoName, string? mediaType = RetType) =>
        Artifact(id, name, Data(json, mediaType));

    private static Native118DiagnosticsDecoder.Result Decode(params WorkIqArtifact[] artifacts) =>
        Native118DiagnosticsDecoder.Decode(SnapshotWithArtifacts(artifacts), CaptureId);

    private static string Executions(params string[] rows) =>
        "{\"plugins\":{\"executions\":[" + string.Join(",", rows) + "]}}";

    private static string Execution(string plugin, string function, string responseStatusJson) =>
        "{\"function\":{\"plugin\":{\"name\":\"" + plugin + "\"},\"functionDisplayName\":\"" + function + "\"},\"executionStatus\":{\"responseStatus\":" + responseStatusJson + "}}";

    private static string Retrieval(string executionJson) => "{\"executions\":[" + executionJson + "]}";

    // ---------------------------------------------------------------------------------------------------------
    // Fixture envelope
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_FixtureEnvelope_DecodesSelectionsExecutionsAndRetrieval()
    {
        var task = SnapshotFromEnvelope(DevRetrievalFixture);

        var result = Native118DiagnosticsDecoder.Decode(task, CaptureId);

        var selections = result.ToolSelections;
        Assert.Equal(EvidenceAvailability.Provided, selections.Availability);
        Assert.Equal(EvidenceValidation.Valid, selections.Validation);
        Assert.Equal(EvidenceCoverage.Unknown, selections.Coverage);
        Assert.Equal(new[] { "ListDemoEvents", "ReadDemoFile" }, selections.Items.Select(s => s.Tool.FunctionDisplayName.Value).ToArray());
        Assert.Equal(new[] { "Demo Calendar", "Demo Files" }, selections.Items.Select(s => s.Tool.PluginName.Value).ToArray());
        var selectionsSource = Assert.Single(selections.Sources);
        Assert.Equal("/artifacts/1/parts/0/data/plugins/selectedFunctions", selectionsSource.JsonPointer);
        Assert.Equal("artifact-devinfo-5c7e1a3f-0011", selectionsSource.ArtifactId);
        Assert.Equal(0, selectionsSource.PartIndex);
        Assert.Equal(EvidenceOrigin.A2AArtifact, selectionsSource.Origin);
        Assert.Equal("/artifacts/1/parts/0/data/plugins/selectedFunctions/1/functionDisplayName", Assert.Single(selections.Items[1].Tool.FunctionDisplayName.Sources).JsonPointer);

        var executions = result.ToolExecutions;
        Assert.Equal(EvidenceValidation.Valid, executions.Validation);
        var execution = Assert.Single(executions.Items);
        Assert.Equal("Demo Files", execution.Tool.PluginName.Value);
        Assert.Equal("ReadDemoFile", execution.Tool.FunctionDisplayName.Value);
        Assert.Equal(200, execution.ResponseStatus.Value);
        Assert.Equal(FieldState.Missing, execution.ProviderInvocationId.State);
        Assert.Equal(FieldState.Missing, execution.Arguments.State);
        Assert.Equal(FieldState.Missing, execution.Result.State);
        Assert.Equal(FieldState.Missing, execution.StartedAt.State);
        Assert.Equal(FieldState.Missing, execution.EndedAt.State);
        Assert.Equal(FieldState.Missing, execution.DurationMilliseconds.State);
        Assert.Equal(ExecutionOutcome.Unknown, execution.Outcome.Value);
        Assert.Equal(Native118DiagnosticsDecoder.OutcomeRuleId, execution.Outcome.DerivationRuleId);

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal("OneDriveAndSharePoint", retrieval.Capability.Value);
        Assert.Equal("Success", retrieval.ProviderStatus.Value);
        Assert.Equal(ExecutionOutcome.Succeeded, retrieval.Outcome.Value);
        Assert.Equal(EvidenceDerivation.ParserProjection, retrieval.Outcome.Derivation);
        Assert.Equal(Native118DiagnosticsDecoder.OutcomeRuleId, retrieval.Outcome.DerivationRuleId);
        var query = Assert.Single(retrieval.Queries.Items);
        Assert.Equal("Project Atlas status report", query.QueryText.Value);
        Assert.Equal(FieldState.Missing, query.FilterExpression.State);
        Assert.Equal(5, query.MaximumNumberOfResults.Value);
        Assert.Equal(42L, retrieval.TotalResultCount.Value);
        Assert.Equal(318d, retrieval.LatencyMilliseconds.Value);
        Assert.Equal(2, retrieval.Hits.Items.Length);
        Assert.Equal(1, retrieval.Hits.Items[0].Rank.Value);
        Assert.Equal(2, retrieval.Hits.Items[0].Passages.Items.Length);
        Assert.Equal("Design review closed on 3 November 2026. Pilot start confirmed for 17 November 2026.", retrieval.Hits.Items[0].Passages.Items[0].Text.Value);
        Assert.Equal("Status report 2026-11-03.docx", retrieval.Hits.Items[0].ResourceMetadata.Value.GetProperty("title").GetString());
        Assert.Equal(FieldState.Missing, retrieval.Hits.Items[1].ResourceMetadata.State);
        Assert.Equal("/artifacts/2/parts/0/data/executions/0/debugInfo/retrievalHits/1", Assert.Single(retrieval.Hits.Items[1].Sources).JsonPointer);
        Assert.Equal("retrieval-passage-0-1-0", retrieval.Hits.Items[1].Passages.Items[0].ObservationId);

        Assert.Equal(EvidenceAvailability.NotProvided, result.ApprovalIntents.Availability);
        Assert.Empty(result.Issues);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Locating artifacts
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_NoArtifacts_IsNotProvidedEverywhere()
    {
        var result = Decode(Artifact("artifact-answer-1", "Answer", Text("hi")));

        Assert.Equal(EvidenceAvailability.NotProvided, result.ToolSelections.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, result.ToolExecutions.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, result.RetrievalExecutions.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, result.ApprovalIntents.Availability);
        Assert.Equal("/artifacts", Assert.Single(result.ToolSelections.Sources).JsonPointer);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Decode_ArtifactFoundByMediaTypeOnly_IsDecoded()
    {
        var result = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":[]}}", name: "whatever"));

        Assert.True(result.ToolSelections.IsObservedEmpty);
    }

    [Fact]
    public void Decode_ArtifactFoundByNameOnly_IsDecoded()
    {
        var result = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":[]}}", mediaType: null));

        Assert.True(result.ToolSelections.IsObservedEmpty);
    }

    [Fact]
    public void Decode_MediaTypeMatchIsCaseInsensitive_NameMatchIsExact()
    {
        var byType = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":[]}}", name: null, mediaType: DevType.ToUpperInvariant()));
        var byWrongName = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":[]}}", name: "developer info", mediaType: null));

        Assert.True(byType.ToolSelections.IsObservedEmpty);
        Assert.Equal(EvidenceAvailability.NotProvided, byWrongName.ToolSelections.Availability);
    }

    [Fact]
    public void Decode_TwoDistinctDeveloperInfoArtifacts_IsConflictingWithoutLastWins()
    {
        var result = Decode(
            DeveloperInfo(Executions(Execution("A", "F", "200")), id: "artifact-dev-1"),
            DeveloperInfo(Executions(Execution("B", "G", "500")), id: "artifact-dev-2"));

        Assert.Equal(EvidenceAvailability.Provided, result.ToolSelections.Availability);
        Assert.Equal(EvidenceValidation.Conflicting, result.ToolSelections.Validation);
        Assert.Equal(EvidenceAvailability.Provided, result.ToolExecutions.Availability);
        Assert.Equal(EvidenceValidation.Conflicting, result.ToolExecutions.Validation);
        Assert.Empty(result.ToolSelections.Items);
        Assert.Empty(result.ToolExecutions.Items);
        Assert.Equal(new[] { "artifact-dev-1", "artifact-dev-2" }, result.ToolExecutions.Sources.Select(s => s.ArtifactId).ToArray());
        Assert.Equal(new[] { "/artifacts/0", "/artifacts/1" }, result.ToolExecutions.Sources.Select(s => s.JsonPointer).ToArray());
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Native118DiagnosticsDecoder.AmbiguousArtifactIssueCode, issue.Code);
        Assert.Equal(2, issue.Sources.Length);
    }

    [Fact]
    public void Decode_FirstObjectDataPart_IsTheOneUsed()
    {
        var artifact = new WorkIqArtifact(
            "artifact-dev-1",
            Native118DiagnosticsDecoder.DeveloperInfoName,
            null,
            ImmutableArray.Create(Text("not data"), Data("[1,2]", DevType), Data("{\"plugins\":{\"selectedFunctions\":[],\"executions\":[]}}", DevType), Data("{\"plugins\":{\"selectedFunctions\":\"x\"}}", DevType)),
            3,
            true);

        var result = Native118DiagnosticsDecoder.Decode(SnapshotWithArtifacts(artifact), CaptureId);

        Assert.True(result.ToolSelections.IsObservedEmpty);
        var source = Assert.Single(result.ToolSelections.Sources);
        Assert.Equal("/artifacts/0/parts/2/data/plugins/selectedFunctions", source.JsonPointer);
        Assert.Equal(2, source.PartIndex);
        Assert.Equal(3, source.ArtifactRevision);
    }

    [Fact]
    public void Decode_NoObjectDataPart_IsMalformed()
    {
        var result = Decode(Artifact("artifact-dev-1", Native118DiagnosticsDecoder.DeveloperInfoName, Text("x"), Data("[]", DevType)));

        Assert.Equal(EvidenceValidation.Malformed, result.ToolSelections.Validation);
        Assert.Equal(EvidenceValidation.Malformed, result.ToolExecutions.Validation);
        Assert.Equal(Native118DiagnosticsDecoder.NoObjectDataPartIssueCode, Assert.Single(result.Issues).Code);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Developer Info
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_ExecutionsAbsent_IsNotProvidedWhileSelectionsStayAsFound()
    {
        var result = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":[]}}"));

        Assert.True(result.ToolSelections.IsObservedEmpty);
        Assert.Equal(EvidenceAvailability.NotProvided, result.ToolExecutions.Availability);
        Assert.False(result.ToolExecutions.IsObservedEmpty);
        Assert.Equal("/artifacts/0/parts/0/data/plugins", Assert.Single(result.ToolExecutions.Sources).JsonPointer);
    }

    [Fact]
    public void Decode_PluginsMissing_IsNotProvidedWithAnIssue()
    {
        var result = Decode(DeveloperInfo("{\"other\":1}"));

        Assert.Equal(EvidenceAvailability.NotProvided, result.ToolSelections.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, result.ToolExecutions.Availability);
        Assert.Contains(result.Issues, i => i.Code == Native118DiagnosticsDecoder.ContainerMalformedIssueCode);
        Assert.Contains(result.Issues, i => i.Code == Native118DiagnosticsDecoder.UnknownFieldsIssueCode);
    }

    [Fact]
    public void Decode_PluginsNotAnObject_IsMalformed()
    {
        var result = Decode(DeveloperInfo("{\"plugins\":[]}"));

        Assert.Equal(EvidenceValidation.Malformed, result.ToolSelections.Validation);
        Assert.Equal(EvidenceValidation.Malformed, result.ToolExecutions.Validation);
        Assert.Equal(Native118DiagnosticsDecoder.ContainerMalformedIssueCode, Assert.Single(result.ToolSelections.Issues).Code);
    }

    [Theory]
    [InlineData("\"wrong-type\"", Native118DiagnosticsDecoder.ArrayNotArrayIssueCode)]
    [InlineData("{}", Native118DiagnosticsDecoder.ArrayNotArrayIssueCode)]
    [InlineData("null", Native118DiagnosticsDecoder.ArrayNullIssueCode)]
    public void Decode_SelectedFunctionsNotAList_IsProvidedMalformedNotObservedEmpty(string value, string issueCode)
    {
        var result = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":" + value + "}}"));

        Assert.Equal(EvidenceAvailability.Provided, result.ToolSelections.Availability);
        Assert.Equal(EvidenceValidation.Malformed, result.ToolSelections.Validation);
        Assert.False(result.ToolSelections.IsObservedEmpty);
        Assert.Equal(issueCode, Assert.Single(result.ToolSelections.Issues).Code);
    }

    [Fact]
    public void Decode_NullRow_IsKeptAsAnObservationWithInvalidFields()
    {
        var result = Decode(DeveloperInfo(Executions("null", Execution("Demo Tool", "Read", "true"))));

        var executions = result.ToolExecutions;
        Assert.Equal(EvidenceValidation.Partial, executions.Validation);
        Assert.Equal(2, executions.Items.Length);
        Assert.Equal(FieldState.Invalid, executions.Items[0].Tool.PluginName.State);
        Assert.Equal(FieldState.Invalid, executions.Items[0].Tool.FunctionDisplayName.State);
        Assert.Equal(FieldState.Invalid, executions.Items[0].ResponseStatus.State);
        Assert.Equal(FieldState.Missing, executions.Items[0].ProviderInvocationId.State);
        Assert.Equal("/artifacts/0/parts/0/data/plugins/executions/0", Assert.Single(executions.Items[0].Sources).JsonPointer);
        Assert.Equal("Demo Tool", executions.Items[1].Tool.PluginName.Value);
        Assert.Equal(FieldState.Invalid, executions.Items[1].ResponseStatus.State);
        Assert.Contains(executions.Issues, i => i.Code == Native118DiagnosticsDecoder.RowNotObjectIssueCode);
        Assert.Contains(executions.Issues, i => i.Code == Native118DiagnosticsDecoder.FieldInvalidIssueCode);
    }

    [Theory]
    [InlineData("200", FieldState.Present)]
    [InlineData("0", FieldState.Present)]
    [InlineData("true", FieldState.Invalid)]
    [InlineData("\"200\"", FieldState.Invalid)]
    [InlineData("200.5", FieldState.Invalid)]
    [InlineData("null", FieldState.Null)]
    public void Decode_ResponseStatus_IsAnIntegerOrNothing(string json, FieldState expected)
    {
        var result = Decode(DeveloperInfo(Executions(Execution("P", "F", json))));

        var execution = Assert.Single(result.ToolExecutions.Items);
        Assert.Equal(expected, execution.ResponseStatus.State);
        Assert.Equal("/artifacts/0/parts/0/data/plugins/executions/0/executionStatus/responseStatus", Assert.Single(execution.ResponseStatus.Sources).JsonPointer);
    }

    [Fact]
    public void Decode_ResponseStatusAbsent_IsMissing()
    {
        var result = Decode(DeveloperInfo(Executions("{\"function\":{\"plugin\":{\"name\":\"P\"},\"functionDisplayName\":\"F\"}}")));

        var execution = Assert.Single(result.ToolExecutions.Items);
        Assert.Equal(FieldState.Missing, execution.ResponseStatus.State);
        Assert.Equal(EvidenceValidation.Valid, result.ToolExecutions.Validation);
    }

    [Fact]
    public void Decode_FunctionDescriptorStates_AreMirroredOnBothNames()
    {
        var result = Decode(DeveloperInfo(Executions("{}", "{\"function\":null}", "{\"function\":5}", "{\"function\":{\"plugin\":null}}", "{\"function\":{\"plugin\":\"x\",\"functionDisplayName\":7}}")));

        var items = result.ToolExecutions.Items;
        Assert.Equal(FieldState.Missing, items[0].Tool.PluginName.State);
        Assert.Equal(FieldState.Missing, items[0].Tool.FunctionDisplayName.State);
        Assert.Equal(FieldState.Null, items[1].Tool.PluginName.State);
        Assert.Equal(FieldState.Null, items[1].Tool.FunctionDisplayName.State);
        Assert.Equal(FieldState.Invalid, items[2].Tool.PluginName.State);
        Assert.Equal(FieldState.Invalid, items[2].Tool.FunctionDisplayName.State);
        Assert.Equal(FieldState.Null, items[3].Tool.PluginName.State);
        Assert.Equal(FieldState.Missing, items[3].Tool.FunctionDisplayName.State);
        Assert.Equal(FieldState.Invalid, items[4].Tool.PluginName.State);
        Assert.Equal(FieldState.Invalid, items[4].Tool.FunctionDisplayName.State);
        Assert.Equal(EvidenceValidation.Partial, result.ToolExecutions.Validation);
    }

    [Fact]
    public void Decode_IdenticalRows_StaySeparateObservationsWithDistinctIds()
    {
        var row = Execution("Demo Search", "Find", "200");

        var result = Decode(DeveloperInfo(Executions(row, row)));

        Assert.Equal(2, result.ToolExecutions.Items.Length);
        Assert.Equal("tool-execution-0", result.ToolExecutions.Items[0].ObservationId);
        Assert.Equal("tool-execution-1", result.ToolExecutions.Items[1].ObservationId);
        Assert.All(result.ToolExecutions.Items, e => Assert.Equal(FieldState.Missing, e.ProviderInvocationId.State));
    }

    [Fact]
    public void Decode_UnknownFields_AreCountedWithPointers()
    {
        var result = Decode(DeveloperInfo("{\"plugins\":{\"selectedFunctions\":[{\"plugin\":{\"name\":\"P\",\"extra\":1},\"functionDisplayName\":\"F\",\"other\":true}],\"executions\":[]},\"meta\":{}}"));

        Assert.Equal(EvidenceValidation.Valid, result.ToolSelections.Validation);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Native118DiagnosticsDecoder.UnknownFieldsIssueCode, issue.Code);
        Assert.Equal(
            new[]
            {
                "/artifacts/0/parts/0/data/meta",
                "/artifacts/0/parts/0/data/plugins/selectedFunctions/0/other",
                "/artifacts/0/parts/0/data/plugins/selectedFunctions/0/plugin/extra",
            },
            issue.Sources.Select(s => s.JsonPointer).ToArray());
    }

    [Fact]
    public void Decode_TransportIsUnknownByRule_EvenWhenANameContainsMcp()
    {
        var result = Decode(DeveloperInfo(Executions(Execution("mcp-server", "mcp_tool", "200"))));

        var tool = Assert.Single(result.ToolExecutions.Items).Tool;
        Assert.Equal(ToolTransport.Unknown, tool.Transport.Value);
        Assert.Equal(EvidenceDerivation.ParserProjection, tool.Transport.Derivation);
        Assert.Equal(Native118DiagnosticsDecoder.TransportRuleId, tool.Transport.DerivationRuleId);
        Assert.Equal(FieldState.Missing, tool.McpServerIdentity.State);
        Assert.Equal(FieldState.Missing, tool.McpToolName.State);
        Assert.Equal(FieldState.Missing, tool.CanonicalName.State);
        Assert.Null(tool.AliasMappingVersion);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Retrieval Info
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_RetrievalStatusMissing_IsMissingNeverSuccess()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"OneDriveAndSharePoint\",\"debugInfo\":{\"retrievalHits\":[]}}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(FieldState.Missing, retrieval.ProviderStatus.State);
        Assert.Equal(ExecutionOutcome.Unknown, retrieval.Outcome.Value);
        Assert.Equal(EvidenceDerivation.ParserProjection, retrieval.Outcome.Derivation);
        Assert.Equal(EvidenceValidation.Valid, result.RetrievalExecutions.Validation);
    }

    [Theory]
    [InlineData("\"Success\"", ExecutionOutcome.Succeeded, FieldState.Present)]
    [InlineData("\"success\"", ExecutionOutcome.Succeeded, FieldState.Present)]
    [InlineData("\"failed\"", ExecutionOutcome.Failed, FieldState.Present)]
    [InlineData("\"FAILURE\"", ExecutionOutcome.Failed, FieldState.Present)]
    [InlineData("\"error\"", ExecutionOutcome.Failed, FieldState.Present)]
    [InlineData("\"partial\"", ExecutionOutcome.Unknown, FieldState.Present)]
    [InlineData("null", ExecutionOutcome.Unknown, FieldState.Null)]
    [InlineData("1", ExecutionOutcome.Unknown, FieldState.Invalid)]
    public void Decode_RetrievalStatus_MapsThroughTheVersionedRule(string statusJson, ExecutionOutcome expectedOutcome, FieldState expectedState)
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"status\":" + statusJson + "}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(expectedState, retrieval.ProviderStatus.State);
        Assert.Equal(expectedOutcome, retrieval.Outcome.Value);
        Assert.Equal(Native118DiagnosticsDecoder.OutcomeRuleId, retrieval.Outcome.DerivationRuleId);
    }

    [Fact]
    public void Decode_UnrankedHit_IsKeptWithRankMissing()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"retrievalHits\":[{\"webUrl\":\"https://a.example.invalid\",\"extracts\":[{\"text\":\"p\"}]},{\"webUrl\":\"https://b.example.invalid\",\"rank\":2}]}}")));

        var hits = Assert.Single(result.RetrievalExecutions.Items).Hits;
        Assert.Equal(EvidenceValidation.Valid, hits.Validation);
        Assert.Equal(2, hits.Items.Length);
        Assert.Equal(FieldState.Missing, hits.Items[0].Rank.State);
        Assert.Equal("https://a.example.invalid", hits.Items[0].WebUrl.Value);
        Assert.Equal("p", Assert.Single(hits.Items[0].Passages.Items).Text.Value);
        Assert.Equal(2, hits.Items[1].Rank.Value);
        Assert.Equal(EvidenceAvailability.NotProvided, hits.Items[1].Passages.Availability);
        Assert.Equal(EvidenceValidation.Valid, result.RetrievalExecutions.Validation);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"1\"")]
    [InlineData("1.5")]
    public void Decode_RankNotAnInteger_IsInvalidAndTheHitIsKept(string rankJson)
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"retrievalHits\":[{\"webUrl\":\"https://a.example.invalid\",\"rank\":" + rankJson + "}]}}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        var hit = Assert.Single(retrieval.Hits.Items);
        Assert.Equal(FieldState.Invalid, hit.Rank.State);
        Assert.Equal("https://a.example.invalid", hit.WebUrl.Value);
        Assert.Equal(EvidenceValidation.Partial, retrieval.Hits.Validation);
        Assert.Equal(EvidenceValidation.Partial, result.RetrievalExecutions.Validation);
    }

    [Fact]
    public void Decode_NullHit_IsKeptInvalid()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"retrievalHits\":[null]}}")));

        var hit = Assert.Single(Assert.Single(result.RetrievalExecutions.Items).Hits.Items);
        Assert.Equal(FieldState.Invalid, hit.WebUrl.State);
        Assert.Equal(FieldState.Invalid, hit.Rank.State);
        Assert.Equal(EvidenceValidation.Malformed, hit.Passages.Validation);
        Assert.Equal("retrieval-hit-0-0", hit.ObservationId);
    }

    [Fact]
    public void Decode_TotalResultCountAndReturnedHits_StaySeparate()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"totalResultCount\":100,\"maximumNumberOfResults\":2,\"retrievalHits\":[{\"webUrl\":\"https://a.example.invalid\"},{\"webUrl\":\"https://b.example.invalid\"}]}}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(100L, retrieval.TotalResultCount.Value);
        Assert.Equal(2, retrieval.Hits.Items.Length);
        Assert.Equal(2, Assert.Single(retrieval.Queries.Items).MaximumNumberOfResults.Value);
        Assert.Equal(FieldState.Missing, Assert.Single(retrieval.Queries.Items).QueryText.State);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("\"100\"")]
    public void Decode_TotalResultCountNotANonNegativeInteger_IsInvalid(string json)
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"totalResultCount\":" + json + "}}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(FieldState.Invalid, retrieval.TotalResultCount.State);
        Assert.Equal(EvidenceValidation.Partial, result.RetrievalExecutions.Validation);
    }

    [Theory]
    [InlineData("318", FieldState.Present)]
    [InlineData("0", FieldState.Present)]
    [InlineData("12.5", FieldState.Present)]
    [InlineData("-1", FieldState.Invalid)]
    [InlineData("\"318\"", FieldState.Invalid)]
    [InlineData("null", FieldState.Null)]
    public void Decode_LatencyMs_IsAFiniteNonNegativeNumberOrNothing(string json, FieldState expected)
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"latencyMs\":" + json + "}}")));

        Assert.Equal(expected, Assert.Single(result.RetrievalExecutions.Items).LatencyMilliseconds.State);
    }

    [Fact]
    public void Decode_DebugInfoAbsent_QueriesAndHitsAreNotProvidedAndFieldsMissing()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"status\":\"Success\"}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(EvidenceAvailability.NotProvided, retrieval.Queries.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, retrieval.Hits.Availability);
        Assert.Equal(FieldState.Missing, retrieval.LatencyMilliseconds.State);
        Assert.Equal(FieldState.Missing, retrieval.TotalResultCount.State);
        Assert.Equal(EvidenceValidation.Valid, result.RetrievalExecutions.Validation);
    }

    [Fact]
    public void Decode_DebugInfoNotAnObject_IsMalformedForItsChildren()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":null}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(EvidenceValidation.Malformed, retrieval.Queries.Validation);
        Assert.Equal(EvidenceValidation.Malformed, retrieval.Hits.Validation);
        Assert.Equal(FieldState.Invalid, retrieval.LatencyMilliseconds.State);
        Assert.Equal(EvidenceValidation.Partial, result.RetrievalExecutions.Validation);
        Assert.Contains(result.RetrievalExecutions.Issues, i => i.Code == Native118DiagnosticsDecoder.ContainerMalformedIssueCode);
    }

    [Fact]
    public void Decode_DebugInfoWithoutQueryFields_QueriesAreNotProvidedNotAnEmptyQuery()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"latencyMs\":1}}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(EvidenceAvailability.NotProvided, retrieval.Queries.Availability);
        Assert.Empty(retrieval.Queries.Items);
    }

    [Fact]
    public void Decode_UnnamedRetrievalRow_IsKeptWithCapabilityMissing()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"status\":\"failed\"}")));

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal(FieldState.Missing, retrieval.Capability.State);
        Assert.Equal(ExecutionOutcome.Failed, retrieval.Outcome.Value);
    }

    [Fact]
    public void Decode_EmptyExecutions_IsObservedEmpty()
    {
        var result = Decode(RetrievalInfo("{\"executions\":[]}"));

        Assert.True(result.RetrievalExecutions.IsObservedEmpty);
    }

    [Fact]
    public void Decode_ExtractWithoutText_IsAPassageWithTextMissing()
    {
        var result = Decode(RetrievalInfo(Retrieval("{\"name\":\"X\",\"debugInfo\":{\"retrievalHits\":[{\"extracts\":[{},{\"text\":5},\"junk\"]}]}}")));

        var passages = Assert.Single(Assert.Single(result.RetrievalExecutions.Items).Hits.Items).Passages;
        Assert.Equal(3, passages.Items.Length);
        Assert.Equal(FieldState.Missing, passages.Items[0].Text.State);
        Assert.Equal(FieldState.Invalid, passages.Items[1].Text.State);
        Assert.Equal(FieldState.Invalid, passages.Items[2].Text.State);
        Assert.Equal(EvidenceValidation.Partial, passages.Validation);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Native import
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void DecodeNativeImport_EmptyRetrieval_IsObservedEmptyWithUnknownCoverageAndTheCollapseLoss()
    {
        var result = Native118DiagnosticsDecoder.DecodeNativeImport(ParseJson("{\"retrieval_executions\":[]}"), "cap-import");

        Assert.True(result.RetrievalExecutions.IsObservedEmpty);
        Assert.Equal(EvidenceCoverage.Unknown, result.RetrievalExecutions.Coverage);
        Assert.Equal(EvidenceOrigin.NativeEvaluationImport, Assert.Single(result.RetrievalExecutions.Sources).Origin);
        Assert.Equal("/retrieval_executions", Assert.Single(result.RetrievalExecutions.Sources).JsonPointer);
        Assert.Equal(EvidenceAvailability.NotProvided, result.ToolSelections.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, result.ApprovalIntents.Availability);
        var loss = Assert.Single(result.Issues);
        Assert.Equal(Native118DiagnosticsDecoder.ImportLossIssuePrefix + Native118DiagnosticsDecoder.LossCollapseToEmpty, loss.Code);
    }

    [Fact]
    public void DecodeNativeImport_NonEmptyRetrieval_NeverSucceedsAndNamesTheDroppedFields()
    {
        var result = Native118DiagnosticsDecoder.DecodeNativeImport(
            ParseJson("{\"retrieval_executions\":[{\"capability\":\"X\",\"status\":\"success\",\"queries\":[{\"queryString\":\"q\"}],\"totalResultCount\":3,\"retrievalHits\":[{\"webUrl\":\"https://a.example.invalid\",\"rank\":1,\"extracts\":[{\"text\":\"p\"}]}]}]}"),
            "cap-import");

        var retrieval = Assert.Single(result.RetrievalExecutions.Items);
        Assert.Equal("success", retrieval.ProviderStatus.Value);
        Assert.Equal(ExecutionOutcome.Unknown, retrieval.Outcome.Value);
        Assert.Equal(Native118DiagnosticsDecoder.ImportOutcomeRuleId, retrieval.Outcome.DerivationRuleId);
        Assert.Equal("q", Assert.Single(retrieval.Queries.Items).QueryText.Value);
        Assert.Equal(FieldState.Missing, retrieval.LatencyMilliseconds.State);
        Assert.Equal(3L, retrieval.TotalResultCount.Value);
        Assert.Equal(1, Assert.Single(retrieval.Hits.Items).Rank.Value);
        var losses = result.Issues.Where(i => i.Code.StartsWith(Native118DiagnosticsDecoder.ImportLossIssuePrefix, StringComparison.Ordinal)).Select(i => i.Code).ToArray();
        Assert.Equal(
            new[]
            {
                Native118DiagnosticsDecoder.ImportLossIssuePrefix + Native118DiagnosticsDecoder.LossDroppedFields,
                Native118DiagnosticsDecoder.ImportLossIssuePrefix + Native118DiagnosticsDecoder.LossStatusDefault,
                Native118DiagnosticsDecoder.ImportLossIssuePrefix + Native118DiagnosticsDecoder.LossUnrankedHits,
            },
            losses);
    }

    [Fact]
    public void DecodeNativeImport_Tools_AreDecodedFromThePersistedNames()
    {
        var result = Native118DiagnosticsDecoder.DecodeNativeImport(
            ParseJson("{\"selected_functions\":[{\"plugin_name\":\"P\",\"function_name\":\"F\"}],\"executions\":[{\"plugin_name\":\"P\",\"function_name\":\"F\",\"response_status\":true}]}"),
            "cap-import");

        Assert.Equal("F", Assert.Single(result.ToolSelections.Items).Tool.FunctionDisplayName.Value);
        var execution = Assert.Single(result.ToolExecutions.Items);
        Assert.Equal("P", execution.Tool.PluginName.Value);
        Assert.Equal(FieldState.Invalid, execution.ResponseStatus.State);
        Assert.Equal(EvidenceValidation.Partial, result.ToolExecutions.Validation);
        Assert.Equal(EvidenceAvailability.NotProvided, result.RetrievalExecutions.Availability);
    }

    [Fact]
    public void DecodeNativeImport_NotAnObject_IsMalformedEverywhere()
    {
        var result = Native118DiagnosticsDecoder.DecodeNativeImport(ParseJson("[]"), "cap-import");

        Assert.Equal(EvidenceValidation.Malformed, result.ToolSelections.Validation);
        Assert.Equal(EvidenceValidation.Malformed, result.RetrievalExecutions.Validation);
        Assert.Equal(Native118DiagnosticsDecoder.ImportNotObjectIssueCode, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Decode_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Native118DiagnosticsDecoder.Decode(null!, CaptureId));
        Assert.Throws<ArgumentException>(() => Native118DiagnosticsDecoder.Decode(Snapshot(), ""));
        Assert.Throws<ArgumentException>(() => Native118DiagnosticsDecoder.DecodeNativeImport(ParseJson("{}"), " "));
    }
}
