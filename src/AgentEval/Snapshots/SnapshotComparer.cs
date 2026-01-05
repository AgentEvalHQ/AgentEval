// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentEval.Snapshots;

/// <summary>
/// Configuration for snapshot comparison.
/// </summary>
public class SnapshotOptions
{
    /// <summary>Fields to ignore during comparison.</summary>
    public HashSet<string> IgnoreFields { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "duration", "elapsed", "startTime", "endTime", "id", "requestId", "created"
    };
    
    /// <summary>Patterns to scrub from string values.</summary>
    public List<(Regex Pattern, string Replacement)> ScrubPatterns { get; set; } = new()
    {
        // OpenAI/Azure response IDs
        (new Regex(@"chatcmpl-[a-zA-Z0-9]+"), "chatcmpl-[SCRUBBED]"),
        (new Regex(@"resp_[a-zA-Z0-9]+"), "resp_[SCRUBBED]"),
        
        // Timestamps in various formats
        (new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?"), "[TIMESTAMP]"),
        (new Regex(@"\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}"), "[TIMESTAMP]"),
        
        // GUIDs
        (new Regex(@"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}"), "[GUID]"),
        
        // Durations in various formats
        (new Regex(@"\d+(\.\d+)?\s*(ms|s|seconds|milliseconds)"), "[DURATION]"),
    };
    
    /// <summary>Enable semantic similarity comparison for text fields.</summary>
    public bool UseSemanticComparison { get; set; } = false;
    
    /// <summary>Similarity threshold for semantic comparison (0-1).</summary>
    public double SemanticThreshold { get; set; } = 0.85;
    
    /// <summary>Fields to apply semantic comparison to.</summary>
    public HashSet<string> SemanticFields { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "response", "output", "content", "message", "answer", "text"
    };
}

/// <summary>
/// Result of a snapshot comparison.
/// </summary>
public class SnapshotComparisonResult
{
    /// <summary>Whether the snapshot matched.</summary>
    public bool IsMatch { get; set; }
    
    /// <summary>Differences found.</summary>
    public List<SnapshotDifference> Differences { get; set; } = new();
    
    /// <summary>Fields that were ignored.</summary>
    public List<string> IgnoredFields { get; set; } = new();
    
    /// <summary>Fields that used semantic comparison.</summary>
    public List<SemanticComparisonResult> SemanticResults { get; set; } = new();
}

/// <summary>
/// A difference between expected and actual values.
/// </summary>
public record SnapshotDifference(
    string Path,
    string Expected,
    string Actual,
    string Message);

/// <summary>
/// Result of semantic similarity comparison.
/// </summary>
public record SemanticComparisonResult(
    string Path,
    string Expected,
    string Actual,
    double Similarity,
    bool Passed);

/// <summary>
/// Compares agent responses against saved snapshots.
/// </summary>
public class SnapshotComparer
{
    private readonly SnapshotOptions _options;

    /// <summary>
    /// Initializes a new snapshot comparer.
    /// </summary>
    public SnapshotComparer(SnapshotOptions? options = null)
    {
        _options = options ?? new SnapshotOptions();
    }

    /// <summary>
    /// Compares two JSON objects, applying scrubbing and optional semantic comparison.
    /// </summary>
    public SnapshotComparisonResult Compare(string expected, string actual)
    {
        var result = new SnapshotComparisonResult { IsMatch = true };
        
        try
        {
            using var expectedDoc = JsonDocument.Parse(expected);
            using var actualDoc = JsonDocument.Parse(actual);
            
            CompareElements(expectedDoc.RootElement, actualDoc.RootElement, "$", result);
        }
        catch (JsonException)
        {
            // Fall back to string comparison
            var scrubbedExpected = ApplyScrubbing(expected);
            var scrubbedActual = ApplyScrubbing(actual);
            
            if (!scrubbedExpected.Equals(scrubbedActual, StringComparison.Ordinal))
            {
                result.IsMatch = false;
                result.Differences.Add(new SnapshotDifference("$", scrubbedExpected, scrubbedActual, "String values differ"));
            }
        }

        return result;
    }

    /// <summary>
    /// Applies scrubbing patterns to normalize a value for comparison.
    /// </summary>
    public string ApplyScrubbing(string value)
    {
        foreach (var (pattern, replacement) in _options.ScrubPatterns)
        {
            value = pattern.Replace(value, replacement);
        }
        return value;
    }

    private void CompareElements(JsonElement expected, JsonElement actual, string path, SnapshotComparisonResult result)
    {
        // Get field name from path
        var fieldName = path.Contains('.') ? path.Split('.').Last() : path;
        fieldName = fieldName.TrimStart('[').TrimEnd(']');
        
        // Check if field should be ignored
        if (_options.IgnoreFields.Contains(fieldName))
        {
            result.IgnoredFields.Add(path);
            return;
        }

        // Type mismatch
        if (expected.ValueKind != actual.ValueKind)
        {
            result.IsMatch = false;
            result.Differences.Add(new SnapshotDifference(
                path,
                expected.ValueKind.ToString(),
                actual.ValueKind.ToString(),
                $"Type mismatch: expected {expected.ValueKind}, got {actual.ValueKind}"));
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(expected, actual, path, result);
                break;
                
            case JsonValueKind.Array:
                CompareArrays(expected, actual, path, result);
                break;
                
            case JsonValueKind.String:
                CompareStrings(expected.GetString()!, actual.GetString()!, path, fieldName, result);
                break;
                
            case JsonValueKind.Number:
                if (expected.GetDouble() != actual.GetDouble())
                {
                    result.IsMatch = false;
                    result.Differences.Add(new SnapshotDifference(
                        path,
                        expected.GetRawText(),
                        actual.GetRawText(),
                        "Number values differ"));
                }
                break;
                
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (expected.GetBoolean() != actual.GetBoolean())
                {
                    result.IsMatch = false;
                    result.Differences.Add(new SnapshotDifference(
                        path,
                        expected.GetBoolean().ToString(),
                        actual.GetBoolean().ToString(),
                        "Boolean values differ"));
                }
                break;
        }
    }

