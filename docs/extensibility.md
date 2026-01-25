# Extensibility Guide

> **Extending AgentEval with custom metrics, plugins, and integrations**

---

## Overview

AgentEval is designed for extensibility. You can:

- Create custom metrics for domain-specific evaluation
- Wrap external evaluation frameworks
- Build plugins for custom test workflows
- Integrate with Microsoft's official evaluators

---

## Creating Custom Metrics

### Basic Metric

Implement the `IMetric` interface for general-purpose metrics:

```csharp
using AgentEval.Core;

public class ResponseLengthMetric : IMetric
{
    private readonly int _minLength;
    private readonly int _maxLength;
    
    public string Name => "ResponseLength";
    public string Description => "Validates response length is within expected range.";
    
    public ResponseLengthMetric(int minLength = 50, int maxLength = 500)
    {
        _minLength = minLength;
        _maxLength = maxLength;
    }
    
    public Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken cancellationToken = default)
    {
        var length = context.Output?.Length ?? 0;
        
        if (length < _minLength)
        {
            return Task.FromResult(MetricResult.Fail(
                Name, 
                $"Response too short: {length} < {_minLength}",
                score: (double)length / _minLength * 50));
        }
        
        if (length > _maxLength)
        {
            return Task.FromResult(MetricResult.Fail(
                Name, 
                $"Response too long: {length} > {_maxLength}",
                score: Math.Max(0, 100 - (length - _maxLength) / 10)));
        }
        
        // Within range - full score
        var score = 100.0;
        return Task.FromResult(MetricResult.Pass(
            Name, 
            score, 
            $"Response length {length} is within range [{_minLength}, {_maxLength}]",
            new Dictionary<string, object>
            {
                ["length"] = length,
                ["minLength"] = _minLength,
                ["maxLength"] = _maxLength
            }));
    }
}
```

### RAG Metric

Implement `IRAGMetric` for metrics that need context or ground truth:

```csharp
public class KeywordCoverageMetric : IRAGMetric
{
    private readonly IReadOnlyList<string> _requiredKeywords;
    
    public string Name => "KeywordCoverage";
    public string Description => "Checks if response contains required keywords from context.";
    
    // RAG-specific requirements
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => false;
    
    public KeywordCoverageMetric(IEnumerable<string> requiredKeywords)
    {
        _requiredKeywords = requiredKeywords.ToList();
    }
    
    public Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.Context))
        {
            return Task.FromResult(MetricResult.Fail(Name, "Context is required for keyword coverage."));
        }
        
        var outputLower = context.Output?.ToLowerInvariant() ?? "";
        var foundKeywords = _requiredKeywords
            .Where(kw => outputLower.Contains(kw.ToLowerInvariant()))
            .ToList();
        
        var coverage = _requiredKeywords.Count > 0 
            ? (double)foundKeywords.Count / _requiredKeywords.Count * 100 
            : 100;
        
        var missingKeywords = _requiredKeywords.Except(foundKeywords).ToList();
        
        if (missingKeywords.Any())
        {
            return Task.FromResult(MetricResult.Fail(
                Name,
                $"Missing keywords: {string.Join(", ", missingKeywords)}",
                coverage,
                new Dictionary<string, object>
                {
                    ["foundKeywords"] = foundKeywords,
                    ["missingKeywords"] = missingKeywords,
                    ["coverage"] = coverage / 100
                }));
        }
        
        return Task.FromResult(MetricResult.Pass(
            Name, 
            coverage,
            $"All {_requiredKeywords.Count} required keywords found."));
    }
}
```

### Agentic Metric

Implement `IAgenticMetric` for metrics that evaluate tool usage:

