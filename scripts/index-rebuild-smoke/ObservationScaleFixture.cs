// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentEval.Indexing;
using AgentEval.Results.Observations;

internal static class ObservationScaleFixture
{
    internal static object Run(EvidenceIndexOptions options, int reports)
    {
        if (reports is < 1 or > 2000 || File.Exists(options.DatabasePath))
            throw new ArgumentException("Expected: a new index and 1..2000 reports. Actual: invalid fixture input. Suggestions: use a temporary directory. Because: existing evidence must remain untouched.");
        const string span = """
            {"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef","name":"private-prompt",
             "startTimeUnixNano":"18446744073709551615",
             "attributes":[{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"12"}},
             {"key":"gen_ai.usage.completion_tokens","value":{"intValue":"13"}}]}
            """;
        var measurements = new List<object>(); var watch = Stopwatch.StartNew();
        void Record(object measurement)
        {
            measurements.Add(measurement); Console.Error.WriteLine(JsonSerializer.Serialize(measurement));
        }
        var batch = new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes(
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" + string.Join(',', Enumerable.Repeat(span, reports)) + "]}]}]}"));
        using var store = new OtlpObservationStore(new()
        {
            Root = Path.Combine(Path.GetDirectoryName(options.DatabasePath)!, "observations"), Source = "scale-receiver"
        }, new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
        var receipt = store.Accept(batch);
        var original = store.Read(receipt.Id);
        Require(original.Content.Capture == OtlpContentCapture.Off, "default content off");
        var expected = original.Sessions.SelectMany(s => s.Members).ToArray();
        Require(expected.Length == reports, "duplicate reports retained by store");
        var fieldsPerReport = expected[0].Span.Fields.Keys.Order(StringComparer.Ordinal).ToArray();
        string id;
        using (var writer = new SqliteEvidenceIndexWriter(options, new SqliteEvidenceIndexSchema()))
        {
            var importer = new OtlpObservationIndexer(writer);
            var imported = importer.Import("observations", store);
            Require(imported.Complete && imported.Changed == 1, "complete observation import");
            var retry = importer.Import("observations", store);
            Require(retry.Complete && retry.Unchanged == 1, "idempotent observation import");
            id = writer.ListSources("observations").Single().Checkpoint.SourceId;
        }
        Record(new { operation = "decode-store-import-retry", reports, milliseconds = watch.Elapsed.TotalMilliseconds });
        var reader = new SqliteEvidenceDetailReader(options);
        foreach (var size in new[] { 100, 250 })
        {
            string? cursor = null; var count = 0; var pages = 0; watch.Restart();
            do
            {
                var page = reader.ReadSpans(new(id, size, cursor)); pages++;
                Require(page.SourceKind == "observation" && page.Verification == "unverified", "observation provenance");
                foreach (var item in page.Items)
                {
                    Require(item.ReportOrdinal == count && item.SpanIndex == count && item.SpanId == "0123456789abcdef" &&
                        item.StartNanos == "18446744073709551615", "duplicate report position and exact timestamp");
                    count++;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
            Require(count == reports, "span coverage");
            Record(new { operation = "spans", size, count, pages, milliseconds = watch.Elapsed.TotalMilliseconds });
            count = 0; pages = 0; watch.Restart();
            do
            {
                var page = reader.ReadFields(new(id, size, cursor)); pages++;
                foreach (var item in page.Items)
                {
                    var ordinal = count / fieldsPerReport.Length; var field = fieldsPerReport[count % fieldsPerReport.Length];
                    Require(item.ReportOrdinal == ordinal && item.FieldName == field, "exact field key ordering");
                    var source = expected[ordinal].Span.Fields[field];
                    Require(item.State == source.State.ToString() && item.TextValue == (source.Value as OtlpValue.Text)?.Value &&
                        item.IntegerValue == (source.Value as OtlpValue.Integer)?.Value, "normalized field value and state");
                    if (field == "tokens.input") Require(item.IntegerValue == 9_007_199_254_740_993L, "exact Int64");
                    if (field == "tokens.output")
                    {
                        using var provenance = JsonDocument.Parse(item.SourcesJson);
                        Require(item.State == "Conflict" && item.IntegerValue is null && item.TextValue is null &&
                            provenance.RootElement.EnumerateArray().Select(p => p.GetProperty("Integer").GetInt64()).Order()
                                .SequenceEqual(new long[] { 12, 13 }), "conflicting aliases and provenance");
                    }
                    count++;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
            Require(count == reports * fieldsPerReport.Length, "field coverage");
            Record(new { operation = "fields", size, count, pages, milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        try { reader.ReadResults(new(id)); throw new InvalidOperationException("Observation unexpectedly accepted as evaluation."); }
        catch (EvidenceIndexException error) when (error.Code == "wrong-source-kind") { }
        return new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            reports, fields = reports * fieldsPerReport.Length, databaseBytes = new FileInfo(options.DatabasePath).Length, measurements };
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException($"Expected: {check}. Actual: mismatch. Suggestions: inspect observation fixture. Because: scale must preserve evidence semantics.");
    }
}
