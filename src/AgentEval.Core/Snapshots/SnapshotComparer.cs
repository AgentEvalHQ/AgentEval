// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Snapshots;

/// <summary>
/// Compares agent responses against saved snapshots.
/// <para>
/// <b>Thread Safety:</b> Instances of <see cref="SnapshotComparer"/> are thread-safe for concurrent
/// <see cref="Compare"/> calls as long as the <see cref="SnapshotOptions"/> are not modified after
/// construction. The <see cref="ApplyScrubbing"/> method is also thread-safe under the same condition.
/// </para>
/// </summary>
public class SnapshotComparer : ISnapshotComparer
{
    private readonly SnapshotOptions _options;

    /// <summary>
    /// Initializes a new snapshot comparer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="SnapshotOptions.SemanticThreshold"/> is outside the [0.0, 1.0] range.
    /// </exception>
    public SnapshotComparer(SnapshotOptions? options = null)
    {
        _options = options ?? new SnapshotOptions();

        if (_options.SemanticThreshold is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"SemanticThreshold must be between 0.0 and 1.0, was {_options.SemanticThreshold}");
        }
    }

    /// <summary>
    /// Compares two JSON objects, applying scrubbing and optional semantic comparison.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expected"/> or <paramref name="actual"/> is null.</exception>
    public SnapshotComparisonResult Compare(string expected, string actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var result = new SnapshotComparisonResult { IsMatchInternal = true };

        try
        {
            using var expectedDoc = JsonDocument.Parse(expected);
            using var actualDoc = JsonDocument.Parse(actual);

            CompareElements(expectedDoc.RootElement, actualDoc.RootElement, "$", "$", result);
        }
        catch (JsonException)
        {
            // Fall back to string comparison
            var scrubbedExpected = ApplyScrubbing(expected);
            var scrubbedActual = ApplyScrubbing(actual);

            if (!scrubbedExpected.Equals(scrubbedActual, StringComparison.Ordinal))
            {
                result.IsMatchInternal = false;
                result.Differences.Add(new SnapshotDifference("$", scrubbedExpected, scrubbedActual, "String values differ"));
            }
        }

        // Freeze result
        return new SnapshotComparisonResult
        {
            IsMatch = result.IsMatchInternal,
            Differences = result.Differences,
            IgnoredFields = result.IgnoredFields,
            SemanticResults = result.SemanticResults
        };
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

    private void CompareElements(JsonElement expected, JsonElement actual, string path, string fieldName, SnapshotComparisonResult result)
    {
        // Check if field should be ignored
        if (_options.IgnoreFields.Contains(fieldName))
        {
            result.IgnoredFields.Add(path);
            return;
        }

        // Type mismatch — treat True/False as compatible boolean types
        if (expected.ValueKind != actual.ValueKind
            && !(expected.ValueKind is JsonValueKind.True or JsonValueKind.False
                 && actual.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            result.IsMatchInternal = false;
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
                CompareNumbers(expected, actual, path, result);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (expected.GetBoolean() != actual.GetBoolean())
                {
                    result.IsMatchInternal = false;
                    result.Differences.Add(new SnapshotDifference(
                        path,
                        expected.GetBoolean().ToString(),
                        actual.GetBoolean().ToString(),
                        "Boolean values differ"));
                }
                break;

            case JsonValueKind.Null:
                // Both are null — match (type guard ensures both are Null here)
                break;

            case JsonValueKind.Undefined:
                // Both undefined — match
                break;
        }
    }

    private void CompareNumbers(JsonElement expected, JsonElement actual, string path, SnapshotComparisonResult result)
    {
        // Try integer comparison first for exact integer values
        if (expected.TryGetInt64(out var expectedLong) && actual.TryGetInt64(out var actualLong))
        {
            if (expectedLong != actualLong)
            {
                result.IsMatchInternal = false;
                result.Differences.Add(new SnapshotDifference(
                    path,
                    expected.GetRawText(),
                    actual.GetRawText(),
                    "Number values differ"));
            }
            return;
        }

        // Fall back to double with epsilon comparison
        var expectedDouble = expected.GetDouble();
        var actualDouble = actual.GetDouble();
        const double epsilon = 1e-10;

        if (Math.Abs(expectedDouble - actualDouble) > epsilon)
        {
            result.IsMatchInternal = false;
            result.Differences.Add(new SnapshotDifference(
                path,
                expected.GetRawText(),
                actual.GetRawText(),
                "Number values differ"));
        }
    }

    private void CompareObjects(JsonElement expected, JsonElement actual, string path, SnapshotComparisonResult result)
    {
        var expectedProps = new Dictionary<string, JsonProperty>();
        foreach (var prop in expected.EnumerateObject())
        {
            expectedProps[prop.Name] = prop;
        }

        var actualProps = new Dictionary<string, JsonProperty>();
        foreach (var prop in actual.EnumerateObject())
        {
            actualProps[prop.Name] = prop;
        }

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
                result.IsMatchInternal = false;
                result.Differences.Add(new SnapshotDifference(
                    $"{path}.{prop.Key}",
                    prop.Value.Value.GetRawText(),
                    "(missing)",
                    "Property missing in actual"));
            }
            else
            {
                CompareElements(prop.Value.Value, actualProps[prop.Key].Value, $"{path}.{prop.Key}", prop.Key, result);
            }
        }

        // Check for extra properties
        if (_options.AllowExtraProperties == null)
        {
            foreach (var prop in actualProps)
            {
                if (!expectedProps.ContainsKey(prop.Key) && !_options.IgnoreFields.Contains(prop.Key))
                {
                    result.IsMatchInternal = false;
                    result.Differences.Add(new SnapshotDifference(
                        $"{path}.{prop.Key}",
                        "(missing)",
                        prop.Value.Value.GetRawText(),
                        "Extra property in actual"));
                }
            }
        }
    }

    private void CompareArrays(JsonElement expected, JsonElement actual, string path, SnapshotComparisonResult result)
    {
        var expectedArr = expected.EnumerateArray().ToList();
        var actualArr = actual.EnumerateArray().ToList();

        if (expectedArr.Count != actualArr.Count)
        {
            result.IsMatchInternal = false;
            result.Differences.Add(new SnapshotDifference(
                path,
                $"[{expectedArr.Count} items]",
                $"[{actualArr.Count} items]",
                "Array length differs"));
            // Continue comparing elements up to the shorter length (CODE-23 fix)
        }

        var minLength = Math.Min(expectedArr.Count, actualArr.Count);
        for (int i = 0; i < minLength; i++)
        {
            CompareElements(expectedArr[i], actualArr[i], $"{path}[{i}]", $"[{i}]", result);
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
            var similarity = ComputeSimpleSimilarity(scrubbedExpected, scrubbedActual);
            var passed = similarity >= _options.SemanticThreshold;

            // Store scrubbed values for consistency (CODE-33 fix)
            result.SemanticResults.Add(new SemanticComparisonResult(
                path, scrubbedExpected, scrubbedActual, similarity, passed));

            if (!passed)
            {
                result.IsMatchInternal = false;
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
                result.IsMatchInternal = false;
                result.Differences.Add(new SnapshotDifference(
                    path,
                    scrubbedExpected,
                    scrubbedActual,
                    "String values differ"));
            }
        }
    }

    /// <summary>
    /// Simple similarity measure (for demo — real implementation would use embeddings).
    /// Uses Jaccard similarity on word sets.
    /// </summary>
    internal static double ComputeSimpleSimilarity(string a, string b)
    {
        // Split on all whitespace (CODE-32 fix), then lowercase for set comparison
        var wordsA = new HashSet<string>(
            a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
             .Select(w => w.ToLowerInvariant()));
        var wordsB = new HashSet<string>(
            b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
             .Select(w => w.ToLowerInvariant()));

        if (wordsA.Count == 0 && wordsB.Count == 0) return 1.0;
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0.0;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return (double)intersection / union;
    }
}
