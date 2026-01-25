# Implementing Embeddings

> **Technical guide for implementing `IAgentEvalEmbeddings` and using embedding utilities**

> **Looking for when to use embedding metrics?** See [RAG Metrics Guide](rag-metrics.md)

---

## Overview

AgentEval includes embedding-based metrics that compute semantic similarity using vector embeddings. These metrics are significantly faster and cheaper than LLM-based evaluation, making them ideal for:

- High-volume test suites
- Continuous integration pipelines
- Quick feedback during development
- Baseline comparison before more expensive LLM evaluation

---

## Available Metrics

| Metric | Compares | Use Case |
|--------|----------|----------|
| `AnswerSimilarityMetric` | Response ↔ Ground Truth | Is the answer semantically correct? |
| `ResponseContextSimilarityMetric` | Response ↔ Context | Is the response grounded in context? |
| `QueryContextSimilarityMetric` | Query ↔ Context | Is the retrieved context relevant? |

---

## How It Works

All embedding metrics follow the same pattern:

1. **Generate embeddings** for two pieces of text
2. **Compute cosine similarity** between the vectors (range: 0-1)
3. **Convert to 0-100 score** using `ScoreNormalizer.FromSimilarity()`
4. **Compare against threshold** to determine pass/fail

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────┐
│   Text A    │───▶│  Embedding  │───▶│   Cosine    │───▶│  Score  │
│   Text B    │───▶│  Generator  │───▶│ Similarity  │───▶│  0-100  │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────┘
```

---

## AnswerSimilarityMetric

Measures semantic similarity between the agent's response and the expected ground truth answer.

### When to Use

- You have ground truth answers for your test cases
- Speed and cost are priorities
- Semantic meaning matters more than exact wording

### Limitations

- Cannot detect subtle factual errors (e.g., wrong numbers)
- May miss negation differences ("is" vs "is not")
- Requires ground truth to be available

### Example

```csharp
using AgentEval.Embeddings;
using AgentEval.Metrics.RAG;
using AgentEval.Core;

// Create embedding generator (implement IAgentEvalEmbeddings)
var embeddings = new OpenAIEmbeddings("text-embedding-3-small");

// Create metric with default threshold (70)
var metric = new AnswerSimilarityMetric(embeddings);

// Or with custom threshold
var strictMetric = new AnswerSimilarityMetric(embeddings, passingThreshold: 85);

// Evaluate
var context = new EvaluationContext
{
    Input = "What is the capital of France?",
    Output = "Paris is the capital of France.",
    GroundTruth = "The capital of France is Paris."
};

var result = await metric.EvaluateAsync(context);

Console.WriteLine($"Score: {result.Score}");           // e.g., 92.5
Console.WriteLine($"Passed: {result.Passed}");         // true
Console.WriteLine($"Similarity: {result.Details["cosineSimilarity"]}");  // e.g., 0.925
```

### Result Details

| Property | Description |
|----------|-------------|
| `cosineSimilarity` | Raw cosine similarity (0-1) |
| `interpretation` | Human-readable interpretation ("Excellent", "Good", etc.) |
| `groundTruthLength` | Character length of ground truth |
| `outputLength` | Character length of response |

---

## ResponseContextSimilarityMetric

Measures how semantically similar the agent's response is to the retrieved context. This is a fast grounding check.

### When to Use

- Checking if RAG responses use the provided context
- Quick grounding validation without LLM calls
- Detecting when responses hallucinate beyond context

### Default Threshold

Uses a lower default threshold (50) because responses may legitimately extend beyond the context with reasoning and formatting.

### Example

```csharp
var metric = new ResponseContextSimilarityMetric(embeddings);

var context = new EvaluationContext
{
    Input = "What products does Contoso sell?",
    Context = "Contoso Corporation is a leading manufacturer of widgets and gadgets. They offer three product lines: basic widgets, premium gadgets, and enterprise solutions.",
    Output = "Contoso sells widgets and gadgets across three product lines."
};

var result = await metric.EvaluateAsync(context);

if (result.Passed)
{
    Console.WriteLine("Response is grounded in context");
}
else
{
    Console.WriteLine($"Warning: Response may contain hallucinations. Similarity: {result.Details["cosineSimilarity"]:P1}");
}
```

---

## QueryContextSimilarityMetric

Measures how relevant the retrieved context is to the user's query. Useful for evaluating retrieval quality without running the full RAG pipeline.

### When to Use

- Evaluating retriever quality
- Debugging poor RAG responses (is the context even relevant?)
- Retrieval system benchmarks

### Example

```csharp
var metric = new QueryContextSimilarityMetric(embeddings);

var context = new EvaluationContext
{
    Input = "How do I reset my password?",
    Context = "To reset your password, navigate to Settings > Security > Password Reset. Click 'Reset Password' and follow the instructions sent to your email.",
    Output = "" // Not needed for this metric
};

var result = await metric.EvaluateAsync(context);

Console.WriteLine($"Context relevance: {result.Score:F1}%");
```

---

## EmbeddingSimilarity Utilities

The `EmbeddingSimilarity` static class provides low-level utilities for working with embeddings:

```csharp
using AgentEval.Embeddings;

// Get embeddings from your provider
var embedding1 = await embeddings.GetEmbeddingAsync("Hello world");
var embedding2 = await embeddings.GetEmbeddingAsync("Hi there");

// Cosine similarity (most common)
float similarity = EmbeddingSimilarity.CosineSimilarity(
    embedding1.Span, 
    embedding2.Span);
// Range: [-1, 1], typically [0, 1] for text

// Euclidean distance
float distance = EmbeddingSimilarity.EuclideanDistance(
    embedding1.Span, 
    embedding2.Span);
