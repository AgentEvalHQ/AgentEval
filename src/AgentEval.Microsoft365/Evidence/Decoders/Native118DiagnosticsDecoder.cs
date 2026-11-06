// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using static AgentEval.Microsoft365.Evidence.Decoders.EvidenceJsonReader;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Decodes the diagnostic artifacts observed in the native evaluator 1.18.0 (experimental, not a documented
/// contract): <c>Developer Info</c> → tool selections and executions (<c>responseStatus</c> integer only;
/// missing args/results/ids stay <see cref="FieldState.Missing"/>), <c>Retrieval Info</c> → retrieval executions
/// (missing status is not success; <c>totalResultCount</c> is not the returned count; unranked hits kept). Approval
/// intents are not a diagnostic artifact: the normaliser decodes them from the status message with
/// <see cref="InteractionDecoder"/> under every profile. Artifacts are found by media type or name; the first
/// object-valued data part is used; several distinct artifacts with the same role yield
/// <see cref="EvidenceValidation.Conflicting"/>, never last-wins. Every row of every list becomes an observation
/// (a non-object row has Invalid fields), identical rows stay separate, coverage is always
/// <see cref="EvidenceCoverage.Unknown"/> (the artifacts declare none) and the scope is
/// <see cref="EvidenceScope.Unknown"/> (whether an artifact lists the task's or the conversation's activity is not
/// declared). Fields outside the known shapes are counted in one <see cref="UnknownFieldsIssueCode"/> issue.
/// </summary>
internal static class Native118DiagnosticsDecoder
{
    /// <summary>The media type of the <c>Developer Info</c> artifact's data part.</summary>
    public const string DeveloperInfoMediaType = "application/vnd.ms-workiq.developer-info";

    /// <summary>The media type of the <c>Retrieval Info</c> artifact's data part.</summary>
    public const string RetrievalInfoMediaType = "application/vnd.ms-workiq-internal.retrieval";

    /// <summary>The artifact name of the tool diagnostics.</summary>
    public const string DeveloperInfoName = "Developer Info";

    /// <summary>The artifact name of the retrieval diagnostics.</summary>
    public const string RetrievalInfoName = "Retrieval Info";

    /// <summary>The rule id of the outcome classification of native 1.18 shapes: no outcome field → Unknown; retrieval <c>status</c> success → Succeeded, failed/failure/error → Failed, else Unknown.</summary>
    public const string OutcomeRuleId = "outcome-map.native118/v1";

    /// <summary>The rule id of the transport classification: Mcp only from an explicit provenance field, of which the native 1.18 shapes define none → Unknown (<see cref="ToolIdentity.FromReportedNames"/>).</summary>
    public const string TransportRuleId = ToolIdentity.NamesOnlyTransportRuleId;

    /// <summary>Issue: two or more distinct artifacts play the same diagnostic role; nothing was chosen.</summary>
    public const string AmbiguousArtifactIssueCode = "diagnostics.ambiguous_artifact";

    /// <summary>Issue: a diagnostic artifact has no object-valued data part.</summary>
    public const string NoObjectDataPartIssueCode = "diagnostics.no_object_data_part";

    /// <summary>Issue: fields outside the native 1.18 shapes were ignored (pointers in the sources).</summary>
    public const string UnknownFieldsIssueCode = "diagnostics.unknown_fields";

    /// <summary>Issue: a container that should be an object is absent, null or of another type.</summary>
    public const string ContainerMalformedIssueCode = "diagnostics.container_malformed";

    /// <summary>Issue: a list field is an explicit null.</summary>
    public const string ArrayNullIssueCode = "diagnostics.array_null";

    /// <summary>Issue: a list field is present but not an array.</summary>
    public const string ArrayNotArrayIssueCode = "diagnostics.array_not_array";

    /// <summary>Issue: a list row is not a JSON object; its fields are Invalid and the row is kept.</summary>
    public const string RowNotObjectIssueCode = "diagnostics.row_not_object";

    /// <summary>Issue: a field has the wrong JSON type (a bool where an integer is expected, a negative count…).</summary>
    public const string FieldInvalidIssueCode = "diagnostics.field_invalid";

    private const int MaxUnknownFieldPointers = 64;

