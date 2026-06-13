// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;

namespace AgentEval.RedTeam.Importers;

/// <summary>
/// Imports a CSV seed-prompt dataset into <see cref="AttackProbe"/>s. Used by benchmark packs whose upstream format is
/// CSV (e.g. HarmBench's <c>Behavior</c> column, JailbreakBench's <c>Goal</c> column). The prompt column (and optional
/// id column) are configured per pack, so a single importer handles any column layout.
/// </summary>
/// <remarks>
/// RFC-4180-ish parsing: comma-separated, fields optionally double-quoted, <c>""</c> escapes a quote inside a quoted
/// field, and quoted fields may contain commas / newlines. The header row maps column names (case-insensitive) to
/// indices. Imported rows carry no expected-token oracle, so they score Inconclusive unless an LLM judge is configured
/// (see <see cref="ImportedProbeAttack"/>) — never a fabricated verdict.
/// </remarks>
public sealed class CsvProbeDatasetImporter : IProbeDatasetImporter
{
    private readonly string _promptColumn;
    private readonly string? _idColumn;

    /// <summary>Creates a CSV importer that reads the prompt from <paramref name="promptColumn"/> (and, if given, the
    /// id from <paramref name="idColumn"/>).</summary>
    public CsvProbeDatasetImporter(string promptColumn, string? idColumn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptColumn);
        _promptColumn = promptColumn;
        _idColumn = idColumn;
    }

    /// <inheritdoc />
    public string Format => "csv";

    /// <inheritdoc />
    public IReadOnlyList<AttackProbe> Import(string content, string datasetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);

        var rows = ParseCsv(content);
        if (rows.Count == 0)
            throw new FormatException($"Dataset '{datasetName}' CSV has no rows.");

        var header = rows[0];
        var promptIdx = IndexOf(header, _promptColumn);
        if (promptIdx < 0)
            throw new FormatException(
                $"Dataset '{datasetName}' CSV has no '{_promptColumn}' column (header: {string.Join(", ", header)}).");
        var idIdx = _idColumn is null ? -1 : IndexOf(header, _idColumn);

        var meta = new Dictionary<string, object>
        {
            [JsonProbeDatasetImporter.DatasetKey] = datasetName,
            [JsonProbeDatasetImporter.LicenseKey] = "unspecified",
        };

        var probes = new List<AttackProbe>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (promptIdx >= row.Length) continue;                 // ragged row — skip
            var prompt = row[promptIdx];
            if (string.IsNullOrWhiteSpace(prompt)) continue;       // blank prompt — skip

            var id = idIdx >= 0 && idIdx < row.Length && !string.IsNullOrWhiteSpace(row[idIdx])
                ? $"{datasetName}-{row[idIdx]}"
                : $"{datasetName}-{i:D4}";

            probes.Add(new AttackProbe
            {
                Id = id,
                Prompt = prompt,
                Difficulty = Difficulty.Hard,
                AttackName = datasetName,
                Technique = "imported",
                Source = datasetName,
                Metadata = meta,
            });
        }

        if (probes.Count == 0)
            throw new FormatException($"Dataset '{datasetName}' CSV had a header but no usable '{_promptColumn}' rows.");
        return probes;
    }

    private static int IndexOf(string[] header, string column)
    {
        for (var i = 0; i < header.Length; i++)
            if (string.Equals(header[i].Trim(), column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // Minimal RFC-4180 parser: quoted fields, "" escapes, commas/newlines inside quotes.
    internal static List<string[]> ParseCsv(string text)
    {
        // Strip a leading UTF-8 BOM — the download path's ReadAsStringAsync does NOT strip it, which would otherwise
        // poison the first header cell (e.g. "﻿Behavior") and break the prompt-column lookup.
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var rows = new List<string[]>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var fieldStart = true;   // RFC-4180: a double-quote is only special at the START of a field

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }   // escaped quote
                    else inQuotes = false;                                                       // closing quote
                }
                else field.Append(c);
            }
            else if (c == '"' && fieldStart)
            {
                inQuotes = true;       // opening quote (only when the field is empty so far)
                fieldStart = false;
            }
            else
            {
                switch (c)
                {
                    case ',': record.Add(field.ToString()); field.Clear(); fieldStart = true; break;
                    case '\r': break;                                                   // swallow CR (handle CRLF)
                    case '\n': record.Add(field.ToString()); field.Clear(); rows.Add(record.ToArray()); record = []; fieldStart = true; break;
                    // A '"' here is mid-field (not at field start) → a literal quote in an unquoted field (RFC-4180).
                    default: field.Append(c); fieldStart = false; break;
                }
            }
        }
        // Trailing field/record (file may not end with newline).
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            rows.Add(record.ToArray());
        }
        // Drop a trailing empty row (a file ending in newline produces one).
        if (rows.Count > 0 && rows[^1].Length == 1 && rows[^1][0].Length == 0)
            rows.RemoveAt(rows.Count - 1);
        return rows;
    }
}
