// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.Results.Observations;

namespace AgentEval.Indexing.Tests;

public sealed class DetailReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-detail-page-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private SqliteEvidenceIndexWriter Writer() => new(Options, new SqliteEvidenceIndexSchema());
    private static EvidenceIndexSource Source(string path = "run") => new("root", path, new string('a', 64), IndexedEvidenceKind.Legacy,
        IndexedVerification.Unverified, new(path, null, null, null, null, null, "PASS", "unknown"),
        [new("a", "case-a", 9007199254740993L, "PASS"), new("z", "case-z", null, "FAIL"), new("legacy:summary", null, null, "PASS")],
        [new("a", "metric", 10, null, "NaN", null, "reported"), new("a", "metric", 2, 0.5, null, false, "reported"),
         new("a", "z", 0, 1, null, true, "reported"), new("legacy:summary", "mean", 0, 0.5, null, null, "legacy-run-summary")], null);

    [Fact]
    public void ReadResultsAndMetrics_CompositeKeysAndAggregateRows_PreserveExactValuesAcrossPages()
    {
        using var writer = Writer(); var id = writer.Upsert(Source()).SourceId;
        var reader = new SqliteEvidenceDetailReader(Options);
        var results = Pages(c => reader.ReadResults(new(id, 1, c)));
        Assert.Equal(new[] { "a", "legacy:summary", "z" }, results.Select(r => r.ResultId));
        Assert.Equal(9007199254740993L, results[0].Trial); Assert.Null(results[1].CaseId);
        var metrics = Pages(c => reader.ReadMetrics(new(id, 1, c)));
        Assert.Equal(new[] { 2, 10, 0, 0 }, metrics.Select(m => m.Ordinal));
        Assert.Equal("NaN", metrics[1].ScoreText); Assert.Null(metrics[1].Score); Assert.Null(metrics[1].Passed);
        Assert.False(metrics[0].Passed); Assert.Equal("legacy-run-summary", metrics[3].Provenance);
        var page = reader.ReadResults(new(id));
        Assert.Equal("legacy", page.SourceKind); Assert.Equal("unverified", page.Verification);
        Assert.Equal(writer.Find("root", "run")!.Fingerprint, page.Fingerprint);
        Assert.Equal("wrong-source-kind", Assert.Throws<EvidenceIndexException>(() => reader.ReadSpans(new(id))).Code);
    }

    [Fact]
    public void Read_DifferentSourceFamilyOrGeneration_RejectsCursorReuse()
    {
        using var writer = Writer(); var id = writer.Upsert(Source()).SourceId; var other = writer.Upsert(Source("other")).SourceId;
        var reader = new SqliteEvidenceDetailReader(Options); var cursor = reader.ReadResults(new(id, 1)).NextCursor!;
        Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new(other, 1, cursor))).Code);
        Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.ReadMetrics(new(id, 1, cursor))).Code);
        Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new(id, 2, cursor))).Code);
        writer.Upsert(Source() with { Run = Source().Run! with { Outcome = "FAIL" } });
        Assert.Equal("stale-cursor", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new(id, 1, cursor))).Code);
    }

    [Fact]
    public void Read_MissingSourceAndEmptyResults_AreDistinct()
    {
        using var writer = Writer(); var id = writer.Upsert(Source() with { Results = [], Metrics = [] }).SourceId;
        var reader = new SqliteEvidenceDetailReader(Options);
        Assert.Empty(reader.ReadResults(new(id)).Items);
        Assert.Null(reader.ReadMetrics(new(id)).NextCursor);
        Assert.Equal("source-not-found", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new(new string('0', 64)))).Code);
        Assert.Equal("invalid-page-request", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new("bad"))).Code);
        Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new(id, Cursor: "e30"))).Code);
    }

    [Fact]
    public void ReadMetrics_OversizedProvenance_RefusesWholePageWithoutAffectingOtherQueries()
    {
        using var writer = Writer();
        var id = writer.Upsert(Source() with { Metrics = [new("a", "large", 0, 0.5, null, null, new string('x', (2 << 20) + 1))] }).SourceId;
        var reader = new SqliteEvidenceDetailReader(Options);
        Assert.Equal("page-byte-limit", Assert.Throws<EvidenceIndexException>(() => reader.ReadMetrics(new(id))).Code);
        Assert.Equal(3, reader.ReadResults(new(id)).Items.Length);
        Assert.Throws<OperationCanceledException>(() => reader.ReadResults(new(id), new(true)));
    }

    [Fact]
    public void ReadObservationDetails_ActualStoredReports_PreserveDuplicatesUnsignedTimesAndConflictProvenance()
    {
        const string span = """
            {"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef","startTimeUnixNano":"18446744073709551615",
             "attributes":[{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"12"}},
             {"key":"gen_ai.usage.completion_tokens","value":{"intValue":"13"}}]}
            """;
        var batch = new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes("{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" + span + "," + span + "]}]}]}"));
        using var store = new OtlpObservationStore(new() { Root = Path.Combine(_root, "observations"), Source = "receiver" },
            new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
        store.Accept(batch); using var writer = Writer();
        Assert.True(new OtlpObservationIndexer(writer).Import("observations", store).Complete);
        var id = writer.ListSources("observations").Single().Checkpoint.SourceId;
        var reader = new SqliteEvidenceDetailReader(Options);
        var spans = Pages(c => reader.ReadSpans(new(id, 1, c)));
        Assert.Equal(new long[] { 0, 1 }, spans.Select(s => s.ReportOrdinal));
        Assert.All(spans, s => Assert.Equal("18446744073709551615", s.StartNanos));
        Assert.Equal(spans[0].SpanId, spans[1].SpanId);
        var fields = Pages(c => reader.ReadFields(new(id, 3, c)));
        Assert.Equal(fields.Count, fields.Select(f => (f.ReportOrdinal, f.FieldName)).Distinct().Count());
        Assert.Equal(2, fields.Count(f => f.FieldName == "tokens.input"));
        Assert.Equal(2, fields.Count(f => f.FieldName == "tokens.output"));
        Assert.All(fields.Where(f => f.FieldName == "tokens.input"), f => Assert.Equal(9007199254740993L, f.IntegerValue));
        foreach (var field in fields.Where(f => f.FieldName == "tokens.output"))
        {
            Assert.Equal("Conflict", field.State); Assert.Null(field.IntegerValue); Assert.Null(field.TextValue);
            using var provenance = JsonDocument.Parse(field.SourcesJson);
            Assert.Equal(new long[] { 12, 13 }, provenance.RootElement.EnumerateArray().Select(p => p.GetProperty("Integer").GetInt64()).Order());
        }
        Assert.Equal("wrong-source-kind", Assert.Throws<EvidenceIndexException>(() => reader.ReadResults(new(id))).Code);
    }

    private static List<T> Pages<T>(Func<string?, EvidenceDetailPage<T>> read)
    {
        var items = new List<T>(); string? cursor = null; var count = 0;
        do { Assert.True(++count < 100); var page = read(cursor); items.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
        return items;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
