// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing.Tests;

public sealed class PageQueryPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-page-plans-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection;

    public PageQueryPlanTests()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.sqlite");
        new SqliteEvidenceIndexSchema().EnsureCurrent(new() { DatabasePath = path });
        _connection = SqliteEvidenceIndexSchema.Open(path, 2, readOnly: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Sources_FirstAndContinuationPages_UseOrderedIndexWithoutTemporarySort(bool filtered, bool continuation)
    {
        using var command = EvidenceIndexPageCommand.Sources(_connection, null, filtered ? "root" : null,
            100, continuation ? "root" : null, continuation ? "path" : null);
        var plan = Explain(command);
        Assert.Contains("USING INDEX", plan);
        Assert.DoesNotContain("TEMP B-TREE", plan);
        if (filtered || continuation) Assert.Contains("SEARCH", plan);
    }

    [Theory]
    [InlineData("results", false)]
    [InlineData("results", true)]
    [InlineData("metrics", false)]
    [InlineData("metrics", true)]
    [InlineData("spans", false)]
    [InlineData("spans", true)]
    [InlineData("fields", false)]
    [InlineData("fields", true)]
    public void Details_FirstAndContinuationPages_SeekSourcePrimaryKeyWithoutScanOrSort(string family, bool continuation)
    {
        object?[] keys = family switch
        {
            "results" => ["result"], "metrics" => ["result", "metric", 2], "spans" => [2L], _ => [2L, "field"]
        };
        using var command = EvidenceIndexPageCommand.Details(_connection, null, family, new string('a', 64), 100,
            continuation ? keys : null);
        var plan = Explain(command);
        Assert.Contains("SEARCH", plan);
        Assert.Contains("source_id=?", plan);
        Assert.DoesNotContain("SCAN", plan);
        Assert.DoesNotContain("TEMP B-TREE", plan);
    }

    private static string Explain(SqliteCommand command)
    {
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(3));
        return string.Join('\n', rows);
    }

    public void Dispose() { _connection.Dispose(); Directory.Delete(_root, recursive: true); }
}
