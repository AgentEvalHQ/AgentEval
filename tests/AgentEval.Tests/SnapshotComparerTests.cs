// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Snapshots;
using Xunit;

namespace AgentEval.Tests;

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
    public void ApplyScrubbing_ScrubsDurations()
    {
        var value = "Completed in 1234ms";
        
        var scrubbed = _comparer.ApplyScrubbing(value);
        
        Assert.Contains("[DURATION]", scrubbed);
        Assert.DoesNotContain("1234ms", scrubbed);
    }

    #endregion

    #region Semantic Comparison Tests

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
        Assert.Contains(result.SemanticResults, r => r.Path == "$.response");
    }

    [Fact]
    public void Compare_SemanticComparison_PassesForSimilarText()
    {
        var options = new SnapshotOptions
        {
            UseSemanticComparison = true,
            SemanticThreshold = 0.3, // Low threshold for word overlap
            SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "content" }
        };
        var comparer = new SnapshotComparer(options);
        
        // These share many words
        var expected = """{"content": "Hello world how are you today"}""";
        var actual = """{"content": "Hello world how is everything today"}""";
        
        var result = comparer.Compare(expected, actual);
        
        var semanticResult = result.SemanticResults.FirstOrDefault(r => r.Path == "$.content");
        Assert.NotNull(semanticResult);
        Assert.True(semanticResult.Similarity > 0.3);
    }

    #endregion

    #region SnapshotStore Tests

    [Fact]
    public async Task SnapshotStore_SaveAndLoad_RoundTrips()
    {
        var store = new SnapshotStore(_testDir);
        var testData = new { Name = "Test", Value = 42 };
        
        await store.SaveAsync("test1", testData);
        var loaded = await store.LoadAsync<Dictionary<string, object>>("test1");
        
        Assert.NotNull(loaded);
        Assert.Equal("Test", loaded["Name"].ToString());
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
}
