# Implementing Embeddings

> **Technical guide for implementing `IAgentEvalEmbeddings` and using embedding utilities**

> **Looking for when to use embedding metrics?** See [RAG Metrics Guide](rag-metrics.md)

> **Want to see embedding metrics in action?** Check out [Sample B1: Comprehensive RAG](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/MetricsAndQuality/01_ComprehensiveRAG.cs) for semantic similarity evaluation examples.

---

## Overview

AgentEval includes embedding-based metrics that compute semantic similarity using vector embeddings. These metrics are significantly faster and cheaper than LLM-based evaluation, making them ideal for:

- High-volume evaluations
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

## CLI and shared provider settings

The three metrics are available through `eval --metrics` for single-run evaluations:

```powershell
$env:AI_INFERENCE_PROVIDER = "openai-compatible"
$env:OPENAI_COMPATIBLE_ENDPOINT = "http://localhost:11434/v1"
$env:OPENAI_COMPATIBLE_MODEL = "your-chat-model"
$env:OPENAI_COMPATIBLE_EMBEDDING_MODEL = "your-embedding-model"
agenteval eval --dataset cases.json --endpoint http://localhost:11434/v1 --model your-chat-model `
  --metrics embed_answer_similarity,embed_response_context,embed_query_context `
  --format json --output report.json --aef .agenteval
```

The embedding model must be served by that endpoint. Configure the provider's API key where required.
`embed_answer_similarity` needs the case's expected output; both context metrics need its context.
Missing inputs or failed embedding requests remain unmeasured and make an otherwise successful run
exit `11`. Missing provider/model configuration exits `3` before subject or metric requests.

Embedding selection follows `AI_INFERENCE_PROVIDER` and that provider's environment endpoint/key.
An explicit subject `--endpoint` or `--sut`, or an LLM `--judge`, does not select the embedding provider.
Code, LLM and embedding metrics can be selected together. With `--runs > 1`, the existing CLI policy
ignores all named metrics with a warning and does not resolve unused embedding configuration.

| Selected provider | Embedding model variable | Default |
|---|---|---|
| `azure` | `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | Required deployment name |
| `foundry` | `FOUNDRY_EMBEDDING_MODEL` | Required deployment name |
| `bitdeer` | `BITDEER_EMBEDDING_MODEL` | `BAAI/bge-m3` on the canonical endpoint |
| `openai` | `OPENAI_EMBEDDING_MODEL` | `text-embedding-3-small` on the canonical endpoint |
| `openai-compatible` | `OPENAI_COMPATIBLE_EMBEDDING_MODEL` | Required model name |

An overridden Bitdeer/OpenAI endpoint also requires an explicit embedding model. Scores depend on
the chosen model. CLI JSON report metadata includes `EmbeddingModel` and `EmbeddingProvider`;
AEF run configuration and suite definition include `embeddingModel` and `embeddingProvider`, so
changing this instrument changes the definition digest. These are configured identities, not a
provider-reported backend fingerprint. Embedding calls use AEF usage role `other`, attributed to
the case. Only SDK-reported token fields are retained: input-only usage leaves output and cost
unknown, and failed calls retain unknown usage. No completion-token zero or billed cost is inferred.

Hosts can reuse `AgentEval.Providers.EmbeddingProviderEnvironment` and `EmbeddingProviderSettings`
from Core to resolve the same policy, then construct their own SDK generator. Core has no provider
SDK dependency. Wrap a Microsoft.Extensions.AI generator with `MEAIEmbeddingAdapter` when an
`IAgentEvalEmbeddings` implementation is needed. See [eval configuration](cli.md#agenteval-eval) for the
CLI subject options.

---

## How It Works

All embedding metrics follow the same pattern:

1. **Generate embeddings** for two pieces of text
2. **Compute cosine similarity** between the vectors (range: -1 to 1 for valid vectors)
3. **Convert to 0-100 score** using `ScoreNormalizer.FromSimilarity()` (clamp cosine to 0–1, then multiply by 100)
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

> **📖 Complete Example:** See [Sample B1: Comprehensive RAG](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/MetricsAndQuality/01_ComprehensiveRAG.cs) for an implementation showing all three embedding metrics alongside LLM evaluation.

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

### DI Registration

`IAgentEvalEmbeddings` is automatically registered in DI when you call `services.AddAgentEval()`, provided you register an `IEmbeddingGenerator<string, Embedding<float>>` first. It wraps the generator with `MEAIEmbeddingAdapter`. You can also register your own implementation before `AddAgentEval()` and it will be preserved (TryAdd semantics):

```csharp
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
services.AddAgentEval(); // IAgentEvalEmbeddings now available via DI

