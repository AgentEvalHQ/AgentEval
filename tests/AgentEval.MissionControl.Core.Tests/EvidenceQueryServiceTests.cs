// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.Results.Observations;
using System.Text;
using System.Text.Json;

namespace AgentEval.MissionControl.Core.Tests;

public sealed class EvidenceQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-shared-query-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private EvidenceQueryService Service() => new(new SqliteEvidenceSourceReader(Options), new SqliteEvidenceDetailReader(Options));
    private SqliteEvidenceIndexWriter Writer() => new(Options, new SqliteEvidenceIndexSchema());
    private static EvidenceIndexSource Source(string path) => new("root", path, new string('a', 64),
        IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
        new(path, null, null, null, null, null, null, "unknown"),
        [new("case-result", "case", 9007199254740993L, null), new("summary", null, null, "PASS")],
        [new("case-result", "metric", 2, null, "NaN", null, "reported")], null);

    public EvidenceQueryServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Sources_UnconfiguredMissingAndEmpty_AreDistinctWithoutCreatingMissingIndex()
    {
        var absent = new EvidenceQueryService().Sources(new());
        Assert.Equal(QueryState.NotConfigured, absent.State); Assert.Null(absent.Data);
        var missing = Service().Sources(new());
        Assert.Equal(QueryState.Unavailable, missing.State); Assert.Equal("missing-index", missing.Code);
        Assert.False(File.Exists(Options.DatabasePath));
        new SqliteEvidenceIndexSchema().EnsureCurrent(Options);
        var empty = Service().Sources(new());
        Assert.Equal(QueryState.Empty, empty.State); Assert.NotNull(empty.Data); Assert.Null(empty.Code);
        Assert.Equal(0, empty.Data.Generation); Assert.Empty(empty.Data.Items);
    }

    [Fact]
    public void Queries_ActualIndex_ReturnOnePageAndPreserveExactValuesAndProvenance()
    {
        using var writer = Writer(); var first = writer.Upsert(Source("a")); writer.Upsert(Source("b"));
        var service = Service(); var page = service.Sources(new(PageSize: 1));
        Assert.Equal(QueryState.Available, page.State); Assert.Single(page.Data!.Items); Assert.NotNull(page.Data.NextCursor);
        var next = service.Sources(new(PageSize: 1, Cursor: page.Data.NextCursor));
        Assert.Equal("b", Assert.Single(next.Data!.Items).RelativePath); Assert.Null(next.Data.NextCursor);
        var results = service.Results(new(first.SourceId));
        Assert.Equal(9007199254740993L, results.Data!.Items[0].Trial);
        Assert.Null(results.Data.Items[1].CaseId); Assert.Equal("unverified", results.Data.Verification);
        var metrics = service.Metrics(new(first.SourceId));
        var metric = Assert.Single(metrics.Data!.Items);
        Assert.Null(metric.Passed); Assert.Null(metric.Score); Assert.Equal("NaN", metric.ScoreText);
        Assert.Equal("reported", metric.Provenance); Assert.Equal(2, metric.Ordinal);
        Assert.Equal(QueryState.InvalidRequest, service.Spans(new(first.SourceId)).State);
        Assert.Equal("wrong-source-kind", service.Fields(new(first.SourceId)).Code);
    }

    [Fact]
    public void Queries_AbsentSourceEmptyDetailsAndChangedCursor_AreDistinct()
    {
        using var writer = Writer(); var first = writer.Upsert(Source("a") with { Results = [], Metrics = [] });
        writer.Upsert(Source("b")); var service = Service();
        Assert.Equal(QueryState.Empty, service.Results(new(first.SourceId)).State);
        Assert.Equal(QueryState.NotFound, service.Results(new(new string('0', 64))).State);
        var cursor = service.Sources(new(PageSize: 1)).Data!.NextCursor;
        writer.Upsert(Source("c"));
        var stale = service.Sources(new(PageSize: 1, Cursor: cursor));
        Assert.Equal(QueryState.StaleCursor, stale.State); Assert.Null(stale.Data);
        Assert.Equal(QueryState.InvalidRequest, service.Sources(new(PageSize: 0)).State);
        Assert.Equal("invalid-cursor", service.Sources(new(Cursor: "bad")).Code);
    }

    [Fact]
    public void Metrics_ExceededTextBudget_ReportsLimitWithoutPartialData()
    {
        using var writer = Writer();
        var source = Source("a");
        var id = writer.Upsert(source with { Metrics = [source.Metrics[0] with { Provenance = new string('x', (2 << 20) + 1) }] }).SourceId;
        var limited = Service().Metrics(new(id));
        Assert.Equal(QueryState.ResourceLimit, limited.State); Assert.Equal("page-byte-limit", limited.Code); Assert.Null(limited.Data);
        Assert.Equal(QueryState.Available, Service().Results(new(id)).State);
    }

    [Fact]
    public void Sources_CorruptDatabase_ReturnsUnavailableWithoutExceptionOrPathDisclosure()
    {
        File.WriteAllText(Options.DatabasePath, "not sqlite"); File.WriteAllText(Options.DatabasePath + ".lock", "");
        var result = Service().Sources(new());
        Assert.Equal(QueryState.Unavailable, result.State); Assert.Equal("index-unavailable", result.Code); Assert.Null(result.Data);
    }

    [Fact]
    public void Queries_Cancellation_PropagatesForConfiguredAndUnconfiguredCapabilities()
    {
        var token = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => Service().Sources(new(), token));
        Assert.Throws<OperationCanceledException>(() => new EvidenceQueryService().Sources(new(), token));
    }

    [Fact]
    public void ObservationQueries_StoredEvidence_PreserveDuplicateReportsAndConflictProvenance()
    {
        const string span = """
            {"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef",
             "startTimeUnixNano":"18446744073709551615",
             "attributes":[{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"12"}},
             {"key":"gen_ai.usage.completion_tokens","value":{"intValue":"13"}}]}
            """;
        var batch = new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes(
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" + span + "," + span + "]}]}]}"));
        using var store = new OtlpObservationStore(new() { Root = Path.Combine(_root, "observations"), Source = "receiver" },
            new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
        store.Accept(batch);
        using var writer = Writer();
        Assert.True(new OtlpObservationIndexer(writer).Import("observations", store).Complete);
        var id = writer.ListSources("observations").Single().Checkpoint.SourceId;
        var service = Service(); var first = service.Spans(new(id, 1));
        Assert.Equal(QueryState.Available, first.State);
        Assert.Equal("observation", first.Data!.SourceKind); Assert.Equal("unverified", first.Data.Verification);
        Assert.Equal("18446744073709551615", Assert.Single(first.Data.Items).StartNanos);
        var second = service.Spans(new(id, 1, first.Data.NextCursor));
        Assert.Equal(1, Assert.Single(second.Data!.Items).ReportOrdinal);
        var fields = service.Fields(new(id)); Assert.Equal(QueryState.Available, fields.State);
        Assert.All(fields.Data!.Items.Where(f => f.FieldName == "tokens.input"), f => Assert.Equal(9007199254740993L, f.IntegerValue));
        var conflicts = fields.Data.Items.Where(f => f.FieldName == "tokens.output").ToArray(); Assert.Equal(2, conflicts.Length);
        foreach (var field in conflicts)
        {
            Assert.Equal("Conflict", field.State); Assert.Null(field.IntegerValue); Assert.Null(field.TextValue);
            using var provenance = JsonDocument.Parse(field.SourcesJson);
            Assert.Equal(new long[] { 12, 13 }, provenance.RootElement.EnumerateArray().Select(p => p.GetProperty("Integer").GetInt64()).Order());
        }
        Assert.Equal("wrong-source-kind", service.Results(new(id)).Code);
    }

    [Fact]
    public void Sources_UnknownAdapterCode_DoesNotLeakCodeOrSuppressProgrammingErrors()
    {
        var reader = new FailingReader(new EvidenceIndexException(_root));
        var service = new EvidenceQueryService(reader, new SqliteEvidenceDetailReader(Options));
        Assert.Equal("index-unavailable", service.Sources(new()).Code);
        reader.Failure = new InvalidOperationException("programming error");
        Assert.Throws<InvalidOperationException>(() => service.Sources(new()));
    }

    private sealed class FailingReader(Exception failure) : IEvidenceSourceReader
    {
        internal Exception Failure { get; set; } = failure;
        public EvidenceSourcePage Read(EvidenceSourcePageRequest request, CancellationToken cancellationToken = default) => throw Failure;
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
