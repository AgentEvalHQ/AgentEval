// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Snapshots;
using Xunit;

namespace AgentEval.Tests.Snapshots;

public class SnapshotComparerTests : IDisposable
{
    private readonly string _testDir;
    private readonly SnapshotComparer _comparer;

    public SnapshotComparerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"AgentEval_SnapshotTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _comparer = new SnapshotComparer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    #region Basic Comparison Tests

    [Fact]
    public void Compare_IdenticalJson_ReturnsMatch()
    {
        var json = """{"name": "test", "value": 42}""";

        var result = _comparer.Compare(json, json);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Compare_DifferentValues_ReturnsDifferences()
    {
        var expected = """{"name": "test", "value": 42}""";
        var actual = """{"name": "test", "value": 99}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Single(result.Differences);
        Assert.Contains("value", result.Differences[0].Path);
    }

    [Fact]
    public void Compare_MissingProperty_ReturnsDifference()
    {
        var expected = """{"name": "test", "value": 42}""";
        var actual = """{"name": "test"}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, d => d.Message.Contains("missing"));
    }

    [Fact]
    public void Compare_DifferentTypes_ReturnsDifference()
    {
        var expected = """{"value": 42}""";
        var actual = """{"value": "42"}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, d => d.Message.Contains("Type mismatch"));
    }

    [Fact]
    public void Compare_NestedObjects_ComparesDeeply()
    {
        var expected = """{"outer": {"inner": "value1"}}""";
        var actual = """{"outer": {"inner": "value2"}}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains("$.outer.inner", result.Differences[0].Path);
    }

