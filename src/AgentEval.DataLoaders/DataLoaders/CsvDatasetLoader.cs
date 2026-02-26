// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Loads test cases from CSV (Comma-Separated Values) format.
/// </summary>
/// <remarks>
/// Supports both simple and complex CSV formats:
/// <list type="bullet">
///   <item>First row must be headers</item>
///   <item>Columns: id, input/question/prompt, expected/answer, category (all optional except input)</item>
///   <item>Quoted strings with escaped quotes ("") supported</item>
///   <item>Empty fields handled gracefully</item>
/// </list>
/// </remarks>
public class CsvDatasetLoader : IDatasetLoader
{
    /// <inheritdoc />
    public string Format => "csv";
    
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".tsv" };

    /// <inheritdoc />
    public bool IsTrulyStreaming => true;

    private readonly char _separator;

    /// <summary>
    /// Initializes a new instance of the <see cref="CsvDatasetLoader"/> class.
    /// </summary>
    /// <param name="separator">The field separator character. Default is comma.</param>
    public CsvDatasetLoader(char separator = ',')
    {
        _separator = separator;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        var results = new List<DatasetTestCase>();
        await foreach (var testCase in LoadStreamingAsync(path, ct))
        {
            results.Add(testCase);
        }
        return results;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(
        string path, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dataset file not found: {path}", path);
        }

        // Auto-detect separator from extension
        var separator = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : _separator;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Read header line
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            throw new InvalidDataException($"CSV file is empty or missing header: {path}");
        }

        var headers = ParseCsvLine(headerLine, separator);
        var columnMap = BuildColumnMap(headers);

        if (!columnMap.ContainsKey("input"))
        {
            throw new InvalidDataException(
                $"CSV file must have an 'input', 'question', 'prompt', or 'query' column: {path}");
        }

        int rowNumber = 1;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            rowNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue; // Skip empty lines
            }

            DatasetTestCase? testCase;
            try
            {
                testCase = ParseRow(line, separator, columnMap, rowNumber);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"Error parsing CSV row {rowNumber} in {path}: {ex.Message}", ex);
            }

            if (testCase != null)
            {
                yield return testCase;
            }
        }
    }

    private static Dictionary<string, int> BuildColumnMap(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i].Trim().ToLowerInvariant();
            
            // Map common column name variants to standard names
            var standardName = header switch
            {
                "question" or "prompt" or "query" => "input",
                "answer" or "expected_output" or "response" => "expected",
                "contexts" or "documents" => "context",
                "ground_truth" => "ground_truth",
                _ => header
            };
            
            // Only store first occurrence (in case of duplicates)
            if (!map.ContainsKey(standardName))
            {
                map[standardName] = i;
            }
        }
        
        return map;
    }

    private static DatasetTestCase? ParseRow(
        string line, 
        char separator, 
        Dictionary<string, int> columnMap, 
        int rowNumber)
    {
        var values = ParseCsvLine(line, separator);
        
        string GetValue(string columnName, string defaultValue = "")
        {
            if (columnMap.TryGetValue(columnName, out var index) && index < values.Count)
            {
                var val = values[index].Trim();
                return string.IsNullOrEmpty(val) ? defaultValue : val;
            }
            return defaultValue;
        }

        var input = GetValue("input");
        if (string.IsNullOrWhiteSpace(input))
        {
            return null; // Skip rows without input
        }

        var testCase = new DatasetTestCase
        {
            Id = GetValue("id", $"row_{rowNumber}"),
            Category = GetValue("category"),
            Input = input,
            ExpectedOutput = GetValue("expected"),
        };

        // Parse context if present (comma-separated within field)
        var contextValue = GetValue("context");
        if (!string.IsNullOrEmpty(contextValue))
        {
            // Context might be pipe-separated since commas are the CSV separator
            testCase.Context = contextValue.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Parse expected tools (pipe-separated)
        var toolsValue = GetValue("expected_tools");
        if (string.IsNullOrEmpty(toolsValue))
        {
            toolsValue = GetValue("tools");
        }
        if (!string.IsNullOrEmpty(toolsValue))
        {
            testCase.ExpectedTools = toolsValue.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Parse evaluation criteria (pipe-separated)
        var criteriaValue = GetValue("evaluation_criteria");
        if (!string.IsNullOrEmpty(criteriaValue))
        {
            testCase.EvaluationCriteria = criteriaValue.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Parse tags (pipe-separated)
        var tagsValue = GetValue("tags");
        if (!string.IsNullOrEmpty(tagsValue))
        {
            testCase.Tags = tagsValue.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Parse passing score
        var passingScoreValue = GetValue("passing_score");
        if (!string.IsNullOrEmpty(passingScoreValue) && int.TryParse(passingScoreValue, out var parsedScore))
        {
            testCase.PassingScore = parsedScore;
        }

        // Parse ground_truth JSON blob (e.g., {"name":"tool","arguments":{"key":"value"}})
        var groundTruthValue = GetValue("ground_truth");
        if (!string.IsNullOrEmpty(groundTruthValue))
        {
            try
            {
                using var doc = JsonDocument.Parse(groundTruthValue);
                var root = doc.RootElement;

                var gt = new GroundTruthToolCall();
                if (root.TryGetProperty("name", out var nameEl))
                {
                    gt.Name = nameEl.GetString() ?? "";
                }

                if (root.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsEl.EnumerateObject())
                    {
                        gt.Arguments[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => prop.Value.GetRawText()
                        };
                    }
                }

                testCase.GroundTruth = gt;
            }
            catch (JsonException)
            {
                // Not valid JSON — store as metadata instead
                testCase.Metadata["ground_truth"] = groundTruthValue;
            }
        }

        // Add any extra columns as metadata
        foreach (var kvp in columnMap)
        {
            if (!IsKnownColumn(kvp.Key) && kvp.Value < values.Count)
            {
                var val = values[kvp.Value].Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    testCase.Metadata[kvp.Key] = val;
                }
            }
        }

        return testCase;
    }

    private static bool IsKnownColumn(string name) => name switch
    {
        "id" or "category" or "input" or "expected" => true,
        "context" or "expected_tools" or "tools" => true,
        "evaluation_criteria" or "tags" or "passing_score" => true,
        "ground_truth" => true,
        _ => false
    };

    /// <summary>
    /// Parses a CSV line handling quoted fields.
    /// </summary>
    private static IReadOnlyList<string> ParseCsvLine(string line, char separator)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++; // Skip next quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == separator)
                {
                    result.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }
        }
        
        // Add last field
        result.Add(field.ToString());
        
        return result;
    }
}
