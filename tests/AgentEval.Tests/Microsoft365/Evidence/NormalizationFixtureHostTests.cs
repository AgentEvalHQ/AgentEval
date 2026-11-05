// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;
using static AgentEval.Tests.Microsoft365.Evidence.EvidenceTestKit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// The Appendix D fixture host (task M23 "done when"): every fixture of <c>normalization-fixtures.json</c> is fed
/// through the real normaliser and decoders, the result is serialised with <see cref="Microsoft365EvidenceJson"/>
/// and the fixture's <c>expected</c> must be a subset (<see cref="JsonSubset"/>) of a small projection of it.
/// <para>
/// How a fixture becomes an input: a completed <see cref="WorkIqTaskSnapshot"/> whose status message carries
/// <c>input.attributions</c> as <c>metadata.attributions</c> (when present) and whose artifacts are an
/// <c>Answer</c> artifact with one text part (<c>input.answerText</c>, when present), a <c>Developer Info</c>
/// artifact (<c>input.developerInfo</c>, media type <c>application/vnd.ms-workiq.developer-info</c>) and a
/// <c>Retrieval Info</c> artifact (<c>input.retrievalInfo</c>, media type
/// <c>application/vnd.ms-workiq-internal.retrieval</c>); <c>profile.developerInfoRequested</c> selects
/// <see cref="DiagnosticProfile.Native118Diagnostics"/>. A fixture with origin <c>NativeEvaluationImport</c> has no
/// A2A snapshot: its <c>input.nativeDiagnostics</c> goes to <see cref="Native118DiagnosticsDecoder.DecodeNativeImport"/>.
/// </para>
/// <para>
/// The projection (the test's own, not the evidence schema): for each channel <c>availability</c>, <c>validation</c>,
/// <c>coverage</c> (enum member names), <c>count</c> = <c>Items.Length</c>, <c>observedEmpty</c> =
/// <c>EvidenceSet.IsObservedEmpty</c>; <c>toolSelections.names</c> = the function display names;
/// <c>toolExecutions.rows[i]</c> = plugin / function (values, or the state as <c>…State</c>),
/// <c>providerInvocationIdState</c>, <c>argumentsState</c>, <c>resultState</c>, <c>responseStatus</c>(+<c>State</c>),
/// <c>outcome</c>; <c>distinctLocalObservationIds</c>; <c>providerInvocationIdStates</c>;
/// <c>retrievalExecutions.rows[i]</c> = <c>capability</c>, <c>providerStatusState</c>, <c>outcome</c>,
/// <c>queryTextState</c> (of the single query, <c>Missing</c> when none), <c>maximumNumberOfResults</c>,
/// <c>totalResultCount</c>, <c>returnedHitCount</c> = <c>Hits.Items.Length</c>, <c>hits[j]</c> = <c>rankState</c>,
/// <c>rank</c>, <c>passages</c> (texts); <c>citations</c> = <c>markerCount</c>, <c>referenceOccurrenceCount</c>
/// (sum of keys), <c>distinctReferenceKeys</c> (first-appearance order), <c>markerKeys</c>, <c>bindings</c> (one per
/// link); <c>rawAnswerUnchanged</c> = raw text equals <c>input.answerText</c>; <c>losses</c> = the ids of the
/// <c>import.loss.*</c> issues; <c>legacyToolUsageReport</c>/<c>legacyToolTimeline</c> = <c>null</c> by
/// construction (the evidence document has no such members; D8 keeps them null on the <c>AgentResponse</c>, which
/// lane A's agent tests assert).
/// </para>
/// <para>
/// <c>metricEligibility</c> is computed from the evidence states alone: <c>retrievalRankAtK</c> = retrieval
/// Provided + Valid with at least one hit and every hit's rank Present; <c>retrievalCorpusRecall</c> = retrieval
/// coverage CompleteForDeclaredScope; <c>assertNoRetrievalPerformed</c> = retrieval observed-empty AND coverage
/// CompleteForDeclaredScope; <c>fullTurnExecutionCount</c> = executions Provided + Valid AND coverage
/// CompleteForDeclaredScope; <c>citationPresence</c> = markers Provided + Valid with at least one marker;
/// <c>citationCorrectness</c> = any link bound by an explicit provider key or reference URI.
/// </para>
/// </summary>
public sealed class NormalizationFixtureHostTests
{
    private const string Catalog = "normalization-fixtures.json";
    private const string ImportCaptureId = "cap-native-import-0001";
    private const string ImportOrigin = "NativeEvaluationImport";