```csharp
public class ToolLatencyMetric : IAgenticMetric
{
    private readonly TimeSpan _maxLatency;
    
    public string Name => "ToolLatency";
    public string Description => "Validates all tool calls complete within time limit.";
    public bool RequiresToolUsage => true;
    
    public ToolLatencyMetric(TimeSpan maxLatency)
    {
        _maxLatency = maxLatency;
    }
    
    public Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken cancellationToken = default)
    {
        if (context.ToolUsage == null || context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No tool calls to evaluate."));
        }
        
        var slowTools = context.ToolUsage.Calls
            .Where(c => c.Duration > _maxLatency)
            .ToList();
        
        if (slowTools.Any())
        {
            var slowestTool = slowTools.OrderByDescending(t => t.Duration).First();
            return Task.FromResult(MetricResult.Fail(
                Name,
                $"Tool '{slowestTool.Name}' took {slowestTool.Duration.TotalMilliseconds:F0}ms (max: {_maxLatency.TotalMilliseconds}ms)",
                score: Math.Max(0, 100 - slowTools.Count * 20),
                details: new Dictionary<string, object>
                {
                    ["slowTools"] = slowTools.Select(t => t.Name).ToList(),
                    ["maxLatencyMs"] = _maxLatency.TotalMilliseconds
                }));
        }
        
        return Task.FromResult(MetricResult.Pass(
            Name,
            100,
            $"All {context.ToolUsage.Count} tool calls completed within {_maxLatency.TotalMilliseconds}ms."));
    }
}
```

---

## Using the Metric Registry

The `MetricRegistry` provides centralized metric management:

```csharp
using AgentEval.Core;

// Create registry
var registry = new MetricRegistry();

// Register metrics by name
registry.Register("length", new ResponseLengthMetric(50, 500));
registry.Register("keywords", new KeywordCoverageMetric(new[] { "price", "shipping" }));

// Or let the metric use its own name
registry.Register(new ToolLatencyMetric(TimeSpan.FromSeconds(5)));

// Get a specific metric
var lengthMetric = registry.Get("length");
var toolLatency = registry.GetRequired("ToolLatency"); // Throws if not found

// Run all metrics
var results = new List<MetricResult>();
foreach (var metric in registry.GetAll())
{
    var result = await metric.EvaluateAsync(context);
    results.Add(result);
}

// Filter by type
var ragMetrics = registry.GetAll().OfType<IRAGMetric>();
var agenticMetrics = registry.GetAll().OfType<IAgenticMetric>();
```

### Pre-populating the Registry

```csharp
// Initialize with multiple metrics
var registry = new MetricRegistry(new IMetric[]
{
    new FaithfulnessMetric(chatClient),
    new RelevanceMetric(chatClient),
    new ToolSelectionMetric(expectedTools),
    new ResponseLengthMetric(100, 1000)
});
```

---

## Wrapping Microsoft Evaluators

AgentEval includes `MicrosoftEvaluatorAdapter` to use official Microsoft evaluators:

```csharp
using AgentEval.Adapters;
using Microsoft.Extensions.AI.Evaluation.Quality;

// Create Microsoft evaluator
var msCoherence = new CoherenceEvaluator();

// Wrap for AgentEval
var coherenceMetric = new MicrosoftEvaluatorAdapter(msCoherence, chatClient);

// Use like any AgentEval metric
var result = await coherenceMetric.EvaluateAsync(context);

// Score is automatically normalized from 1-5 to 0-100
Console.WriteLine($"Coherence: {result.Score}"); // e.g., 85.0
```

### Available Microsoft Evaluators

| Microsoft Evaluator | AgentEval Equivalent | Score Range |
|---------------------|---------------------|-------------|
| `FluencyEvaluator` | Wrap with adapter | 1-5 → 0-100 |
| `CoherenceEvaluator` | Wrap with adapter | 1-5 → 0-100 |
| `RelevanceEvaluator` | `RelevanceMetric` (native) | 0-100 |
| `GroundednessEvaluator` | `FaithfulnessMetric` (native) | 0-100 |
| `EquivalenceEvaluator` | `AnswerSimilarityMetric` (native) | 0-100 |
| `TaskAdherenceEvaluator` | `TaskCompletionMetric` (native) | 0-100 |
| `ToolCallAccuracyEvaluator` | `ToolSelectionMetric` (native) | 0-100 |

### When to Use the Adapter

- **Use adapter** when you want Microsoft's exact prompts and scoring
- **Use native** when you want AgentEval's tool tracking and detailed breakdowns

---

## Score Normalization

AgentEval uses 0-100 scores. The `ScoreNormalizer` class converts between scales:

```csharp
using AgentEval.Core;

// Convert Microsoft's 1-5 scale to AgentEval's 0-100
double score = ScoreNormalizer.From1To5(4.2);  // Returns 80.0

// Formula: (score - 1) * 25
// 1 → 0, 2 → 25, 3 → 50, 4 → 75, 5 → 100

// Convert cosine similarity (0-1) to 0-100
double score2 = ScoreNormalizer.FromSimilarity(0.85);  // Returns 85.0

// Get human-readable interpretation
string interpretation = ScoreNormalizer.Interpret(85);
// Returns: "Good" (for 80-89 range)

// Interpretation ranges:
// 90-100: "Excellent"
// 80-89:  "Good"
// 70-79:  "Acceptable"
// 60-69:  "Marginal"
// 50-59:  "Poor"
// 0-49:   "Fail"
```

### Custom Score Ranges

```csharp
// Convert from any range to 0-100
public static double NormalizeScore(double value, double min, double max)
{
    if (max <= min) throw new ArgumentException("max must be greater than min");
    return (value - min) / (max - min) * 100;
}

// Example: Convert 0-10 scale
var score = NormalizeScore(7.5, 0, 10);  // Returns 75.0
```

---

## Creating Plugins

Plugins extend the evaluation harness with custom behavior:

```csharp
public interface IAgentEvalPlugin
{
    string Name { get; }
    
    /// <summary>Called before each test runs.</summary>
    Task OnTestStartAsync(TestCase testCase, CancellationToken ct);
    
    /// <summary>Called after each test completes.</summary>
    Task OnTestCompleteAsync(TestResult result, CancellationToken ct);
    
    /// <summary>Called after the entire test suite completes.</summary>
    Task OnSuiteCompleteAsync(TestSummary summary, CancellationToken ct);
}
```

### Example: Logging Plugin

```csharp
public class ConsoleLoggingPlugin : IAgentEvalPlugin
{
    public string Name => "ConsoleLogging";
    
    public Task OnTestStartAsync(TestCase testCase, CancellationToken ct)
    {
        Console.WriteLine($"▶ Starting: {testCase.Name}");
        return Task.CompletedTask;
    }
    
    public Task OnTestCompleteAsync(TestResult result, CancellationToken ct)
    {
        var icon = result.Passed ? "✅" : "❌";
        Console.WriteLine($"{icon} {result.Name}: {result.Score:F1}/100");
        return Task.CompletedTask;
    }
    
    public Task OnSuiteCompleteAsync(TestSummary summary, CancellationToken ct)
    {
        Console.WriteLine($"\n📊 Summary: {summary.PassedCount}/{summary.TotalCount} passed");
        return Task.CompletedTask;
    }
}
```

### Example: Metrics Export Plugin

```csharp
public class PrometheusMetricsPlugin : IAgentEvalPlugin
{
    private readonly Counter _testsPassed;
    private readonly Counter _testsFailed;
    private readonly Histogram _testDuration;
    
    public string Name => "PrometheusMetrics";
    
    public Task OnTestCompleteAsync(TestResult result, CancellationToken ct)
    {
        if (result.Passed)
            _testsPassed.Inc();
        else
            _testsFailed.Inc();
        
        _testDuration.Observe(result.Duration.TotalSeconds);
        return Task.CompletedTask;
    }
}
```

---

## Retry Policies

Use `RetryPolicy` for resilient metric evaluation:

```csharp
using AgentEval.Core;

// Create retry policy
var retryPolicy = new RetryPolicy(
    maxRetries: 3,
    initialDelayMs: 1000,
    backoffMultiplier: 2.0);  // 1s, 2s, 4s

// Apply to a metric
var result = await retryPolicy.ExecuteAsync(
    async ct => await metric.EvaluateAsync(context, ct),
    cancellationToken);

// Or use extension method
var result = await metric.WithRetry(retryPolicy).EvaluateAsync(context);
```

### When to Use Retry

- LLM API calls (rate limits, transient failures)
- Embedding generation
- External service calls in custom metrics

---

## LLM-Based Custom Metrics

Build metrics that use LLM-as-judge:

```csharp
public class ToneMetric : IMetric
{
    private readonly IChatClient _chatClient;
    private readonly string _expectedTone;
    
    public string Name => "Tone";
    public string Description => $"Evaluates if response matches expected {_expectedTone} tone.";
    
    public ToneMetric(IChatClient chatClient, string expectedTone = "professional")
    {
        _chatClient = chatClient;
        _expectedTone = expectedTone;
    }
    
    public async Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken ct = default)
    {
        var prompt = $@"Evaluate if this response has a {_expectedTone} tone.

Response to evaluate:
{context.Output}

Respond with JSON:
{{
    ""score"": <1-5 where 5 is perfect {_expectedTone} tone>,
    ""reasoning"": ""<brief explanation>""
}}";

        var response = await _chatClient.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, prompt) },
            cancellationToken: ct);
        
        var parsed = LlmJsonParser.ParseMetricResponse<ToneResponse>(response.Message.Text);
        var score = ScoreNormalizer.From1To5(parsed?.Score ?? 3);
        
        return new MetricResult
        {
            MetricName = Name,
            Score = score,
            Passed = score >= 70,
            Explanation = parsed?.Reasoning ?? "Unable to parse response"
        };
    }
    
    private record ToneResponse(int Score, string Reasoning);
}
```

---

## Testing Custom Metrics

Use `FakeChatClient` for unit testing:

```csharp
using AgentEval.Testing;
using Xunit;

public class ToneMetricTests
{
    [Fact]
    public async Task Professional_Tone_Scores_High()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient()
            .WithResponse(@"{""score"": 5, ""reasoning"": ""Very professional""}");
        
        var metric = new ToneMetric(fakeChatClient, "professional");
        
        var context = new EvaluationContext
        {
            Input = "How do I reset my password?",
            Output = "I'd be happy to help you reset your password. Please follow these steps..."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.True(result.Passed);
        Assert.Equal(100, result.Score);
        Assert.Contains("professional", fakeChatClient.LastPrompt);
    }
    
    [Fact]
    public async Task Unprofessional_Tone_Scores_Low()
    {
        var fakeChatClient = new FakeChatClient()
            .WithResponse(@"{""score"": 1, ""reasoning"": ""Too casual""}");
        
        var metric = new ToneMetric(fakeChatClient, "professional");
        
        var context = new EvaluationContext
        {
            Input = "How do I reset my password?",
            Output = "lol just click the button dude"
        };
        
        var result = await metric.EvaluateAsync(context);
        
        Assert.False(result.Passed);
        Assert.Equal(0, result.Score);
    }
}
```

---

## Best Practices

### 1. Use Appropriate Interface

```csharp
// ✅ Good: RAG metric declares its requirements
public class MyRagMetric : IRAGMetric
{
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => true;
}

// ❌ Bad: Generic metric that secretly needs context
public class MyMetric : IMetric
{
    public Task<MetricResult> EvaluateAsync(EvaluationContext ctx, CancellationToken ct)
    {
        // Crashes if ctx.Context is null!
        var words = ctx.Context.Split(' ');
    }
}
```

### 2. Handle Missing Data Gracefully

```csharp
public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
{
    // ✅ Good: Check and fail gracefully
    if (string.IsNullOrEmpty(context.GroundTruth))
    {
        return Task.FromResult(MetricResult.Fail(Name, "Ground truth is required."));
    }
    
    // Continue with evaluation...
}
```

### 3. Include Detailed Metadata

```csharp
return MetricResult.Pass(Name, score, explanation, new Dictionary<string, object>
{
    // ✅ Good: Rich details for debugging
    ["rawSimilarity"] = similarity,
    ["threshold"] = _threshold,
    ["comparisonType"] = "cosine",
    ["text1Length"] = text1.Length,
    ["text2Length"] = text2.Length
});
```

### 4. Use Constants for Magic Numbers

```csharp
// ✅ Good: Use EvaluationDefaults
public MyMetric(double threshold = EvaluationDefaults.PassingScoreThreshold)

// ❌ Bad: Magic numbers
public MyMetric(double threshold = 70)
```

---

## See Also

- [Architecture Overview](architecture.md) - Understanding the metric hierarchy
- [Embedding Metrics](embedding-metrics.md) - Fast similarity evaluation
- [Benchmarks Guide](benchmarks.md) - Performance testing