    private static readonly IReadOnlySet<string> DeveloperRootKeys = new HashSet<string>(StringComparer.Ordinal) { "plugins" };
    private static readonly IReadOnlySet<string> PluginsKeys = new HashSet<string>(StringComparer.Ordinal) { "selectedFunctions", "executions" };
    private static readonly IReadOnlySet<string> SelectionRowKeys = new HashSet<string>(StringComparer.Ordinal) { "plugin", "functionDisplayName" };
    private static readonly IReadOnlySet<string> PluginKeys = new HashSet<string>(StringComparer.Ordinal) { "name" };
    private static readonly IReadOnlySet<string> ExecutionRowKeys = new HashSet<string>(StringComparer.Ordinal) { "function", "executionStatus" };
    private static readonly IReadOnlySet<string> FunctionKeys = new HashSet<string>(StringComparer.Ordinal) { "plugin", "functionDisplayName" };
    private static readonly IReadOnlySet<string> ExecutionStatusKeys = new HashSet<string>(StringComparer.Ordinal) { "responseStatus" };
    private static readonly IReadOnlySet<string> RetrievalRootKeys = new HashSet<string>(StringComparer.Ordinal) { "executions" };
    private static readonly IReadOnlySet<string> RetrievalRowKeys = new HashSet<string>(StringComparer.Ordinal) { "name", "status", "debugInfo" };
    private static readonly IReadOnlySet<string> DebugInfoKeys = new HashSet<string>(StringComparer.Ordinal) { "queryString", "filterExpression", "maximumNumberOfResults", "totalResultCount", "latencyMs", "retrievalHits" };
    private static readonly IReadOnlySet<string> HitKeys = new HashSet<string>(StringComparer.Ordinal) { "webUrl", "rank", "extracts", "resourceMetadata" };
    private static readonly IReadOnlySet<string> ExtractKeys = new HashSet<string>(StringComparer.Ordinal) { "text" };

    /// <summary>The decoded diagnostic channels of one task snapshot.</summary>
    /// <param name="ToolSelections">Reported tool selections.</param>
    /// <param name="ToolExecutions">Reported tool executions.</param>
    /// <param name="RetrievalExecutions">Reported retrieval executions.</param>
    /// <param name="Issues">Decoder-level issues.</param>
    internal sealed record Result(
        EvidenceSet<ToolSelectionObservation> ToolSelections,
        EvidenceSet<ToolExecutionObservation> ToolExecutions,
        EvidenceSet<RetrievalExecutionObservation> RetrievalExecutions,
        ImmutableArray<EvidenceIssue> Issues);

    /// <summary>Decodes one row of a list; <paramref name="invalid"/> reports whether any field of the row was invalid.</summary>
    private delegate T RowDecoder<T>(JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> issues, out bool invalid);

    private enum LocateKind
    {
        Absent,
        Found,
        NoObjectDataPart,
        Conflicting,
    }

    private sealed record Located(LocateKind Kind, JsonElement Data, SourceRef Source, ImmutableArray<SourceRef> ArtifactSources);

    /// <summary>Decodes the diagnostic artifacts and data parts of one task snapshot.</summary>
    /// <param name="task">The final task snapshot.</param>
    /// <param name="captureId">The capture the snapshot was read from (for source pointers).</param>
    public static Result Decode(WorkIqTaskSnapshot task, string captureId)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var artifactsSource = new SourceRef(captureId, 0, EvidenceOrigin.A2AArtifact, "/artifacts");
        var issues = new List<EvidenceIssue>();
        var unknown = new List<SourceRef>();

        var developer = Locate(task, artifactsSource, DeveloperInfoMediaType, DeveloperInfoName);
        EvidenceSet<ToolSelectionObservation> selections;
        EvidenceSet<ToolExecutionObservation> executions;
        switch (developer.Kind)
        {
            case LocateKind.Found:
                DecodeDeveloperInfo(developer.Data, developer.Source, out selections, out executions, issues, unknown);
                break;
            case LocateKind.Conflicting:
                issues.Add(AmbiguousArtifact(DeveloperInfoName, developer.ArtifactSources));
                selections = Conflicting<ToolSelectionObservation>(developer.ArtifactSources);
                executions = Conflicting<ToolExecutionObservation>(developer.ArtifactSources);
                break;
            case LocateKind.NoObjectDataPart:
                issues.Add(NoObjectDataPart(DeveloperInfoName, developer.Source));
                selections = MalformedSet<ToolSelectionObservation>(developer.Source);
                executions = MalformedSet<ToolExecutionObservation>(developer.Source);
                break;
            default:
                selections = EvidenceSet<ToolSelectionObservation>.NotProvided(EvidenceScope.Unknown, One(artifactsSource));
                executions = EvidenceSet<ToolExecutionObservation>.NotProvided(EvidenceScope.Unknown, One(artifactsSource));
                break;
        }

