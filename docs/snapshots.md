# Snapshot Evaluation

AgentEval provides snapshot evaluation capabilities for comparing agent responses against saved baselines. This is especially useful for detecting regressions in agent behavior and ensuring consistent responses over time.

## Overview

Snapshot evaluation allows you to:

- Save agent responses as baselines (snapshots)
- Compare new responses against saved snapshots
- Ignore dynamic fields (timestamps, IDs)
- Scrub sensitive or variable data with patterns
- Use semantic similarity for fuzzy matching
- Track changes over time

## Quick Start

```csharp
using AgentEval.Snapshots;
using System.Text.RegularExpressions;

// Configure snapshot comparison
var options = new SnapshotOptions
{
    IgnoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "requestId"
    },
    ScrubPatterns = new List<(Regex Pattern, string Replacement)>
    {
        (new Regex(@"\d{4}-\d{2}-\d{2}"), "[DATE]")
    }
};

// Compare responses
var comparer = new SnapshotComparer(options);
var result = comparer.Compare(expectedJson, actualJson);

if (result.IsMatch)
{
    Console.WriteLine("✅ Response matches snapshot");
}
else
{
    Console.WriteLine("❌ Differences found:");
    foreach (var diff in result.Differences)
    {
        Console.WriteLine($"  {diff.Path}: {diff.Expected} → {diff.Actual}");
    }
}
```

## SnapshotOptions

Configure how snapshots are compared:

```csharp
using System.Text.RegularExpressions;

var options = new SnapshotOptions
{
    // Fields to completely ignore (case-insensitive HashSet)
    IgnoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp",
        "requestId",
        "duration",
        "elapsed"
    },
    
    // Patterns to scrub (Regex, Replacement) tuples
    ScrubPatterns = new List<(Regex Pattern, string Replacement)>
    {
        // Dates
        (new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}"), "[DATETIME]"),
        (new Regex(@"\d{4}-\d{2}-\d{2}"), "[DATE]"),
        
        // IDs
        (new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"), "[GUID]"),
        (new Regex(@"id_[a-zA-Z0-9]+"), "[ID]"),
        
        // Secrets
        (new Regex(@"sk-[a-zA-Z0-9]+"), "[API_KEY]")
    },
    
    // Enable semantic similarity comparison for text fields
    UseSemanticComparison = true,
    
    // Fields to compare semantically (case-insensitive HashSet)
    SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "response",
        "content",
        "summary"
    },
    
    // Similarity threshold for semantic comparison (0.0 - 1.0)
    SemanticThreshold = 0.85
};
```

## SnapshotComparer

The `SnapshotComparer` performs JSON comparison with configurable options:

```csharp
var comparer = new SnapshotComparer(options);

// Compare JSON strings
var result = comparer.Compare(expectedJson, actualJson);

// Access results
Console.WriteLine($"Match: {result.IsMatch}");
Console.WriteLine($"Differences: {result.Differences.Count}");
Console.WriteLine($"Ignored Fields: {result.IgnoredFields.Count}");
Console.WriteLine($"Semantic Results: {result.SemanticResults.Count}");

// Apply scrubbing to a value
var scrubbed = comparer.ApplyScrubbing(rawValue);
```

### Comparison Result

```csharp
public class SnapshotComparisonResult
{
    // Whether the snapshots match
    public bool IsMatch { get; set; }
    
    // List of differences found
    public List<SnapshotDifference> Differences { get; set; }
    
    // Fields that were ignored during comparison
    public List<string> IgnoredFields { get; set; }
    
    // Results of semantic comparisons
    public List<SemanticComparisonResult> SemanticResults { get; set; }
}

public record SnapshotDifference(
    string Path,      // JSON path to the difference
    string Expected,  // Expected value
    string Actual,    // Actual value
    string Message    // Description of the difference
);

public record SemanticComparisonResult(
    string Path,      // JSON path
    string Expected,  // Expected value  
    string Actual,    // Actual value
    double Similarity, // Computed similarity score
    bool Passed       // Whether it met the threshold
);
```

## SnapshotStore

Persist and retrieve snapshots from disk:

```csharp
var store = new SnapshotStore("./snapshots");

// Save a snapshot (async)
var response = await agent.GetResponseAsync("What is 2+2?");
await store.SaveAsync("math-test", response);

// Save with a suffix for variants
await store.SaveAsync("math-test", response, "v2");

// Load a snapshot (async)
var baseline = await store.LoadAsync<MyResponseType>("math-test");

// Load with suffix
var baselineV2 = await store.LoadAsync<MyResponseType>("math-test", "v2");

// Check if snapshot exists
if (store.Exists("math-test"))
{
    var baseline = await store.LoadAsync<MyResponseType>("math-test");
    var result = comparer.Compare(
        JsonSerializer.Serialize(baseline), 
        JsonSerializer.Serialize(newResponse));
}

// Get the file path for a snapshot
var path = store.GetSnapshotPath("math-test");
var pathWithSuffix = store.GetSnapshotPath("math-test", "v2");
```

