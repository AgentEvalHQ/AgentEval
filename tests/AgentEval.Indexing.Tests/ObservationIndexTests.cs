// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.Results.Observations;

namespace AgentEval.Indexing.Tests;

public sealed class ObservationIndexTests : IDisposable
{
    private const string TestTraceId = "0123456789abcdef0123456789abcdef"; // DevSkim: ignore DS173237 - synthetic OTLP trace identity, not a credential

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-observation-index-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    private OtlpObservationStore Store() => new(new() { Root = Path.Combine(_root, "observations"), Source = "receiver" },
        new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
    private SqliteEvidenceIndexWriter Writer() => new(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
    private static OtlpTraceBatch Batch(string span = "0123456789abcdef") => new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes($$$"""
        {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"{{{TestTraceId}}}","spanId":"SPAN",
        "startTimeUnixNano":"18446744073709551615","name":"private-prompt",
        "attributes":[{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}},
        {"key":"gen_ai.usage.output_tokens","value":{"intValue":"12"}},
        {"key":"gen_ai.usage.completion_tokens","value":{"intValue":"13"}}]}]}]}]}
        """.Replace("SPAN", span)));

    [Fact]
    public void Import_VerifiedStoredRecords_PreservesPrecisionConflictsOmissionsAndIdempotence()
    {
        using var store = Store();
        store.Accept(Batch());
        using var writer = Writer();
        var indexer = new OtlpObservationIndexer(writer);
        var first = indexer.Import("observations", store);
        Assert.True(first.Complete); Assert.Equal(1, first.Changed);
        var second = indexer.Import("observations", store);
        Assert.True(second.Complete); Assert.Equal(1, second.Unchanged); Assert.Equal(0, second.Changed);
        using var connection = SqliteEvidenceIndexSchema.Open(Database, 1);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT integer_value FROM observed_fields WHERE field_name='tokens.input'";
        Assert.Equal(9007199254740993L, command.ExecuteScalar());
        command.CommandText = "SELECT start_nanos FROM observed_spans";
        Assert.Equal("18446744073709551615", command.ExecuteScalar());
        command.CommandText = "SELECT state,integer_value,sources_json FROM observed_fields WHERE field_name='tokens.output'";
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read()); Assert.Equal("Conflict", reader.GetString(0)); Assert.True(reader.IsDBNull(1));
            using var sources = JsonDocument.Parse(reader.GetString(2));
            Assert.Equal(new long[] { 12, 13 }, sources.RootElement.EnumerateArray().Select(v => v.GetProperty("Integer").GetInt64()).Order());
        }
        command.CommandText = "SELECT removals_json FROM observation_batches";
        using var removals = JsonDocument.Parse((string)command.ExecuteScalar()!);
        Assert.Equal(1, removals.RootElement.GetProperty("SpanNames").GetInt64());
        command.CommandText = "SELECT count(*) FROM evaluation_runs";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Import_BudgetOrCorruptSource_ReportsIncompleteWithoutDeletingPriorIndexRows()
    {
        using var store = Store();
        var first = store.Accept(Batch()); store.Accept(Batch("1123456789abcdef"));
        using var writer = Writer();
        var indexer = new OtlpObservationIndexer(writer);
        var partial = indexer.Import("root", store, maximumRecords: 1);
        Assert.False(partial.Complete); Assert.Equal(1, partial.Processed);
        Assert.True(indexer.Import("root", store).Complete);
        var path = Path.Combine(_root, "observations", "records", first.Id + ".json");
        var bytes = File.ReadAllBytes(path); bytes[0] = (byte)'['; File.WriteAllBytes(path, bytes);
        var failed = indexer.Import("root", store);
        Assert.False(failed.Complete); Assert.Single(failed.Issues); Assert.Equal(1, failed.Unchanged);
        Assert.NotNull(writer.Find("root", "records/" + first.Id + ".json"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