// Range: [0, ∞), 0 = identical

// Dot product
float dotProduct = EmbeddingSimilarity.DotProduct(
    embedding1.Span, 
    embedding2.Span);
// Unnormalized similarity measure
```

### Batch Operations

```csharp
// Compare query against multiple candidates
var candidates = new List<ReadOnlyMemory<float>> 
{ 
    doc1Embedding, 
    doc2Embedding, 
    doc3Embedding 
};

float[] similarities = EmbeddingSimilarity.ComputeSimilarities(
    queryEmbedding, 
    candidates);

// Find top-k most similar items
var documents = new[]
{
    (doc1, doc1Embedding),
    (doc2, doc2Embedding),
    (doc3, doc3Embedding)
};

var topResults = EmbeddingSimilarity.TopK(queryEmbedding, documents, topK: 2);

foreach (var (doc, similarity) in topResults)
{
    Console.WriteLine($"{doc.Title}: {similarity:P1}");
}
```

---

## Implementing IAgentEvalEmbeddings

To use embedding metrics, implement the `IAgentEvalEmbeddings` interface:

```csharp
public interface IAgentEvalEmbeddings
{
    /// <summary>
    /// Get embeddings for a single text.
    /// </summary>
    Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get embeddings for multiple texts (batch).
    /// </summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, 
        CancellationToken cancellationToken = default);
}
```

### OpenAI Example

```csharp
public class OpenAIEmbeddings : IAgentEvalEmbeddings
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    
    public OpenAIEmbeddings(IChatClient chatClient, string model = "text-embedding-3-small")
    {
        _generator = chatClient.AsEmbeddingGenerator(model);
    }
    
    public async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text, 
        CancellationToken ct = default)
    {
        var result = await _generator.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return result.Vector;
    }
    
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, 
        CancellationToken ct = default)
    {
        var results = await _generator.GenerateEmbeddingsAsync(texts.ToList(), cancellationToken: ct);
        return results.Select(r => r.Vector).ToList();
    }
}
```

### Azure AI Example

```csharp
public class AzureEmbeddings : IAgentEvalEmbeddings
{
    private readonly EmbeddingsClient _client;
    
    public AzureEmbeddings(string endpoint, string key, string deploymentName)
    {
        var credential = new AzureKeyCredential(key);
        _client = new EmbeddingsClient(new Uri(endpoint), credential, deploymentName);
    }
    
    public async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text, 
        CancellationToken ct = default)
    {
        var response = await _client.EmbedAsync(text, ct);
        return response.Value.Data[0].Embedding.ToArray();
    }
    
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, 
        CancellationToken ct = default)
    {
        var response = await _client.EmbedAsync(texts.ToList(), ct);
        return response.Value.Data.Select(d => (ReadOnlyMemory<float>)d.Embedding.ToArray()).ToList();
    }
}
```

---

## Score Interpretation

Embedding similarity scores are normalized to 0-100 scale:

| Score Range | Interpretation | Typical Meaning |
|-------------|----------------|-----------------|
| 90-100 | Excellent | Nearly identical meaning |
| 80-89 | Good | Very similar meaning |
| 70-79 | Acceptable | Related meaning |
| 60-69 | Marginal | Somewhat related |
| 50-59 | Poor | Weakly related |
| 0-49 | Fail | Different topics |

The `ScoreNormalizer` class handles this conversion:

```csharp
// Convert cosine similarity (0-1) to score (0-100)
double score = ScoreNormalizer.FromSimilarity(0.85);  // Returns 85.0

// Get human-readable interpretation
string interpretation = ScoreNormalizer.Interpret(85);  // Returns "Good"
```

---

## Performance Considerations

### Speed

Embedding metrics are **10-100x faster** than LLM-based metrics:

| Metric Type | Typical Latency | Cost per Eval |
|-------------|-----------------|---------------|
| LLM-based (GPT-4) | 1-5 seconds | $0.01-0.05 |
| Embedding-based | 50-200ms | $0.0001 |

### Batching

For best performance, batch embedding requests:

```csharp
// Bad: Sequential calls
foreach (var testCase in testCases)
{
    var embedding = await embeddings.GetEmbeddingAsync(testCase.Output);
}

// Good: Batch call
var outputs = testCases.Select(tc => tc.Output);
var allEmbeddings = await embeddings.GetEmbeddingsAsync(outputs);
```

### Caching

Consider caching embeddings for static content:

```csharp
public class CachingEmbeddings : IAgentEvalEmbeddings
{
    private readonly IAgentEvalEmbeddings _inner;
    private readonly Dictionary<string, ReadOnlyMemory<float>> _cache = new();
    
    public async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        if (_cache.TryGetValue(text, out var cached))
            return cached;
        
        var embedding = await _inner.GetEmbeddingAsync(text, ct);
        _cache[text] = embedding;
        return embedding;
    }
}
```

---

## Combining with LLM Metrics

Embedding metrics work well as a first-pass filter before expensive LLM evaluation:

```csharp
// Quick embedding check first
var similarityMetric = new AnswerSimilarityMetric(embeddings, passingThreshold: 60);
var quickResult = await similarityMetric.EvaluateAsync(context);

if (!quickResult.Passed)
{
    // Very low similarity - likely wrong, skip expensive eval
    return TestResult.Fail(quickResult.Explanation);
}

// Passes quick check - run full LLM evaluation
var faithfulnessMetric = new FaithfulnessMetric(chatClient);
var fullResult = await faithfulnessMetric.EvaluateAsync(context);
```

---

## See Also

- [Architecture Overview](architecture.md) - Understanding the metric hierarchy
- [Extensibility Guide](extensibility.md) - Creating custom metrics
- [Benchmarks Guide](benchmarks.md) - Performance testing