### File Structure

Snapshots are stored as JSON files:

```
./snapshots/
  ├── math-test.json
  ├── math-test.v2.json
  ├── booking-flow.json
  └── error-handling.json
```

## Usage in Evaluations

### Basic Snapshot Evaluation

```csharp
[Fact]
public async Task Agent_Response_MatchesSnapshot()
{
    var store = new SnapshotStore("./snapshots");
    var comparer = new SnapshotComparer(new SnapshotOptions
    {
        IgnoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp"
        }
    });
    
    var response = await _agent.GetResponseAsync("What is the capital of France?");
    var responseJson = JsonSerializer.Serialize(response);
    
    if (!store.Exists("capital-france"))
    {
        // First run - save the snapshot
        await store.SaveAsync("capital-france", response);
        Assert.True(true, "Snapshot created");
        return;
    }
    
    var baselineJson = await File.ReadAllTextAsync(store.GetSnapshotPath("capital-france"));
    var result = comparer.Compare(baselineJson, responseJson);
    
    Assert.True(result.IsMatch, 
        $"Response differs from snapshot:\n{string.Join("\n", result.Differences.Select(d => $"{d.Path}: {d.Message}"))}");
}
```

### Update Snapshots Programmatically

```csharp
[Fact]
public async Task Agent_Response_UpdateSnapshot()
{
    var updateSnapshots = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "true";
    
    var store = new SnapshotStore("./snapshots");
    var response = await _agent.GetResponseAsync("...");
    
    if (updateSnapshots)
    {
        await store.SaveAsync("my-test", response);
        Assert.True(true, "Snapshot updated");
        return;
    }
    
    // Normal comparison
    var baselineJson = await File.ReadAllTextAsync(store.GetSnapshotPath("my-test"));
    var responseJson = JsonSerializer.Serialize(response);
    var result = new SnapshotComparer().Compare(baselineJson, responseJson);
    Assert.True(result.IsMatch);
}
```

Run with: `UPDATE_SNAPSHOTS=true dotnet test`

## Semantic Comparison

For fields where exact matching is too strict, use semantic comparison:

```csharp
var options = new SnapshotOptions
{
    UseSemanticComparison = true,
    SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "response", "summary", "explanation"
    },
    SemanticThreshold = 0.7  // 70% similarity required
};

var comparer = new SnapshotComparer(options);

// These would match semantically:
// Expected: "The capital of France is Paris"
// Actual: "Paris is the capital city of France"
```

The semantic comparison uses Jaccard similarity on word sets, which works well for:
- Rephrased sentences
- Different word order
- Minor wording changes

## Integration with Verify.Xunit

AgentEval also supports the popular [Verify](https://github.com/VerifyTests/Verify) library for more advanced snapshot evaluation:

```csharp
using VerifyXunit;

[UsesVerify]
public class AgentSnapshotTests
{
    [Fact]
    public async Task Response_MatchesVerifySnapshot()
    {
        var response = await _agent.GetResponseAsync("What is 2+2?");
        
        await Verify(response)
            .ScrubMember("timestamp")
            .ScrubMember("requestId");
    }
}
```

## Best Practices

1. **Ignore volatile fields** - Always ignore timestamps, request IDs, and other dynamic data
2. **Scrub secrets** - Use patterns to replace API keys, tokens, and sensitive data
3. **Use semantic matching for natural language** - Exact matching is too brittle for LLM outputs
4. **Version your snapshots** - Commit snapshot files to source control
5. **Review snapshot updates** - Don't blindly update; verify changes are intentional
6. **Organize by feature** - Use descriptive names and folder structure
7. **Set appropriate thresholds** - Start with 0.8 similarity and adjust based on your needs

## Common Patterns

### Scrubbing Dynamic Data

```csharp
using System.Text.RegularExpressions;

var options = new SnapshotOptions
{
    ScrubPatterns = new List<(Regex Pattern, string Replacement)>
    {
        // ISO timestamps
        (new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?"), "[TIMESTAMP]"),
        
        // GUIDs
        (new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"), "[GUID]"),
        
        // Email addresses
        (new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"), "[EMAIL]"),
        
        // Phone numbers
        (new Regex(@"\+?\d{1,3}[-.\s]?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}"), "[PHONE]"),
        
        // IP addresses
        (new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}"), "[IP]")
    }
};
```

### Evaluating Multiple Response Formats

```csharp
[Theory]
[InlineData("json")]
[InlineData("markdown")]
[InlineData("plain")]
public async Task Response_Format_MatchesSnapshot(string format)
{
    var store = new SnapshotStore("./snapshots");
    var response = await _agent.GetResponseAsync($"Format: {format}");
    
    var snapshotName = $"format-{format}";
    // ... compare with format-specific snapshot
}
```

## See Also

- [Conversations](conversations.md) - Snapshot evaluating multi-turn conversations
- [Extensibility](extensibility.md) - Custom snapshot comparers
- [Sample 07](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample07_SnapshotTesting.cs) - Runnable snapshot testing example
