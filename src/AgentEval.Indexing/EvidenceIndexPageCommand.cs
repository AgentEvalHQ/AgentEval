// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

// The same command construction is used by page readers and query-plan acceptance tests.
internal static class EvidenceIndexPageCommand
{
    internal static SqliteCommand Sources(SqliteConnection connection, SqliteTransaction? transaction,
        string? rootId, int size, string? lastRoot, string? lastPath)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT source_id,root_id,relative_path,kind,verification,content_digest,indexed_generation,
                length(CAST(source_id AS BLOB))+length(CAST(root_id AS BLOB))+length(CAST(relative_path AS BLOB))+
                length(CAST(kind AS BLOB))+length(CAST(verification AS BLOB))+length(CAST(content_digest AS BLOB))
            FROM sources
            """;
        var conditions = new List<string>();
        if (rootId is not null)
        {
            conditions.Add("root_id=$root COLLATE BINARY"); command.Parameters.AddWithValue("$root", rootId);
        }
        if (lastRoot is not null)
        {
            conditions.Add("(root_id,relative_path)>($lastRoot,$lastPath)");
            command.Parameters.AddWithValue("$lastRoot", lastRoot); command.Parameters.AddWithValue("$lastPath", lastPath!);
        }
        if (conditions.Count > 0) command.CommandText += " WHERE " + string.Join(" AND ", conditions);
        command.CommandText += " ORDER BY root_id COLLATE BINARY,relative_path COLLATE BINARY LIMIT $limit";
        command.Parameters.AddWithValue("$limit", size + 1);
        return command;
    }

    internal sealed record DetailShape(string Table, string[] Columns, int[] TextColumns, string[] Order);
    internal static DetailShape Shape(string family) => family switch
    {
        "results" => new("evaluation_results", ["result_id", "case_id", "trial", "outcome"], [0, 1, 3], ["result_id"]),
        "metrics" => new("metric_values", ["result_id", "metric_id", "ordinal", "score", "score_text", "passed", "provenance"],
            [0, 1, 4, 6], ["result_id", "metric_id", "ordinal"]),
        "spans" => new("observed_spans", ["report_ordinal", "group_id", "grouping_revision", "grouping_basis", "mapping_revision", "trace_id",
            "span_id", "parent_span_id", "start_nanos", "end_nanos", "resource_index", "scope_index", "span_index"],
            [1, 2, 3, 4, 5, 6, 7, 8, 9], ["report_ordinal"]),
        "fields" => new("observed_fields", ["report_ordinal", "field_name", "state", "text_value", "integer_value", "sources_json"],
            [1, 2, 3, 5], ["report_ordinal", "field_name"]),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    internal static SqliteCommand Details(SqliteConnection connection, SqliteTransaction? transaction,
        string family, string sourceId, int size, object?[]? keys)
    {
        var shape = Shape(family);
        var command = connection.CreateCommand(); command.Transaction = transaction;
        var lengths = string.Join("+", shape.TextColumns.Select(i => $"coalesce(length(CAST({shape.Columns[i]} AS BLOB)),0)"));
        command.CommandText = $"SELECT {string.Join(',', shape.Columns)},{lengths} FROM {shape.Table} WHERE source_id=$source";
        command.Parameters.AddWithValue("$source", sourceId);
        if (keys is not null)
        {
            var names = Enumerable.Range(0, keys.Length).Select(i => "$key" + i).ToArray();
            command.CommandText += shape.Order.Length == 1 ? $" AND {shape.Order[0]}>{names[0]}" :
                $" AND ({string.Join(',', shape.Order)})>({string.Join(',', names)})";
            for (var i = 0; i < keys.Length; i++) command.Parameters.AddWithValue(names[i], keys[i]!);
        }
        command.CommandText += " ORDER BY " + string.Join(',', shape.Order) + " LIMIT $limit";
        command.Parameters.AddWithValue("$limit", size + 1);
        return command;
    }
}