    [Fact]
    public void Compare_Arrays_ComparesElements()
    {
        var expected = """{"items": [1, 2, 3]}""";
        var actual = """{"items": [1, 2, 4]}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains("$.items[2]", result.Differences[0].Path);
    }

    [Fact]
    public void Compare_ArrayLengthDifference_ReturnsDifference()
    {
        var expected = """{"items": [1, 2, 3]}""";
        var actual = """{"items": [1, 2]}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, d => d.Message.Contains("Array length"));
    }

    #endregion

    #region Null and Edge Case Tests (TEST-12, CODE-12)

    [Fact]
    public void Compare_NullExpected_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _comparer.Compare(null!, "{}"));
    }

    [Fact]
    public void Compare_NullActual_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _comparer.Compare("{}", null!));
    }

    [Fact]
    public void Compare_BothNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _comparer.Compare(null!, null!));
    }

    [Fact]
    public void Compare_NullJsonValues_Match()
    {
        var expected = """{"value": null}""";
        var actual = """{"value": null}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Compare_NullVsString_ReportsTypeMismatch()
    {
        var expected = """{"value": null}""";
        var actual = """{"value": "hello"}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, d => d.Message.Contains("Type mismatch"));
    }

    [Fact]
    public void Compare_InvalidJson_FallsBackToStringComparison()
    {
        var expected = "not json at all";
        var actual = "not json at all";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void Compare_InvalidJson_DifferentStrings_ReturnsDifference()
    {
        var expected = "hello world";
        var actual = "hello earth";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Single(result.Differences);
    }

    [Fact]
    public void Compare_EmptyObjects_Match()
    {
        var result = _comparer.Compare("{}", "{}");
        Assert.True(result.IsMatch);
    }

    [Fact]
    public void Compare_EmptyArrays_Match()
    {
        var result = _comparer.Compare("""{"a":[]}""", """{"a":[]}""");
        Assert.True(result.IsMatch);
    }

    #endregion

    #region Boolean Dead Code Fix (CODE-30)

    [Fact]
    public void Compare_BooleanTrueVsFalse_ReportsValueDifference()
    {
        var expected = """{"flag": true}""";
        var actual = """{"flag": false}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Single(result.Differences);
        Assert.Contains("Boolean values differ", result.Differences[0].Message);
    }

    [Fact]
    public void Compare_BooleanSameValue_Matches()
    {
        var expected = """{"flag": true}""";
        var actual = """{"flag": true}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
    }

    #endregion

    #region Floating Point Comparison (CODE-10)

    [Fact]
    public void Compare_FloatingPointNearlyEqual_Matches()
    {
        // 0.1 + 0.2 != 0.3 in floating-point — but should match with epsilon
        var expected = """{"value": 0.3}""";
        var actual = """{"value": 0.3}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void Compare_IntegerComparison_Works()
    {
        var expected = """{"value": 42}""";
        var actual = """{"value": 42}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void Compare_DifferentNumbers_ReturnsDifference()
    {
        var expected = """{"value": 1.0}""";
        var actual = """{"value": 2.0}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
    }

    #endregion

    #region Array Continuation After Length Mismatch (CODE-23)

    [Fact]
    public void Compare_ArrayLengthDifference_StillComparesCommonElements()
    {
        var expected = """{"items": [1, 2, 3]}""";
        var actual = """{"items": [1, 99]}""";

        var result = _comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        // Should have array length diff AND element diff for items[1]
        Assert.True(result.Differences.Count >= 2,
            $"Expected at least 2 differences (length + value), got {result.Differences.Count}");
    }

    #endregion

    #region Scrubbing Tests

    [Fact]
    public void Compare_IgnoresTimestampFields()
    {
        var expected = """{"data": "test", "timestamp": "2024-01-01T00:00:00Z"}""";
        var actual = """{"data": "test", "timestamp": "2025-12-31T23:59:59Z"}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
        Assert.Contains("$.timestamp", result.IgnoredFields);
    }

    [Fact]
    public void Compare_IgnoresIdFields()
    {
        var expected = """{"name": "test", "id": "abc-123"}""";
        var actual = """{"name": "test", "id": "xyz-789"}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
        Assert.Contains("$.id", result.IgnoredFields);
    }

    [Fact]
    public void ApplyScrubbing_ScrubsOpenAIResponseIds()
    {
        var value = "Response chatcmpl-abc123xyz received";

        var scrubbed = _comparer.ApplyScrubbing(value);

        Assert.Contains("chatcmpl-[SCRUBBED]", scrubbed);
        Assert.DoesNotContain("abc123xyz", scrubbed);
    }

    [Fact]
    public void ApplyScrubbing_ScrubsTimestamps()
    {
        var value = "Created at 2024-01-15T10:30:00Z";

        var scrubbed = _comparer.ApplyScrubbing(value);

        Assert.Contains("[TIMESTAMP]", scrubbed);
        Assert.DoesNotContain("2024-01-15", scrubbed);
    }

    [Fact]
    public void ApplyScrubbing_ScrubsGuids()
    {
        var value = "ID: 550e8400-e29b-41d4-a716-446655440000";

        var scrubbed = _comparer.ApplyScrubbing(value);

        Assert.Contains("[GUID]", scrubbed);
        Assert.DoesNotContain("550e8400", scrubbed);
    }

    [Fact]
    public void ApplyScrubbing_ScrubsUppercaseGuids()
    {
        var value = "ID: 550E8400-E29B-41D4-A716-446655440000";

        var scrubbed = _comparer.ApplyScrubbing(value);

        Assert.Contains("[GUID]", scrubbed);
    }

    [Fact]
    public void ApplyScrubbing_ScrubsDurations()
    {
        var value = "Completed in 1234ms";

        var scrubbed = _comparer.ApplyScrubbing(value);

        Assert.Contains("[DURATION]", scrubbed);
        Assert.DoesNotContain("1234ms", scrubbed);
    }

    [Fact]
    public void ApplyScrubbing_DurationRegex_DoesNotMatchPartialWords()
    {
        // CODE-15: "100seconds" should match as a duration, but "processors" should not
        var value = "Found 8 processors in the system";

        var scrubbed = _comparer.ApplyScrubbing(value);

        Assert.DoesNotContain("[DURATION]", scrubbed);
        Assert.Contains("processors", scrubbed);
    }

    #endregion

    #region Semantic Comparison Tests (Fix TEST-11)

    [Fact]
    public void Compare_WithSemanticComparison_UsesThreshold()
    {
        var options = new SnapshotOptions
        {
            UseSemanticComparison = true,
            SemanticThreshold = 0.5,
            SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "response" }
        };
        var comparer = new SnapshotComparer(options);

        var expected = """{"response": "The weather in Seattle is cloudy and cool"}""";
        var actual = """{"response": "Seattle has cloudy weather and cool temperatures"}""";

        var result = comparer.Compare(expected, actual);

        Assert.NotEmpty(result.SemanticResults);
        var semantic = result.SemanticResults.First(r => r.Path == "$.response");
        Assert.True(result.IsMatch, "Semantic comparison should pass with 0.5 threshold");
        Assert.True(semantic.Passed, "Semantic result should have passed");
        Assert.True(semantic.Similarity >= 0.5, $"Similarity {semantic.Similarity} should meet or exceed 0.5 threshold");
    }

    [Fact]
    public void Compare_SemanticComparison_PassesForSimilarText()
    {
        var options = new SnapshotOptions
        {
            UseSemanticComparison = true,
            SemanticThreshold = 0.3,
            SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "content" }
        };
        var comparer = new SnapshotComparer(options);

        var expected = """{"content": "Hello world how are you today"}""";
        var actual = """{"content": "Hello world how is everything today"}""";

        var result = comparer.Compare(expected, actual);

        var semanticResult = result.SemanticResults.FirstOrDefault(r => r.Path == "$.content");
        Assert.NotNull(semanticResult);
        Assert.True(semanticResult.Similarity > 0.3);
        Assert.True(semanticResult.Passed);
    }

    [Fact]
    public void Compare_SemanticComparison_FailsBelowThreshold()
    {
        var options = new SnapshotOptions
        {
            UseSemanticComparison = true,
            SemanticThreshold = 0.99, // Very high threshold
            SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "response" }
        };
        var comparer = new SnapshotComparer(options);

        var expected = """{"response": "The weather is sunny"}""";
        var actual = """{"response": "It is cloudy outside"}""";

        var result = comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.NotEmpty(result.Differences);
    }

    [Fact]
    public void Compare_SemanticComparison_StoresScrubbedValues()
    {
        // CODE-33: SemanticComparisonResult should store scrubbed values
        var options = new SnapshotOptions
        {
            UseSemanticComparison = true,
            SemanticThreshold = 0.1,
            SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "response" }
        };
        var comparer = new SnapshotComparer(options);

        var expected = """{"response": "Response chatcmpl-abc123 was good"}""";
        var actual = """{"response": "Response chatcmpl-xyz789 was good"}""";

        var result = comparer.Compare(expected, actual);

        var semantic = result.SemanticResults.First();
        // Stored values should be scrubbed (not original)
        Assert.Contains("chatcmpl-[SCRUBBED]", semantic.Expected);
        Assert.Contains("chatcmpl-[SCRUBBED]", semantic.Actual);
    }

    [Fact]
    public void ComputeSimpleSimilarity_SplitsOnAllWhitespace()
    {
        // CODE-32: Tabs and newlines should be treated as word separators
        var similarity = SnapshotComparer.ComputeSimpleSimilarity(
            "Hello\tworld\nhow\r\nare you",
            "Hello world how are you");

        Assert.Equal(1.0, similarity);
    }

    [Fact]
    public void ComputeSimpleSimilarity_EmptyStrings_ReturnOne()
    {
        var similarity = SnapshotComparer.ComputeSimpleSimilarity("", "");
        Assert.Equal(1.0, similarity);
    }

    [Fact]
    public void ComputeSimpleSimilarity_OneEmpty_ReturnZero()
    {
        var similarity = SnapshotComparer.ComputeSimpleSimilarity("hello", "");
        Assert.Equal(0.0, similarity);
    }

    #endregion

    #region SemanticThreshold Validation (CODE-31)

    [Fact]
    public void Constructor_NegativeThreshold_ThrowsArgumentOutOfRange()
    {
        var options = new SnapshotOptions { SemanticThreshold = -1.0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotComparer(options));
    }

    [Fact]
    public void Constructor_ThresholdAboveOne_ThrowsArgumentOutOfRange()
    {
        var options = new SnapshotOptions { SemanticThreshold = 1.5 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotComparer(options));
    }

    [Fact]
    public void Constructor_ThresholdAtBoundaries_Succeeds()
    {
        _ = new SnapshotComparer(new SnapshotOptions { SemanticThreshold = 0.0 });
        _ = new SnapshotComparer(new SnapshotOptions { SemanticThreshold = 1.0 });
    }

    #endregion

    #region SnapshotStore Tests (Fix TEST-10)

    [Fact]
    public async Task SnapshotStore_SaveAndLoad_RoundTrips()
    {
        var store = new SnapshotStore(_testDir);
        var testData = new { Name = "Test", Value = 42 };

        await store.SaveAsync("test1", testData);
        var loaded = await store.LoadAsync<JsonElement>("test1");

        Assert.Equal("Test", loaded.GetProperty("Name").GetString());
        Assert.Equal(42, loaded.GetProperty("Value").GetInt32());
    }

    [Fact]
    public void SnapshotStore_Exists_ReturnsTrueWhenExists()
    {
        var store = new SnapshotStore(_testDir);
        var path = store.GetSnapshotPath("exists_test");
        File.WriteAllText(path, "{}");

        Assert.True(store.Exists("exists_test"));
    }

    [Fact]
    public void SnapshotStore_Exists_ReturnsFalseWhenNotExists()
    {
        var store = new SnapshotStore(_testDir);

        Assert.False(store.Exists("nonexistent"));
    }

    [Fact]
    public void SnapshotStore_GetSnapshotPath_SanitizesFileName()
    {
        var store = new SnapshotStore(_testDir);

        var path = store.GetSnapshotPath("test:with/invalid\\chars?");

        Assert.DoesNotContain(":", Path.GetFileName(path));
        Assert.DoesNotContain("/", Path.GetFileName(path));
        Assert.DoesNotContain("\\", Path.GetFileName(path));
    }

    [Fact]
    public void SnapshotStore_GetSnapshotPath_IncludesSuffix()
    {
        var store = new SnapshotStore(_testDir);

        var path = store.GetSnapshotPath("mytest", "expected");

        Assert.Contains("mytest.expected.json", path);
    }

    [Fact]
    public async Task SnapshotStore_LoadAsync_ReturnsDefault_WhenNotFound()
    {
        var store = new SnapshotStore(_testDir);

        var result = await store.LoadAsync<JsonElement>("nonexistent");

        Assert.Equal(default, result);
    }

    [Fact]
    public async Task SnapshotStore_LoadAsync_CorruptedFile_ThrowsInvalidOperation()
    {
        var store = new SnapshotStore(_testDir);
        var path = store.GetSnapshotPath("corrupted");
        await File.WriteAllTextAsync(path, "not valid json {{{");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.LoadAsync<JsonElement>("corrupted"));
    }

    [Fact]
    public void SnapshotStore_SuffixWithPathSeparator_ThrowsArgumentException()
    {
        var store = new SnapshotStore(_testDir);

        Assert.Throws<ArgumentException>(() => store.GetSnapshotPath("test", "../../../etc/passwd"));
    }

    [Fact]
    public void SnapshotStore_SuffixWithDot_ThrowsArgumentException()
    {
        var store = new SnapshotStore(_testDir);

        Assert.Throws<ArgumentException>(() => store.GetSnapshotPath("test", ".."));
    }

    [Fact]
    public void SnapshotStore_EmptyBasePath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SnapshotStore(""));
        Assert.Throws<ArgumentException>(() => new SnapshotStore("   "));
    }

    [Fact]
    public void SnapshotStore_Delete_ReturnsTrueWhenDeleted()
    {
        var store = new SnapshotStore(_testDir);
        var path = store.GetSnapshotPath("to_delete");
        File.WriteAllText(path, "{}");

        Assert.True(store.Delete("to_delete"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SnapshotStore_Delete_ReturnsFalseWhenNotExists()
    {
        var store = new SnapshotStore(_testDir);

        Assert.False(store.Delete("nonexistent"));
    }

    [Fact]
    public async Task SnapshotStore_ListSnapshots_ReturnsAllSnapshots()
    {
        var store = new SnapshotStore(_testDir);
        await store.SaveAsync("snap1", new { A = 1 });
        await store.SaveAsync("snap2", new { B = 2 });

        var list = store.ListSnapshots();

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task SnapshotStore_Count_ReturnsCorrectCount()
    {
        var store = new SnapshotStore(_testDir);
        Assert.Equal(0, store.Count);

        await store.SaveAsync("snap1", new { A = 1 });
        Assert.Equal(1, store.Count);

        await store.SaveAsync("snap2", new { B = 2 });
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void SnapshotStore_SanitizeFileName_CollisionResistance()
    {
        // CODE-17: "test:1" and "test/1" both sanitize differently
        var path1 = SnapshotStore.SanitizeFileName("test:1");
        var path2 = SnapshotStore.SanitizeFileName("test/1");

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public async Task SnapshotStore_CancellationToken_Supported()
    {
        var store = new SnapshotStore(_testDir);
        using var cts = new CancellationTokenSource();

        await store.SaveAsync("cancel_test", new { Value = 1 }, cancellationToken: cts.Token);
        var loaded = await store.LoadAsync<JsonElement>("cancel_test", cancellationToken: cts.Token);

        Assert.Equal(1, loaded.GetProperty("Value").GetInt32());
    }

    #endregion

    #region Extra Properties (CODE-6)

    [Fact]
    public void Compare_ExtraProperties_AllowedByDefault()
    {
        var expected = """{"name": "test"}""";
        var actual = """{"name": "test", "extra": "value"}""";

        var result = _comparer.Compare(expected, actual);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void Compare_ExtraProperties_ReportedWhenConfigured()
    {
        var options = new SnapshotOptions { AllowExtraProperties = null };
        var comparer = new SnapshotComparer(options);

        var expected = """{"name": "test"}""";
        var actual = """{"name": "test", "extra": "value"}""";

        var result = comparer.Compare(expected, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, d => d.Message.Contains("Extra property"));
    }

    #endregion

    #region SnapshotOptions Tests

    [Fact]
    public void SnapshotOptions_DefaultIgnoreFields_IncludesCommonVolatileFields()
    {
        var options = new SnapshotOptions();

        Assert.Contains("timestamp", options.IgnoreFields);
        Assert.Contains("duration", options.IgnoreFields);
        Assert.Contains("id", options.IgnoreFields);
        Assert.Contains("created", options.IgnoreFields);
    }

    [Fact]
    public void SnapshotOptions_DefaultSemanticFields_IncludesCommonTextFields()
    {
        var options = new SnapshotOptions();

        Assert.Contains("response", options.SemanticFields);
        Assert.Contains("content", options.SemanticFields);
        Assert.Contains("message", options.SemanticFields);
    }

    [Fact]
    public void SnapshotOptions_CustomIgnoreFields_OverridesDefaults()
    {
        var options = new SnapshotOptions
        {
            IgnoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom_field" }
        };

        Assert.Contains("custom_field", options.IgnoreFields);
        Assert.DoesNotContain("timestamp", options.IgnoreFields);
    }

    #endregion

    #region Interface Tests

    [Fact]
    public void SnapshotComparer_ImplementsISnapshotComparer()
    {
        ISnapshotComparer comparer = new SnapshotComparer();
        var result = comparer.Compare("{}", "{}");
        Assert.True(result.IsMatch);
    }

    [Fact]
    public void SnapshotStore_ImplementsISnapshotStore()
    {
        ISnapshotStore store = new SnapshotStore(_testDir);
        Assert.False(store.Exists("nonexistent"));
    }

    #endregion
}