    private void CompareObjects(JsonElement expected, JsonElement actual, string path, SnapshotComparisonResult result)
    {
        var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name);
        var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name);

        // Check for missing properties
        foreach (var prop in expectedProps)
        {
            if (_options.IgnoreFields.Contains(prop.Key))
            {
                result.IgnoredFields.Add($"{path}.{prop.Key}");
                continue;
            }
            
            if (!actualProps.ContainsKey(prop.Key))
            {
                result.IsMatch = false;
                result.Differences.Add(new SnapshotDifference(
                    $"{path}.{prop.Key}",
                    prop.Value.Value.GetRawText(),
                    "(missing)",
                    "Property missing in actual"));
            }
            else
            {
                CompareElements(prop.Value.Value, actualProps[prop.Key].Value, $"{path}.{prop.Key}", result);
            }
        }

        // Check for extra properties (optional - may want to ignore)
        foreach (var prop in actualProps.Where(p => !expectedProps.ContainsKey(p.Key)))
        {
            if (!_options.IgnoreFields.Contains(prop.Key))
            {
                // Note: depending on use case, extra properties might be OK
                // For now, we'll flag them as informational, not failures
            }
        }
    }

    private void CompareArrays(JsonElement expected, JsonElement actual, string path, SnapshotComparisonResult result)
    {
        var expectedArr = expected.EnumerateArray().ToList();
        var actualArr = actual.EnumerateArray().ToList();

        if (expectedArr.Count != actualArr.Count)
        {
            result.IsMatch = false;
            result.Differences.Add(new SnapshotDifference(
                path,
                $"[{expectedArr.Count} items]",
                $"[{actualArr.Count} items]",
                "Array length differs"));
            return;
        }

        for (int i = 0; i < expectedArr.Count; i++)
        {
            CompareElements(expectedArr[i], actualArr[i], $"{path}[{i}]", result);
        }
    }

    private void CompareStrings(string expected, string actual, string path, string fieldName, SnapshotComparisonResult result)
    {
        // Apply scrubbing
        var scrubbedExpected = ApplyScrubbing(expected);
        var scrubbedActual = ApplyScrubbing(actual);

        // Check if semantic comparison should be used
        if (_options.UseSemanticComparison && _options.SemanticFields.Contains(fieldName))
        {
            // Placeholder for semantic comparison
            // In real implementation, would use embedding similarity
            var similarity = ComputeSimpleSimilarity(scrubbedExpected, scrubbedActual);
            var passed = similarity >= _options.SemanticThreshold;
            
            result.SemanticResults.Add(new SemanticComparisonResult(
                path, expected, actual, similarity, passed));
            
            if (!passed)
            {
                result.IsMatch = false;
                result.Differences.Add(new SnapshotDifference(
                    path,
                    scrubbedExpected,
                    scrubbedActual,
                    $"Semantic similarity {similarity:P1} below threshold {_options.SemanticThreshold:P1}"));
            }
        }
        else
        {
            // Exact comparison after scrubbing
            if (!scrubbedExpected.Equals(scrubbedActual, StringComparison.Ordinal))
            {
                result.IsMatch = false;
                result.Differences.Add(new SnapshotDifference(
                    path,
                    scrubbedExpected,
                    scrubbedActual,
                    "String values differ"));
            }
        }
    }

    /// <summary>
    /// Simple similarity measure (for demo - real implementation would use embeddings).
    /// Uses Jaccard similarity on word sets.
    /// </summary>
    private static double ComputeSimpleSimilarity(string a, string b)
    {
        var wordsA = new HashSet<string>(
            a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        var wordsB = new HashSet<string>(
            b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        if (wordsA.Count == 0 && wordsB.Count == 0) return 1.0;
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0.0;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();
        
        return (double)intersection / union;
    }
}

/// <summary>
/// Manages saving and loading snapshots.
/// </summary>
public class SnapshotStore
{
    private readonly string _basePath;

    /// <summary>
    /// Initializes a new snapshot store.
    /// </summary>
    /// <param name="basePath">Base directory for snapshots.</param>
    public SnapshotStore(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    /// <summary>
    /// Gets the path for a snapshot file.
    /// </summary>
    public string GetSnapshotPath(string testName, string suffix = "")
    {
        var sanitized = SanitizeFileName(testName);
        var fileName = string.IsNullOrEmpty(suffix) ? $"{sanitized}.json" : $"{sanitized}.{suffix}.json";
        return Path.Combine(_basePath, fileName);
    }

    /// <summary>
    /// Saves a snapshot.
    /// </summary>
    public async Task SaveAsync<T>(string testName, T value, string suffix = "")
    {
        var path = GetSnapshotPath(testName, suffix);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Loads a snapshot if it exists.
    /// </summary>
    public async Task<T?> LoadAsync<T>(string testName, string suffix = "")
    {
        var path = GetSnapshotPath(testName, suffix);
        if (!File.Exists(path)) return default;
        
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>
    /// Checks if a snapshot exists.
    /// </summary>
    public bool Exists(string testName, string suffix = "")
    {
        return File.Exists(GetSnapshotPath(testName, suffix));
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