        var retrieval = Locate(task, artifactsSource, RetrievalInfoMediaType, RetrievalInfoName);
        EvidenceSet<RetrievalExecutionObservation> retrievals;
        switch (retrieval.Kind)
        {
            case LocateKind.Found:
                retrievals = DecodeRetrievalInfo(retrieval.Data, retrieval.Source, issues, unknown);
                break;
            case LocateKind.Conflicting:
                issues.Add(AmbiguousArtifact(RetrievalInfoName, retrieval.ArtifactSources));
                retrievals = Conflicting<RetrievalExecutionObservation>(retrieval.ArtifactSources);
                break;
            case LocateKind.NoObjectDataPart:
                issues.Add(NoObjectDataPart(RetrievalInfoName, retrieval.Source));
                retrievals = MalformedSet<RetrievalExecutionObservation>(retrieval.Source);
                break;
            default:
                retrievals = EvidenceSet<RetrievalExecutionObservation>.NotProvided(EvidenceScope.Unknown, One(artifactsSource));
                break;
        }

        AddUnknownFieldsIssue(issues, unknown);
        return new Result(selections, executions, retrievals, issues.ToImmutableArray());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Locating artifacts
    // ---------------------------------------------------------------------------------------------------------

    private static Located Locate(WorkIqTaskSnapshot task, SourceRef artifactsSource, string mediaType, string name)
    {
        var matches = new List<(int Index, WorkIqArtifact Artifact)>();
        for (var i = 0; i < task.Artifacts.Length; i++)
        {
            var artifact = task.Artifacts[i];
            if (HasMediaType(artifact, mediaType) || string.Equals(artifact.Name, name, StringComparison.Ordinal))
            {
                matches.Add((i, artifact));
            }
        }

        if (matches.Count == 0)
        {
            return new Located(LocateKind.Absent, default, artifactsSource, ImmutableArray<SourceRef>.Empty);
        }

        var artifactSources = matches
            .Select(m => Child(artifactsSource, m.Index) with { ArtifactId = m.Artifact.ArtifactId, ArtifactRevision = m.Artifact.Revision })
            .ToImmutableArray();
        if (matches.Count > 1)
        {
            return new Located(LocateKind.Conflicting, default, artifactsSource, artifactSources);
        }

        var (_, found) = matches[0];
        var artifactSource = artifactSources[0];
        for (var j = 0; j < found.Parts.Length; j++)
        {
            var part = found.Parts[j];
            if (part.Kind == WorkIqPartKind.Data && part.Data is { ValueKind: JsonValueKind.Object } data)
            {
                var dataSource = Child(Child(artifactSource, "parts"), j) with { PartIndex = j };
                return new Located(LocateKind.Found, data, Child(dataSource, "data"), artifactSources);
            }
        }

        return new Located(LocateKind.NoObjectDataPart, default, artifactSource, artifactSources);
    }

    private static bool HasMediaType(WorkIqArtifact artifact, string mediaType) =>
        artifact.Parts.Any(p => p.MediaType is not null && string.Equals(p.MediaType, mediaType, StringComparison.OrdinalIgnoreCase));

    private static EvidenceSet<T> Conflicting<T>(ImmutableArray<SourceRef> artifactSources) =>
        EvidenceSet<T>.Provided(ImmutableArray<T>.Empty, EvidenceValidation.Conflicting, EvidenceCoverage.Unknown, EvidenceScope.Unknown, artifactSources);

    private static EvidenceIssue AmbiguousArtifact(string role, ImmutableArray<SourceRef> artifactSources) =>
        new(
            AmbiguousArtifactIssueCode,
            FormattableString.Invariant($"{artifactSources.Length} distinct artifacts play the '{role}' role with no declared relationship; none was chosen (no last-wins) and all are referenced."),
            artifactSources);

