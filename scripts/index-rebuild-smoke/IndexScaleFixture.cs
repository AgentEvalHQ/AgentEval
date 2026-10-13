// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentEval.Indexing;

// Offline, deterministic measurements of the public writer/read APIs, including validation overhead.
// Timing is diagnostic, never a hardware-independent pass/fail threshold.
internal static class IndexScaleFixture
{
    internal static object Run(EvidenceIndexOptions options, int sources, int resultsPerSource)
    {
        if (sources is < 1 or > 5000 || resultsPerSource is < 1 or > 100 || File.Exists(options.DatabasePath))
            throw new ArgumentException("Expected: a new index, 1..5000 sources and 1..100 results per source. Actual: invalid scale input. Suggestions: use a fresh temporary directory. Because: fixtures must not modify existing evidence.");
        var measurements = new List<object>();
        var watch = Stopwatch.StartNew();
        void Record(object measurement)
        {
            measurements.Add(measurement);
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(measurement));
        }
        var schema = new SqliteEvidenceIndexSchema();
        string detailSource;
        using (var writer = new SqliteEvidenceIndexWriter(options, schema,
            new EvidenceIndexWriteLimits { MaxTotalRows = 2_000_000 }))
        {
            writer.BindRoots([new("scale", Path.GetDirectoryName(options.DatabasePath)!, IndexedEvidenceKind.Legacy)]);
            for (var i = 0; i < sources; i++) writer.Upsert(Source(i, resultsPerSource));
            // A larger single source exposes detail traversal costs independently of source count.
            detailSource = writer.Upsert(Source(sources, 10_000)).SourceId;
            Record(new { operation = "write", count = sources + 1, milliseconds = watch.Elapsed.TotalMilliseconds });
            watch.Restart();
            for (var i = 0; i < sources; i++) Require(!writer.Upsert(Source(i, resultsPerSource)).Changed, "no-op retry");
            Record(new { operation = "retry", count = sources, milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        watch.Restart();
        for (var i = 0; i < 5; i++) schema.EnsureCurrent(options);
        Record(new { operation = "full-validation", count = 5, milliseconds = watch.Elapsed.TotalMilliseconds });
        var sourceReader = new SqliteEvidenceSourceReader(options);
        var detailReader = new SqliteEvidenceDetailReader(options);
        foreach (var size in new[] { 25, 250 })
        {
            string? cursor = null; var count = 0; var pages = 0;
            watch.Restart();
            do
            {
                var page = sourceReader.Read(new(PageSize: size, Cursor: cursor)); pages++;
                foreach (var item in page.Items) Require(item.RelativePath == PathFor(count++), "exact source ordering");
                cursor = page.NextCursor;
            } while (cursor is not null);
            Require(count == sources + 1, "source coverage");
            Record(new { operation = "sources", size, count, pages, milliseconds = watch.Elapsed.TotalMilliseconds });
            count = 0; pages = 0; watch.Restart();
            do
            {
                var page = detailReader.ReadResults(new(detailSource, size, cursor)); pages++;
                foreach (var item in page.Items)
                {
                    Require(item.ResultId == ResultFor(count) && item.Trial == 9_007_199_254_740_993L + count, "exact result values");
                    count++;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
            Require(count == 10_000, "result coverage");
            Record(new { operation = "results", size, count, pages, milliseconds = watch.Elapsed.TotalMilliseconds });
            count = 0; pages = 0; watch.Restart();
            do
            {
                var page = detailReader.ReadMetrics(new(detailSource, size, cursor)); pages++;
                foreach (var item in page.Items)
                {
                    Require(item.ResultId == ResultFor(count) && item.Score == count && item.Passed is null &&
                        item.Provenance == "scale-fixture", "exact metric values and provenance");
                    count++;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
            Require(count == 10_000, "metric coverage");
            Record(new { operation = "metrics", size, count, pages, milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        return new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            sources = sources + 1, results = sources * resultsPerSource + 10_000,
            databaseBytes = new FileInfo(options.DatabasePath).Length, measurements };
    }

    private static string PathFor(int i) => $"source-{i:D6}";
    private static string ResultFor(int i) => $"result-{i:D6}";
    private static EvidenceIndexSource Source(int i, int count) => new("scale", PathFor(i), new string('a', 64),
        IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
        new(PathFor(i), null, "agent:scale", null, null, null, null, "unknown"),
        Enumerable.Range(0, count).Select(n => new IndexedResult(ResultFor(n), $"case-{n}", 9_007_199_254_740_993L + n, null)).ToImmutableArray(),
        Enumerable.Range(0, count).Select(n => new IndexedMetric(ResultFor(n), "code_scale", 0, n, null, null, "scale-fixture")).ToImmutableArray(), null);
    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException($"Expected: {check}. Actual: mismatch. Suggestions: inspect the fixture output and index. Because: scale must preserve correctness.");
    }
}