// Or register your own:
services.AddSingleton<IAgentEvalEmbeddings>(new MyCustomEmbeddings());
services.AddAgentEval(); // Your registration wins
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

## Choosing a threshold

The defaults are **70 for answer similarity** and **50 for both context similarities**. They are
starting points, not cutoffs calibrated for every model or task. `ScoreNormalizer.FromSimilarity`
rescales cosine; it does not calibrate score distributions across embedding models. A score of 85
is not an 85% probability that an answer is correct.

A wrong answer can share a topic and vocabulary with the reference and still pass a similarity
threshold. For example, "Lyon is the capital of France" and "Paris is the capital of France" are
topically close but factually different. This risk applies when using models such as `BAAI/bge-m3`;
neither the default 70 nor a higher unvalidated cutoff establishes factual correctness. Use a
separate factual check when correctness, numbers or negation matter.

Choose a cutoff using representative labeled acceptable and unacceptable responses for your task,
including wrong same-topic answers and negations. Inspect false accepts and false rejects, select
a tradeoff suitable for the application, then check it on held-out examples. Recheck after changing
the embedding model, provider deployment, preprocessing or dataset. Keep model/provider identity
with scores and compare like instruments; a threshold from another embedding model is not portable
without validation. No universal calibrated cutoff is supplied by this guide.

Library callers set `passingThreshold` in the metric constructor. CLI `eval --metrics` uses the
metric defaults; `--success-threshold` controls stochastic success rate and does not tune embedding
cutoffs. The case's passing-score policy is also separate from each named metric's cutoff.

## Score Interpretation

Embedding similarity scores use the 0–100 scale. `ScoreNormalizer.Interpret` supplies these generic
display labels; they are not validated semantic meanings or per-metric pass/fail thresholds:

| Score Range | Generic display label |
|-------------|-----------------------|
| 90–100 | Excellent |
| 75–<90 | Good |
| 60–<75 | Satisfactory |
| 40–<60 | Needs Improvement |
| 20–<40 | Poor |
| 0–<20 | Very Poor |

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

> The example above sends outputs as one batch. Provider limits and actual latency/cost still apply.

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

Embedding metrics can prioritize review after validating a cutoff for the application. A high
similarity does not replace a factual check. Keep each measurement's availability and result:

```csharp
// The cutoff is illustrative; validate it on your own labeled examples.
var similarityMetric = new AnswerSimilarityMetric(embeddings, passingThreshold: 60);
var quickResult = await similarityMetric.EvaluateAsync(context);

Console.WriteLine(quickResult.Measured
    ? $"Similarity: {quickResult.Score}, passed: {quickResult.Passed}"
    : $"Similarity not measured: {quickResult.Explanation}");

// Separate factual evaluation against the ground truth; do not infer it from similarity.
var correctnessMetric = new AnswerCorrectnessMetric(chatClient);
var fullResult = await correctnessMetric.EvaluateAsync(context);
Console.WriteLine(fullResult.Measured
    ? $"Correctness: {fullResult.Score}, passed: {fullResult.Passed}"
    : $"Correctness not measured: {fullResult.Explanation}");
```

---

## See Also

- [Sample B1: Comprehensive RAG](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/MetricsAndQuality/01_ComprehensiveRAG.cs) - All three embedding metrics alongside LLM evaluation
- [Architecture Overview](architecture.md) - Understanding the metric hierarchy
- [Extensibility Guide](extensibility.md) - Creating custom metrics
- [Benchmarks Guide](benchmarks.md) - Performance evaluation