    private static EvidenceIssue NoObjectDataPart(string role, SourceRef artifactSource) =>
        Issue(NoObjectDataPartIssueCode, FormattableString.Invariant($"The '{role}' artifact has no data part whose data is a JSON object; nothing could be read from it."), artifactSource);

    private static void AddUnknownFieldsIssue(List<EvidenceIssue> issues, List<SourceRef> unknown)
    {
        if (unknown.Count > 0)
        {
            issues.Add(new EvidenceIssue(
                UnknownFieldsIssueCode,
                FormattableString.Invariant($"{unknown.Count} field(s) outside the native 1.18 shapes were ignored (kept raw in the capture); the source pointers name them."),
                unknown.Take(MaxUnknownFieldPointers).ToImmutableArray()));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Developer Info
    // ---------------------------------------------------------------------------------------------------------

    private static void DecodeDeveloperInfo(
        JsonElement data,
        SourceRef dataSource,
        out EvidenceSet<ToolSelectionObservation> selections,
        out EvidenceSet<ToolExecutionObservation> executions,
        List<EvidenceIssue> issues,
        List<SourceRef> unknown)
    {
        CollectUnknownFields(data, dataSource, DeveloperRootKeys, unknown);
        if (!TryGetProperty(data, "plugins", out var plugins))
        {
            issues.Add(Issue(ContainerMalformedIssueCode, "Developer Info has no 'plugins' object; no tool selection or execution was reported.", dataSource));
            selections = EvidenceSet<ToolSelectionObservation>.NotProvided(EvidenceScope.Unknown, One(dataSource));
            executions = EvidenceSet<ToolExecutionObservation>.NotProvided(EvidenceScope.Unknown, One(dataSource));
            return;
        }

        var pluginsSource = Child(dataSource, "plugins");
        if (plugins.ValueKind != JsonValueKind.Object)
        {
            var issue = Issue(ContainerMalformedIssueCode, "Developer Info 'plugins' is not a JSON object; its lists cannot be read.", pluginsSource);
            selections = MalformedSet<ToolSelectionObservation>(pluginsSource, ImmutableArray.Create(issue));
            executions = MalformedSet<ToolExecutionObservation>(pluginsSource, ImmutableArray.Create(issue));
            return;
        }

        CollectUnknownFields(plugins, pluginsSource, PluginsKeys, unknown);
        selections = DecodeRows<ToolSelectionObservation>(
            plugins,
            "selectedFunctions",
            pluginsSource,
            (JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> rowIssues, out bool invalid) =>
                DecodeSelectionRow(row, rowSource, index, rowIssues, unknown, out invalid),
            InvalidSelectionRow);
        executions = DecodeRows<ToolExecutionObservation>(
            plugins,
            "executions",
            pluginsSource,
            (JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> rowIssues, out bool invalid) =>
                DecodeExecutionRow(row, rowSource, index, rowIssues, unknown, out invalid),
            InvalidExecutionRow);
    }

    private static ToolSelectionObservation DecodeSelectionRow(JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> issues, List<SourceRef> unknown, out bool invalid)
    {
        CollectUnknownFields(row, rowSource, SelectionRowKeys, unknown);
        var plugin = ReadPluginName(row, rowSource, unknown);
        var function = ReadString(row, "functionDisplayName", rowSource);
        NoteInvalid(issues, FieldInvalidIssueCode, "plugin.name", plugin);
        NoteInvalid(issues, FieldInvalidIssueCode, "functionDisplayName", function);
        invalid = IsInvalid(plugin) || IsInvalid(function);
        return new ToolSelectionObservation(ObservationId("tool-selection", index), Observed<string>.Missing(), ToolIdentity.FromReportedNames(plugin, function, One(rowSource)), One(rowSource));
    }

    private static ToolSelectionObservation InvalidSelectionRow(SourceRef rowSource, int index)
    {
        var invalid = Observed<string>.Invalid(One(rowSource));
        return new ToolSelectionObservation(ObservationId("tool-selection", index), Observed<string>.Missing(), ToolIdentity.FromReportedNames(invalid, invalid, One(rowSource)), One(rowSource));
    }

    private static ToolExecutionObservation DecodeExecutionRow(JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> issues, List<SourceRef> unknown, out bool invalid)
    {
        CollectUnknownFields(row, rowSource, ExecutionRowKeys, unknown);

        Observed<string> plugin;
        Observed<string> function;
        if (!TryGetProperty(row, "function", out var descriptor))
        {
            plugin = Observed<string>.Missing();
            function = Observed<string>.Missing();
        }
        else
        {
            var descriptorSource = Child(rowSource, "function");
            switch (descriptor.ValueKind)
            {
                case JsonValueKind.Null:
                    plugin = Observed<string>.Null(One(descriptorSource));
                    function = Observed<string>.Null(One(descriptorSource));
                    break;
                case JsonValueKind.Object:
                    CollectUnknownFields(descriptor, descriptorSource, FunctionKeys, unknown);
                    plugin = ReadPluginName(descriptor, descriptorSource, unknown);
                    function = ReadString(descriptor, "functionDisplayName", descriptorSource);
                    break;
                default:
                    plugin = Observed<string>.Invalid(One(descriptorSource));
                    function = Observed<string>.Invalid(One(descriptorSource));
                    break;
            }
        }

        Observed<int> status;
        if (!TryGetProperty(row, "executionStatus", out var executionStatus))
        {
            status = Observed<int>.Missing();
        }
        else
        {
            var statusSource = Child(rowSource, "executionStatus");
            switch (executionStatus.ValueKind)
            {
                case JsonValueKind.Null:
                    status = Observed<int>.Null(One(statusSource));
                    break;
                case JsonValueKind.Object:
                    CollectUnknownFields(executionStatus, statusSource, ExecutionStatusKeys, unknown);
                    status = ReadInteger(executionStatus, "responseStatus", statusSource);
                    break;
                default:
                    status = Observed<int>.Invalid(One(statusSource));
                    break;
            }
        }

        NoteInvalid(issues, FieldInvalidIssueCode, "function.plugin.name", plugin);
        NoteInvalid(issues, FieldInvalidIssueCode, "function.functionDisplayName", function);
        NoteInvalid(issues, FieldInvalidIssueCode, "executionStatus.responseStatus", status);
        invalid = IsInvalid(plugin) || IsInvalid(function) || IsInvalid(status);
        return ExecutionOf(index, plugin, function, status, rowSource);
    }

    private static ToolExecutionObservation ExecutionOf(int index, Observed<string> plugin, Observed<string> function, Observed<int> status, SourceRef rowSource) =>
        new(
            ObservationId("tool-execution", index),
            Observed<string>.Missing(),
            ToolIdentity.FromReportedNames(plugin, function, One(rowSource)),
            Observed<JsonElement>.Missing(),
            Observed<JsonElement>.Missing(),
            status,
            UnknownOutcome(One(rowSource)),
            Observed<DateTimeOffset>.Missing(),
            Observed<DateTimeOffset>.Missing(),
            Observed<double>.Missing(),
            One(rowSource));

    private static ToolExecutionObservation InvalidExecutionRow(SourceRef rowSource, int index)
    {
        var invalid = Observed<string>.Invalid(One(rowSource));
        return ExecutionOf(index, invalid, invalid, Observed<int>.Invalid(One(rowSource)), rowSource);
    }

    private static Observed<string> ReadPluginName(JsonElement container, SourceRef containerSource, List<SourceRef> unknown)
    {
        if (!TryGetProperty(container, "plugin", out var plugin))
        {
            return Observed<string>.Missing();
        }

        var pluginSource = Child(containerSource, "plugin");
        switch (plugin.ValueKind)
        {
            case JsonValueKind.Null:
                return Observed<string>.Null(One(pluginSource));
            case JsonValueKind.Object:
                CollectUnknownFields(plugin, pluginSource, PluginKeys, unknown);
                return ReadString(plugin, "name", pluginSource);
            default:
                return Observed<string>.Invalid(One(pluginSource));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Retrieval Info
    // ---------------------------------------------------------------------------------------------------------

    private static EvidenceSet<RetrievalExecutionObservation> DecodeRetrievalInfo(JsonElement data, SourceRef dataSource, List<EvidenceIssue> issues, List<SourceRef> unknown)
    {
        CollectUnknownFields(data, dataSource, RetrievalRootKeys, unknown);
        return DecodeRows<RetrievalExecutionObservation>(
            data,
            "executions",
            dataSource,
            (JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> rowIssues, out bool invalid) =>
                DecodeRetrievalRow(row, rowSource, index, rowIssues, unknown, out invalid),
            InvalidRetrievalRow);
    }

    private static RetrievalExecutionObservation DecodeRetrievalRow(JsonElement row, SourceRef rowSource, int index, List<EvidenceIssue> issues, List<SourceRef> unknown, out bool invalid)
    {
        CollectUnknownFields(row, rowSource, RetrievalRowKeys, unknown);
        var capability = ReadString(row, "name", rowSource);
        var status = ReadString(row, "status", rowSource);
        var outcome = MapOutcome(status, rowSource);
        NoteInvalid(issues, FieldInvalidIssueCode, "name", capability);
        NoteInvalid(issues, FieldInvalidIssueCode, "status", status);

        EvidenceSet<RetrievalQuery> queries;
        Observed<double> latency;
        Observed<long> total;
        EvidenceSet<RetrievedHit> hits;
        var containerInvalid = false;
        if (!TryGetProperty(row, "debugInfo", out var debug))
        {
            queries = EvidenceSet<RetrievalQuery>.NotProvided(EvidenceScope.Unknown, One(rowSource));
            latency = Observed<double>.Missing();
            total = Observed<long>.Missing();
            hits = EvidenceSet<RetrievedHit>.NotProvided(EvidenceScope.Unknown, One(rowSource));
        }
        else
        {
            var debugSource = Child(rowSource, "debugInfo");
            if (debug.ValueKind != JsonValueKind.Object)
            {
                containerInvalid = true;
                issues.Add(Issue(ContainerMalformedIssueCode, "'debugInfo' is not a JSON object; its query, counts, latency and hits cannot be read.", debugSource));
                queries = MalformedSet<RetrievalQuery>(debugSource);
                latency = Observed<double>.Invalid(One(debugSource));
                total = Observed<long>.Invalid(One(debugSource));
                hits = MalformedSet<RetrievedHit>(debugSource);
            }
            else
            {
                CollectUnknownFields(debug, debugSource, DebugInfoKeys, unknown);
                queries = DecodeQuery(debug, debugSource, issues);
                latency = ReadFiniteNonNegativeNumber(debug, "latencyMs", debugSource);
                total = ReadNonNegativeInteger(debug, "totalResultCount", debugSource);
                NoteInvalid(issues, FieldInvalidIssueCode, "latencyMs", latency);
                NoteInvalid(issues, FieldInvalidIssueCode, "totalResultCount", total);
                hits = DecodeHits(debug, debugSource, index, issues, unknown);
            }
        }

        invalid = containerInvalid || IsInvalid(capability) || IsInvalid(status) || IsNotValid(queries) || IsInvalid(latency) || IsInvalid(total) || IsNotValid(hits);
        return new RetrievalExecutionObservation(
            ObservationId("retrieval-execution", index),
            Observed<string>.Missing(),
            capability,
            status,
            outcome,
            queries,
            latency,
            total,
            hits,
            One(rowSource));
    }

    /// <summary>One flat query per execution (the native schema has no sub-queries); absent when none of its three fields was sent.</summary>
    private static EvidenceSet<RetrievalQuery> DecodeQuery(JsonElement debug, SourceRef debugSource, List<EvidenceIssue> issues)
    {
        var queryText = ReadString(debug, "queryString", debugSource);
        var filter = ReadString(debug, "filterExpression", debugSource);
        var maximum = ReadInteger(debug, "maximumNumberOfResults", debugSource);
        if (queryText.State == FieldState.Missing && filter.State == FieldState.Missing && maximum.State == FieldState.Missing)
        {
            return EvidenceSet<RetrievalQuery>.NotProvided(EvidenceScope.Unknown, One(debugSource));
        }

        NoteInvalid(issues, FieldInvalidIssueCode, "queryString", queryText);
        NoteInvalid(issues, FieldInvalidIssueCode, "filterExpression", filter);
        NoteInvalid(issues, FieldInvalidIssueCode, "maximumNumberOfResults", maximum);
        var invalid = IsInvalid(queryText) || IsInvalid(filter) || IsInvalid(maximum);
        return EvidenceSet<RetrievalQuery>.Provided(
            ImmutableArray.Create(new RetrievalQuery(queryText, filter, maximum)),
            invalid ? EvidenceValidation.Partial : EvidenceValidation.Valid,
            EvidenceCoverage.Unknown,
            EvidenceScope.Unknown,
            One(debugSource));
    }

    private static EvidenceSet<RetrievedHit> DecodeHits(JsonElement container, SourceRef containerSource, int executionIndex, List<EvidenceIssue> issues, List<SourceRef> unknown) =>
        DecodeRows<RetrievedHit>(
            container,
            "retrievalHits",
            containerSource,
            (JsonElement hit, SourceRef hitSource, int index, List<EvidenceIssue> rowIssues, out bool invalid) =>
                DecodeHitRow(hit, hitSource, executionIndex, index, rowIssues, unknown, out invalid),
            (hitSource, index) => InvalidHitRow(hitSource, executionIndex, index),
            issues);

    private static RetrievedHit DecodeHitRow(JsonElement hit, SourceRef hitSource, int executionIndex, int index, List<EvidenceIssue> issues, List<SourceRef> unknown, out bool invalid)
    {
        CollectUnknownFields(hit, hitSource, HitKeys, unknown);
        var url = ReadString(hit, "webUrl", hitSource);
        var rank = ReadInteger(hit, "rank", hitSource);
        var metadata = ReadObject(hit, "resourceMetadata", hitSource);
        var passages = DecodeRows<RetrievalPassage>(
            hit,
            "extracts",
            hitSource,
            (JsonElement extract, SourceRef extractSource, int passageIndex, List<EvidenceIssue> rowIssues, out bool passageInvalid) =>
            {
                CollectUnknownFields(extract, extractSource, ExtractKeys, unknown);
                var text = ReadString(extract, "text", extractSource);
                NoteInvalid(rowIssues, FieldInvalidIssueCode, "text", text);
                passageInvalid = IsInvalid(text);
                return PassageOf(executionIndex, index, passageIndex, text, extractSource);
            },
            (extractSource, passageIndex) => PassageOf(executionIndex, index, passageIndex, Observed<string>.Invalid(One(extractSource)), extractSource),
            issues);
        NoteInvalid(issues, FieldInvalidIssueCode, "webUrl", url);
        NoteInvalid(issues, FieldInvalidIssueCode, "rank", rank);
        NoteInvalid(issues, FieldInvalidIssueCode, "resourceMetadata", metadata);
        invalid = IsInvalid(url) || IsInvalid(rank) || IsInvalid(metadata) || IsNotValid(passages);
        return new RetrievedHit(ObservationId("retrieval-hit", executionIndex, index), Observed<string>.Missing(), url, rank, passages, metadata, One(hitSource));
    }

    private static RetrievedHit InvalidHitRow(SourceRef hitSource, int executionIndex, int index) =>
        new(
            ObservationId("retrieval-hit", executionIndex, index),
            Observed<string>.Missing(),
            Observed<string>.Invalid(One(hitSource)),
            Observed<int>.Invalid(One(hitSource)),
            MalformedSet<RetrievalPassage>(hitSource),
            Observed<JsonElement>.Invalid(One(hitSource)),
            One(hitSource));

    private static RetrievalPassage PassageOf(int executionIndex, int hitIndex, int index, Observed<string> text, SourceRef source) =>
        new(
            ObservationId("retrieval-passage", executionIndex, hitIndex, index),
            text,
            Observed<int>.Missing(),
            Observed<int>.Missing(),
            Observed<double>.Missing(),
            One(source));

    private static RetrievalExecutionObservation InvalidRetrievalRow(SourceRef rowSource, int index)
    {
        var invalid = Observed<string>.Invalid(One(rowSource));
        return new RetrievalExecutionObservation(
            ObservationId("retrieval-execution", index),
            Observed<string>.Missing(),
            invalid,
            invalid,
            UnknownOutcome(One(rowSource)),
            MalformedSet<RetrievalQuery>(rowSource),
            Observed<double>.Invalid(One(rowSource)),
            Observed<long>.Invalid(One(rowSource)),
            MalformedSet<RetrievedHit>(rowSource),
            One(rowSource));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared rules
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The versioned status → outcome map (<see cref="OutcomeRuleId"/>). A present string maps case-insensitively:
    /// <c>success</c> → Succeeded, <c>failed</c>/<c>failure</c>/<c>error</c> → Failed, anything else → Unknown; a
    /// missing, null or invalid status → Unknown. Always a parser projection naming its rule.
    /// </summary>
    private static Observed<ExecutionOutcome> MapOutcome(Observed<string> status, SourceRef rowSource)
    {
        if (!status.IsPresent)
        {
            return UnknownOutcome(status.Sources.IsDefaultOrEmpty ? One(rowSource) : status.Sources);
        }

        var text = status.Value!;
        ExecutionOutcome outcome;
        if (string.Equals(text, "success", StringComparison.OrdinalIgnoreCase))
        {
            outcome = ExecutionOutcome.Succeeded;
        }
        else if (string.Equals(text, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "error", StringComparison.OrdinalIgnoreCase))
        {
            outcome = ExecutionOutcome.Failed;
        }
        else
        {
            outcome = ExecutionOutcome.Unknown;
        }

        return Observed<ExecutionOutcome>.Present(outcome, status.Sources, EvidenceDerivation.ParserProjection, OutcomeRuleId);
    }

    private static Observed<ExecutionOutcome> UnknownOutcome(ImmutableArray<SourceRef> sources) =>
        Observed<ExecutionOutcome>.Present(ExecutionOutcome.Unknown, sources, EvidenceDerivation.ParserProjection, OutcomeRuleId);

    /// <summary>
    /// Decodes a list field into a provided collection: absent → NotProvided; null → Provided + Malformed
    /// (<see cref="ArrayNullIssueCode"/>); not an array → Provided + Malformed (<see cref="ArrayNotArrayIssueCode"/>);
    /// an empty array → Provided + Valid with zero items; otherwise one observation per row (a non-object row via
    /// <paramref name="invalidRow"/>, kept, with <see cref="RowNotObjectIssueCode"/>). Row-level issues go into the
    /// collection's own issues, and also into <paramref name="parentIssues"/> when given.
    /// </summary>
    private static EvidenceSet<T> DecodeRows<T>(
        JsonElement parent,
        string name,
        SourceRef parentSource,
        RowDecoder<T> decodeRow,
        Func<SourceRef, int, T> invalidRow,
        List<EvidenceIssue>? parentIssues = null)
    {
        if (!TryGetProperty(parent, name, out var array))
        {
            return EvidenceSet<T>.NotProvided(EvidenceScope.Unknown, One(parentSource));
        }

        var listSource = Child(parentSource, name);
        var issues = new List<EvidenceIssue>();
        if (array.ValueKind == JsonValueKind.Null)
        {
            issues.Add(Issue(ArrayNullIssueCode, FormattableString.Invariant($"'{name}' is an explicit null, not a list; nothing can be read from it."), listSource));
            parentIssues?.AddRange(issues);
            return MalformedSet<T>(listSource, issues.ToImmutableArray());
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Issue(ArrayNotArrayIssueCode, FormattableString.Invariant($"'{name}' is present but is not a JSON array; nothing can be read from it."), listSource));
            parentIssues?.AddRange(issues);
            return MalformedSet<T>(listSource, issues.ToImmutableArray());
        }

        var items = ImmutableArray.CreateBuilder<T>();
        var interpreted = 0;
        var anyInvalid = false;
        var index = 0;
        foreach (var row in array.EnumerateArray())
        {
            var rowSource = Child(listSource, index);
            if (row.ValueKind == JsonValueKind.Object)
            {
                items.Add(decodeRow(row, rowSource, index, issues, out var invalid));
                interpreted++;
                anyInvalid |= invalid;
            }
            else
            {
                items.Add(invalidRow(rowSource, index));
                anyInvalid = true;
                issues.Add(Issue(RowNotObjectIssueCode, FormattableString.Invariant($"Row {index} of '{name}' is not a JSON object; its fields are Invalid and the row is kept."), rowSource));
            }

            index++;
        }

        parentIssues?.AddRange(issues);
        return EvidenceSet<T>.Provided(
            items.ToImmutable(),
            RowSetValidation(index, interpreted, anyInvalid),
            EvidenceCoverage.Unknown,
            EvidenceScope.Unknown,
            One(listSource),
            issues.ToImmutableArray());
    }
}
