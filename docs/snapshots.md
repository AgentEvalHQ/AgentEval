# Snapshot Testing

AgentEval provides snapshot testing capabilities for comparing agent responses against saved baselines. This is especially useful for detecting regressions in agent behavior and ensuring consistent responses over time.

## Overview

Snapshot testing allows you to:

- Save agent responses as baselines (snapshots)
- Compare new responses against saved snapshots
- Ignore dynamic fields (timestamps, IDs)
- Scrub sensitive or variable data with patterns
- Use semantic similarity for fuzzy matching
- Track changes over time

## Quick Start

```csharp
using AgentEval.Snapshots;

// Configure snapshot comparison
var options = new SnapshotOptions
{
    IgnoreFields = new[] { "timestamp", "requestId" },
    ScrubPatterns = new Dictionary<string, string>
    {
        { @"\d{4}-\d{2}-\d{2}", "[DATE]" }
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
var options = new SnapshotOptions
{
    // Fields to completely ignore (by JSON path)
    IgnoreFields = new[]
    {
        "timestamp",
        "requestId",
        "metadata.processedAt",
        "response.headers.date"
    },
    
    // Patterns to scrub (regex → replacement)
    ScrubPatterns = new Dictionary<string, string>
    {
        // Dates
        { @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", "[DATETIME]" },
        { @"\d{4}-\d{2}-\d{2}", "[DATE]" },
        
        // IDs
        { @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "[GUID]" },
        { @"id_[a-zA-Z0-9]+", "[ID]" },
        
        // Secrets
        { @"sk-[a-zA-Z0-9]+", "[API_KEY]" }
    },
    
    // Fields to compare semantically (uses Jaccard similarity)
    SemanticFields = new[]
    {
        "response.content",
        "summary"
    },
    
    // Similarity threshold for semantic comparison (0.0 - 1.0)
    SemanticThreshold = 0.8
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
Console.WriteLine($"Similarity: {result.Similarity:P0}");
Console.WriteLine($"Differences: {result.Differences.Count}");
```

### Comparison Result

```csharp
public record SnapshotComparisonResult
{
    // Whether the snapshots match
    public bool IsMatch { get; }
    
    // Overall similarity score (0.0 - 1.0)
    public double Similarity { get; }
    
    // List of differences found
    public IReadOnlyList<SnapshotDifference> Differences { get; }
}

public record SnapshotDifference
{
    // JSON path to the difference
    public string Path { get; }
    
    // Expected value
    public string? Expected { get; }
    
    // Actual value
    public string? Actual { get; }
    
    // Type of difference
    public DifferenceType Type { get; }
}

public enum DifferenceType
{
    ValueMismatch,
    MissingField,
    ExtraField,
    TypeMismatch
}
```

## SnapshotStore

Persist and retrieve snapshots from disk:

```csharp
var store = new SnapshotStore("./snapshots");

// Save a snapshot
var response = await agent.GetResponseAsync("What is 2+2?");
store.Save("math-test", response);

// Load a snapshot
var baseline = store.Load("math-test");

// Check if snapshot exists
if (store.Exists("math-test"))
{
    var baseline = store.Load("math-test");
    var result = comparer.Compare(baseline, newResponse);
}

// Update a snapshot
store.Save("math-test", newResponse); // Overwrites existing

// Delete a snapshot
store.Delete("math-test");

// List all snapshots
var snapshots = store.List();
foreach (var name in snapshots)
{
    Console.WriteLine(name);
}
```

### File Structure

Snapshots are stored as JSON files:

```
./snapshots/
  ├── math-test.json
  ├── booking-flow.json
  └── error-handling.json
```

## Usage in Tests

### Basic Snapshot Test

```csharp
[Fact]
public async Task Agent_Response_MatchesSnapshot()
{
    var store = new SnapshotStore("./snapshots");
    var comparer = new SnapshotComparer(new SnapshotOptions
    {
        IgnoreFields = new[] { "timestamp" }
    });
    
    var response = await _agent.GetResponseAsync("What is the capital of France?");
    var responseJson = JsonSerializer.Serialize(response);
    
    if (!store.Exists("capital-france"))
    {
        // First run - save the snapshot
        store.Save("capital-france", responseJson);
        Assert.True(true, "Snapshot created");
        return;
    }
    
    var baseline = store.Load("capital-france");
    var result = comparer.Compare(baseline, responseJson);
    
    Assert.True(result.IsMatch, 
        $"Response differs from snapshot:\n{string.Join("\n", result.Differences)}");
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
    var responseJson = JsonSerializer.Serialize(response);
    
    if (updateSnapshots)
    {
        store.Save("my-test", responseJson);
        Assert.True(true, "Snapshot updated");
        return;
    }
    
    // Normal comparison
    var baseline = store.Load("my-test");
    var result = new SnapshotComparer().Compare(baseline, responseJson);
    Assert.True(result.IsMatch);
}
```

Run with: `UPDATE_SNAPSHOTS=true dotnet test`

## Semantic Comparison

For fields where exact matching is too strict, use semantic comparison:

```csharp
var options = new SnapshotOptions
{
    SemanticFields = new[] { "response", "summary", "explanation" },
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

AgentEval also supports the popular [Verify](https://github.com/VerifyTests/Verify) library for more advanced snapshot testing:

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
var options = new SnapshotOptions
{
    ScrubPatterns = new Dictionary<string, string>
    {
        // ISO timestamps
        { @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", "[TIMESTAMP]" },
        
        // GUIDs
        { @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "[GUID]" },
        
        // Email addresses
        { @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", "[EMAIL]" },
        
        // Phone numbers
        { @"\+?\d{1,3}[-.\s]?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}", "[PHONE]" },
        
        // IP addresses
        { @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", "[IP]" }
    }
};
```

### Testing Multiple Response Formats

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

- [CLI Reference](cli.md) - Running snapshot tests from command line
- [Conversations](conversations.md) - Snapshot testing multi-turn conversations
- [Extensibility](extensibility.md) - Custom snapshot comparers
