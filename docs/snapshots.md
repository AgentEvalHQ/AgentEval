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
- Manage snapshots (list, count, delete)

## Quick Start

```csharp
using AgentEval.Snapshots;
using System.Text.Json;
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
        (new Regex(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled), "[DATE]")
    }
};

// Compare responses
var comparer = new SnapshotComparer(options);
string expectedJson = """{"name": "test", "timestamp": "2024-01-01"}""";
string actualJson = """{"name": "test", "timestamp": "2025-06-15"}""";
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
    // Defaults: timestamp, duration, elapsed, startTime, endTime, id, requestId, created
    IgnoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp",
        "duration",
        "elapsed",
        "startTime",
        "endTime",
        "id",
        "requestId",
        "created"
    },
    
    // Patterns to scrub (Regex, Replacement) tuples
    // Defaults include: chatcmpl-[SCRUBBED], resp_[SCRUBBED], [TIMESTAMP], [GUID], [DURATION]
    ScrubPatterns = new List<(Regex Pattern, string Replacement)>
    {
        // Dates
        (new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", RegexOptions.Compiled), "[TIMESTAMP]"),
        (new Regex(@"\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}", RegexOptions.Compiled), "[TIMESTAMP]"),
        
        // GUIDs (with word boundaries)
        (new Regex(@"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b", RegexOptions.Compiled), "[GUID]"),
        
        // OpenAI/Azure response IDs
        (new Regex(@"chatcmpl-[a-zA-Z0-9]+", RegexOptions.Compiled), "chatcmpl-[SCRUBBED]"),
        (new Regex(@"resp_[a-zA-Z0-9]+", RegexOptions.Compiled), "resp_[SCRUBBED]"),
        
        // Durations (with word boundaries to avoid false positives)
        (new Regex(@"\b\d+(\.\d+)?\s*(ms|s|seconds|milliseconds)\b", RegexOptions.Compiled), "[DURATION]"),
        
        // Secrets
        (new Regex(@"sk-[a-zA-Z0-9]+", RegexOptions.Compiled), "[API_KEY]")
    },
    
    // Enable semantic similarity comparison for text fields
    UseSemanticComparison = true,
    
    // Fields to compare semantically (case-insensitive HashSet)
    // Defaults: response, output, content, message, answer, text
    SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "response",
        "output",
        "content",
        "message",
        "answer",
        "text"
    },
    
    // Similarity threshold for semantic comparison (0.0 - 1.0, default: 0.85)
    SemanticThreshold = 0.85,
    
    // Extra properties handling:
    //   true  = silently allow extra properties in actual (default)
    //   false = (reserved for future use)
    //   null  = report extra properties as differences
    AllowExtraProperties = true
};
```

## SnapshotComparer

The `SnapshotComparer` performs JSON comparison with configurable options. It implements `ISnapshotComparer` for dependency injection.

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
    // Whether the snapshots match (init-only)
    public bool IsMatch { get; init; }
    
    // List of differences found
    public List<SnapshotDifference> Differences { get; init; }
    
    // Fields that were ignored during comparison
    public List<string> IgnoredFields { get; init; }
    
    // Results of semantic comparisons
    public List<SemanticComparisonResult> SemanticResults { get; init; }
}

public record SnapshotDifference(
    string Path,      // JSON path to the difference (e.g., "$.response")
    string Expected,  // Expected value
    string Actual,    // Actual value
    string Message    // Description of the difference
);

public record SemanticComparisonResult(
    string Path,      // JSON path
    string Expected,  // Scrubbed expected value
    string Actual,    // Scrubbed actual value
    double Similarity, // Computed Jaccard similarity score
    bool Passed       // Whether it met the threshold
);
```

## SnapshotStore

Persist and retrieve snapshots from disk. Implements `ISnapshotStore` for dependency injection.

```csharp
var store = new SnapshotStore("./snapshots");

// Save a snapshot (async, supports CancellationToken)
var response = new { query = "What is 2+2?", answer = "4" };
await store.SaveAsync("math-test", response);

// Save with a suffix for variants
await store.SaveAsync("math-test", response, "v2");

// Load a snapshot (async)
var baseline = await store.LoadAsync<JsonElement>("math-test");

// Load with suffix
var baselineV2 = await store.LoadAsync<JsonElement>("math-test", "v2");

// Check if snapshot exists
if (store.Exists("math-test"))
{
    var saved = await store.LoadAsync<JsonElement>("math-test");
    var result = comparer.Compare(saved.GetRawText(), JsonSerializer.Serialize(newResponse));
}

// Delete a snapshot
bool deleted = store.Delete("math-test");

// List all snapshots
IReadOnlyList<string> snapshots = store.ListSnapshots();

// Get snapshot count
int count = store.Count;

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

## Dependency Injection

Register snapshot services via `AddAgentEval()`:

```csharp
services.AddAgentEval(options =>
{
    options.DefaultModelId = "gpt-4o";
});

// ISnapshotComparer is registered as a singleton
// Inject it where needed:
public class MyService(ISnapshotComparer comparer)
{
    public void Check(string expected, string actual)
    {
        var result = comparer.Compare(expected, actual);
    }
}
```

> **Note:** `ISnapshotStore` requires a `basePath` constructor parameter and is typically instantiated directly or via a factory, not through DI.

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
    
    // Run evaluation via harness
    var harness = new MAFEvaluationHarness();
    var testCase = new TestCase
    {
        Name = "Capital Query",
        Input = "What is the capital of France?"
    };
    var evalResult = await harness.RunEvaluationAsync(adapter, testCase);
    var responseJson = JsonSerializer.Serialize(new { response = evalResult.ActualOutput });
    
    if (!store.Exists("capital-france"))
    {
        // First run - save the snapshot
        await store.SaveAsync("capital-france", new { response = evalResult.ActualOutput });
        Assert.True(true, "Snapshot created");
        return;
    }
    
    var baseline = await store.LoadAsync<JsonElement>("capital-france");
    var result = comparer.Compare(baseline.GetRawText(), responseJson);
    
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
    var harness = new MAFEvaluationHarness();
    var testCase = new TestCase { Name = "Test", Input = "..." };
    var evalResult = await harness.RunEvaluationAsync(adapter, testCase);
    
    if (updateSnapshots)
    {
        await store.SaveAsync("my-test", new { response = evalResult.ActualOutput });
        Assert.True(true, "Snapshot updated");
        return;
    }
    
    // Normal comparison
    var baseline = await store.LoadAsync<JsonElement>("my-test");
    var responseJson = JsonSerializer.Serialize(new { response = evalResult.ActualOutput });
    var result = new SnapshotComparer().Compare(baseline.GetRawText(), responseJson);
    Assert.True(result.IsMatch);
}
```

Run with:
- **PowerShell:** `$env:UPDATE_SNAPSHOTS="true"; dotnet test`
- **Bash/macOS:** `UPDATE_SNAPSHOTS=true dotnet test`
- **CI/CD:** Set `UPDATE_SNAPSHOTS` as a pipeline variable

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

The built-in semantic comparison uses **Jaccard similarity on word sets**, which works well for:
- Different word order in the same content
- Minor wording changes
- Overlapping vocabulary

> **Limitation:** Jaccard similarity measures word overlap, not semantic meaning. It will not match genuinely rephrased content with different vocabulary (e.g., "happy" vs "joyful"). For true semantic matching, consider wiring an `IEmbeddingGenerator` in a future release.

## Integration with Verify.Xunit (Planned)

> **Status: Planned** — This integration is not yet implemented. The section below shows the intended API.

AgentEval plans to support the popular [Verify](https://github.com/VerifyTests/Verify) library for more advanced snapshot evaluation:

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

1. **Ignore volatile fields** — Always ignore timestamps, request IDs, and other dynamic data
2. **Scrub secrets** — Use patterns to replace API keys, tokens, and sensitive data
3. **Use semantic matching for natural language** — Exact matching is too brittle for LLM outputs
4. **Version your snapshots** — Commit snapshot files to source control
5. **Review snapshot updates** — Don't blindly update; verify changes are intentional
6. **Organize by feature** — Use descriptive names and folder structure
7. **Set appropriate thresholds** — The default similarity threshold is 0.85; adjust based on your needs

## Common Patterns

### Scrubbing Dynamic Data

```csharp
using System.Text.RegularExpressions;

var options = new SnapshotOptions
{
    ScrubPatterns = new List<(Regex Pattern, string Replacement)>
    {
        // ISO timestamps
        (new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", RegexOptions.Compiled), "[TIMESTAMP]"),
        
        // GUIDs (with word boundaries)
        (new Regex(@"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b", RegexOptions.Compiled), "[GUID]"),
        
        // Email addresses
        (new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled), "[EMAIL]"),
        
        // Phone numbers
        (new Regex(@"\+?\d{1,3}[-.\s]?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}", RegexOptions.Compiled), "[PHONE]"),
        
        // IP addresses
        (new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", RegexOptions.Compiled), "[IP]")
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
    var harness = new MAFEvaluationHarness();
    var testCase = new TestCase
    {
        Name = $"Format-{format}",
        Input = $"Give a summary. Format: {format}"
    };
    var evalResult = await harness.RunEvaluationAsync(adapter, testCase);
    
    var snapshotName = $"format-{format}";
    // ... compare with format-specific snapshot
}
```

## See Also

- [Conversations](conversations.md) — Multi-turn conversation evaluation
- [Evaluation Guide](evaluation-guide.md) — Strategies including snapshot regression testing
- [Sample 07](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample07_SnapshotTesting.cs) — Runnable snapshot testing example

---

*Last updated: 2026-02-24*