    /// <summary>Every fixture id of the catalog.</summary>
    public static TheoryData<string> FixtureIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var fixture in LoadFixtures())
            {
                data.Add(fixture.GetProperty("id").GetString()!);
            }

            return data;
        }
    }

    /// <summary>The fixtures that model A2A artifacts (and therefore produce a whole evidence document).</summary>
    public static TheoryData<string> A2AFixtureIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var fixture in LoadFixtures().Where(f => !string.Equals(f.GetProperty("origin").GetString(), ImportOrigin, StringComparison.Ordinal)))
            {
                data.Add(fixture.GetProperty("id").GetString()!);
            }

            return data;
        }
    }

    [Fact]
    public void Catalog_Always_HasSevenUniqueFixtures()
    {
        var ids = LoadFixtures().Select(f => f.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(7, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Normalize_Fixture_ExpectedIsASubsetOfTheProjection(string id)
    {
        var fixture = LoadFixture(id);

        var projection = Project(fixture);

        JsonSubset.AssertSubset(fixture.GetProperty("expected"), projection);
    }

    [Theory]
    [MemberData(nameof(A2AFixtureIds))]
    public void Normalize_A2AFixture_RoundTripsThroughEvidenceJson(string id)
    {
        var fixture = LoadFixture(id);
        var evidence = NormalizeA2A(fixture);

        var json = Microsoft365EvidenceJson.Serialize(evidence);
        var reread = Microsoft365EvidenceJson.Deserialize(json);

        Assert.Equal(json, Microsoft365EvidenceJson.Serialize(reread));
        Assert.Equal(Microsoft365Evidence.SchemaVersion, reread.SchemaVersion);
    }

    [Theory]
    [MemberData(nameof(A2AFixtureIds))]
    public void Normalize_A2AFixture_IsDeterministic(string id)
    {
        var fixture = LoadFixture(id);

        var first = Microsoft365EvidenceJson.Serialize(NormalizeA2A(fixture));
        var second = Microsoft365EvidenceJson.Serialize(NormalizeA2A(LoadFixture(id)));

        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Host
    // ---------------------------------------------------------------------------------------------------------

    private static JsonElement Project(JsonElement fixture)
    {
        var input = fixture.GetProperty("input");
        if (string.Equals(fixture.GetProperty("origin").GetString(), ImportOrigin, StringComparison.Ordinal))
        {
            var result = Native118DiagnosticsDecoder.DecodeNativeImport(input.GetProperty("nativeDiagnostics"), ImportCaptureId);
            var markers = EvidenceSet<CitationMarker>.NotProvided(EvidenceScope.Response, default);
            return ToElement(Projection(
                result.ToolSelections,
                result.ToolExecutions,
                result.RetrievalExecutions,
                markers,
                ImmutableArray<CitationLink>.Empty,
                result.Issues,
                rawAnswerUnchanged: null));
        }

        var evidence = NormalizeA2A(fixture);
        bool? rawUnchanged = input.TryGetProperty("answerText", out var answerText)
            ? evidence.Answer is { RawText.IsPresent: true } answer && string.Equals(answer.RawText.Value, answerText.GetString(), StringComparison.Ordinal)
            : null;
        return ToElement(Projection(
            evidence.ToolSelections,
            evidence.ToolExecutions,
            evidence.RetrievalExecutions,
            evidence.CitationMarkers,
            evidence.CitationLinks,
            evidence.Issues,
            rawUnchanged));
    }

    private static Microsoft365TurnEvidence NormalizeA2A(JsonElement fixture)
    {
        var input = fixture.GetProperty("input");
        var profile = fixture.GetProperty("profile").GetProperty("developerInfoRequested").GetBoolean()
            ? DiagnosticProfile.Native118Diagnostics
            : DiagnosticProfile.PublicAnswerOnly;

        var artifacts = ImmutableArray.CreateBuilder<WorkIqArtifact>();
        if (input.TryGetProperty("answerText", out var answerText))
        {
            artifacts.Add(Artifact("artifact-answer-0001", "Answer", Text(answerText.GetString()!)));
        }

        if (input.TryGetProperty("developerInfo", out var developerInfo))
        {
            artifacts.Add(Artifact("artifact-devinfo-0001", Native118DiagnosticsDecoder.DeveloperInfoName, Data(developerInfo, Native118DiagnosticsDecoder.DeveloperInfoMediaType)));
        }

        if (input.TryGetProperty("retrievalInfo", out var retrievalInfo))
        {
            artifacts.Add(Artifact("artifact-retrieval-0001", Native118DiagnosticsDecoder.RetrievalInfoName, Data(retrievalInfo, Native118DiagnosticsDecoder.RetrievalInfoMediaType)));
        }

        var status = input.TryGetProperty("attributions", out var attributions)
            ? StatusWithAttributions(attributions.GetRawText())
            : Message("msg-status-0001", ImmutableArray<WorkIqPart>.Empty);

        var task = Snapshot(WorkIqTaskState.Completed, status, artifacts.ToImmutable());
        return Normalize(task, profile: profile);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Projection
    // ---------------------------------------------------------------------------------------------------------

    private static JsonObject Projection(
        EvidenceSet<ToolSelectionObservation> selections,
        EvidenceSet<ToolExecutionObservation> executions,
        EvidenceSet<RetrievalExecutionObservation> retrievals,
        EvidenceSet<CitationMarker> markers,
        ImmutableArray<CitationLink> links,
        ImmutableArray<EvidenceIssue> issues,
        bool? rawAnswerUnchanged)
    {
        var projection = new JsonObject
        {
            ["toolSelections"] = ProjectSelections(selections),
            ["toolExecutions"] = ProjectExecutions(executions),
            ["retrievalExecutions"] = ProjectRetrievals(retrievals),
            ["citations"] = ProjectCitations(markers, links),
            ["metricEligibility"] = ProjectEligibility(retrievals, executions, markers, links),
            ["losses"] = Arr(issues
                .Where(i => i.Code.StartsWith(Native118DiagnosticsDecoder.ImportLossIssuePrefix, StringComparison.Ordinal))
                .Select(i => Str(i.Code[Native118DiagnosticsDecoder.ImportLossIssuePrefix.Length..]))),
            ["legacyToolUsageReport"] = null,
            ["legacyToolTimeline"] = null,
        };
        if (rawAnswerUnchanged is { } unchanged)
        {
            projection["rawAnswerUnchanged"] = unchanged;
        }

        return projection;
    }

    private static JsonObject ProjectSet<T>(EvidenceSet<T> set) => new()
    {
        ["availability"] = set.Availability.ToString(),
        ["validation"] = set.Validation.ToString(),
        ["coverage"] = set.Coverage.ToString(),
        ["count"] = set.Items.Length,
        ["observedEmpty"] = set.IsObservedEmpty,
    };

    private static JsonObject ProjectSelections(EvidenceSet<ToolSelectionObservation> set)
    {
        var projection = ProjectSet(set);
        projection["names"] = Arr(set.Items.Select(s => Str(ValueOrNull(s.Tool.FunctionDisplayName))));
        return projection;
    }

    private static JsonObject ProjectExecutions(EvidenceSet<ToolExecutionObservation> set)
    {
        var projection = ProjectSet(set);
        projection["rows"] = Arr(set.Items.Select(e => (JsonNode?)new JsonObject
        {
            ["plugin"] = Str(ValueOrNull(e.Tool.PluginName)),
            ["pluginState"] = e.Tool.PluginName.State.ToString(),
            ["function"] = Str(ValueOrNull(e.Tool.FunctionDisplayName)),
            ["functionState"] = e.Tool.FunctionDisplayName.State.ToString(),
            ["providerInvocationIdState"] = e.ProviderInvocationId.State.ToString(),
            ["argumentsState"] = e.Arguments.State.ToString(),
            ["resultState"] = e.Result.State.ToString(),
            ["responseStatus"] = e.ResponseStatus.IsPresent ? e.ResponseStatus.Value : (JsonNode?)null,
            ["responseStatusState"] = e.ResponseStatus.State.ToString(),
            ["outcome"] = e.Outcome.IsPresent ? e.Outcome.Value.ToString() : e.Outcome.State.ToString(),
        }));
        projection["distinctLocalObservationIds"] = set.Items.Select(e => e.ObservationId).Distinct(StringComparer.Ordinal).Count();
        projection["providerInvocationIdStates"] = Arr(set.Items.Select(e => Str(e.ProviderInvocationId.State.ToString())));
        return projection;
    }

    private static JsonObject ProjectRetrievals(EvidenceSet<RetrievalExecutionObservation> set)
    {
        var projection = ProjectSet(set);
        projection["rows"] = Arr(set.Items.Select(r => (JsonNode?)new JsonObject
        {
            ["capability"] = Str(ValueOrNull(r.Capability)),
            ["capabilityState"] = r.Capability.State.ToString(),
            ["providerStatus"] = Str(ValueOrNull(r.ProviderStatus)),
            ["providerStatusState"] = r.ProviderStatus.State.ToString(),
            ["outcome"] = r.Outcome.IsPresent ? r.Outcome.Value.ToString() : r.Outcome.State.ToString(),
            ["queryTextState"] = r.Queries.Items.Length > 0 ? r.Queries.Items[0].QueryText.State.ToString() : FieldState.Missing.ToString(),
            ["maximumNumberOfResults"] = r.Queries.Items.Length > 0 && r.Queries.Items[0].MaximumNumberOfResults.IsPresent
                ? r.Queries.Items[0].MaximumNumberOfResults.Value
                : (JsonNode?)null,
            ["totalResultCount"] = r.TotalResultCount.IsPresent ? r.TotalResultCount.Value : (JsonNode?)null,
            ["totalResultCountState"] = r.TotalResultCount.State.ToString(),
            ["latencyMsState"] = r.LatencyMilliseconds.State.ToString(),
            ["returnedHitCount"] = r.Hits.Items.Length,
            ["hits"] = Arr(r.Hits.Items.Select(h => (JsonNode?)new JsonObject
            {
                ["rankState"] = h.Rank.State.ToString(),
                ["rank"] = h.Rank.IsPresent ? h.Rank.Value : (JsonNode?)null,
                ["webUrlState"] = h.WebUrl.State.ToString(),
                ["passages"] = Arr(h.Passages.Items.Select(p => Str(ValueOrNull(p.Text)))),
            })),
        }));
        return projection;
    }

    private static JsonObject ProjectCitations(EvidenceSet<CitationMarker> markers, ImmutableArray<CitationLink> links)
    {
        var distinct = new List<string>();
        foreach (var key in markers.Items.SelectMany(m => m.ReferenceKeys))
        {
            if (!distinct.Contains(key, StringComparer.Ordinal))
            {
                distinct.Add(key);
            }
        }

        return new JsonObject
        {
            ["markerCount"] = markers.Items.Length,
            ["referenceOccurrenceCount"] = markers.Items.Sum(m => m.ReferenceKeys.Length),
            ["distinctReferenceKeys"] = Arr(distinct.Select(Str)),
            ["markerKeys"] = Arr(markers.Items.Select(m => (JsonNode?)Arr(m.ReferenceKeys.Select(Str)))),
            ["bindings"] = Arr(links.Select(l => Str(l.Binding.ToString()))),
        };
    }

    private static JsonObject ProjectEligibility(
        EvidenceSet<RetrievalExecutionObservation> retrievals,
        EvidenceSet<ToolExecutionObservation> executions,
        EvidenceSet<CitationMarker> markers,
        ImmutableArray<CitationLink> links)
    {
        var hits = retrievals.Items.SelectMany(r => r.Hits.Items).ToArray();
        var retrievalValid = retrievals.IsProvided && retrievals.Validation == EvidenceValidation.Valid;
        var complete = retrievals.Coverage == EvidenceCoverage.CompleteForDeclaredScope;
        return new JsonObject
        {
            ["retrievalRankAtK"] = retrievalValid && hits.Length > 0 && hits.All(h => h.Rank.IsPresent),
            ["retrievalCorpusRecall"] = complete,
            ["assertNoRetrievalPerformed"] = retrievals.IsObservedEmpty && complete,
            ["fullTurnExecutionCount"] = executions.IsProvided && executions.Validation == EvidenceValidation.Valid && executions.Coverage == EvidenceCoverage.CompleteForDeclaredScope,
            ["citationPresence"] = markers.IsProvided && markers.Validation == EvidenceValidation.Valid && markers.Items.Length > 0,
            ["citationCorrectness"] = links.Any(l => l.Binding is CitationBinding.ExplicitProviderKey or CitationBinding.ExplicitReferenceUri),
        };
    }

    private static string? ValueOrNull(Observed<string> observed) => observed.IsPresent ? observed.Value : null;

    private static JsonNode? Str(string? text) => text is null ? null : JsonValue.Create(text);

    private static JsonArray Arr(IEnumerable<JsonNode?> nodes) => new(nodes.ToArray());

    private static JsonElement ToElement(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Catalog
    // ---------------------------------------------------------------------------------------------------------

    private static List<JsonElement> LoadFixtures()
    {
        using var document = JsonDocument.Parse(FixtureLoader.Text(Catalog));
        return document.RootElement.GetProperty("fixtures").EnumerateArray().Select(f => f.Clone()).ToList();
    }

    private static JsonElement LoadFixture(string id) =>
        LoadFixtures().Single(f => string.Equals(f.GetProperty("id").GetString(), id, StringComparison.Ordinal));
}
