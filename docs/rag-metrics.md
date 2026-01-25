````markdown
# RAG Metrics Guide

> **Comprehensive evaluation for Retrieval-Augmented Generation systems**

---

## Overview

AgentEval provides 10 metrics specifically designed for evaluating RAG pipelines, covering the entire retrieval → generation → evaluation cycle.

### Metric Categories

| Category | Metrics | Cost | Best For |
|----------|---------|------|----------|
| **LLM-based** | Faithfulness, Relevance, Context Precision/Recall, Answer Correctness | $$$ | High-accuracy evaluation, production sampling |
| **Embedding-based** | Answer Similarity, Response-Context, Query-Context | $ | Volume testing, fast feedback |
| **Information Retrieval** | Recall@K, MRR | **FREE** | CI/CD, retrieval optimization |

### When to Use Each

```
RAG Pipeline Stage          Recommended Metrics
─────────────────────────────────────────────────────────
                           ┌─────────────────────────────┐
  Query                    │                             │
    │                      │                             │
    ▼                      │                             │
┌───────────┐              │  code_recall_at_k (FREE)    │
│ Retrieval │──────────────│  code_mrr (FREE)            │
└───────────┘              │  embed_query_context ($)    │
    │                      │  llm_context_precision ($$$)│
    │                      │  llm_context_recall ($$$)   │
    ▼                      │                             │
┌───────────┐              │                             │
│ Generation│──────────────│  llm_faithfulness ($$$)     │
└───────────┘              │  embed_response_context ($) │
    │                      │                             │
    ▼                      │                             │
┌───────────┐              │  llm_answer_correctness ($$$│
│  Answer   │──────────────│  embed_answer_similarity ($)│
└───────────┘              │  llm_relevance ($$$)        │
                           └─────────────────────────────┘
```

---

## Information Retrieval Metrics (FREE)

These metrics evaluate retrieval quality using only document IDs - **no API calls required**.

### code_recall_at_k

**Purpose:** Measures what proportion of relevant documents were found in the top K results.

**Formula:** `Recall@K = |Relevant ∩ Retrieved@K| / |Relevant|`

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Cost | **FREE** |
| Default K | 10 |
| Pass Threshold | 70% |

**Example:**
```csharp
var metric = new RecallAtKMetric(k: 5);

var context = new EvaluationContext
{
    RelevantDocumentIds = ["doc1", "doc2", "doc3"],      // Ground truth
    RetrievedDocumentIds = ["doc1", "doc4", "doc2", "doc5", "doc6"]  // Search results
};

var result = await metric.EvaluateAsync(context);
// Score: 67 (2 of 3 relevant docs found in top 5)
```

**AdditionalData:**
- `k` - The K value used
- `relevant_count` - Total relevant documents
- `retrieved_at_k` - Documents retrieved at K
- `relevant_found_at_k` - Relevant documents found in results

---

### code_mrr

**Purpose:** Mean Reciprocal Rank - measures how early the first relevant document appears.

**Formula:** `MRR = 1 / rank_of_first_relevant`

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Cost | **FREE** |
| Default maxRank | Unlimited |
| Pass Threshold | 33% (first relevant in top 3) |

**Scoring:**
| First Relevant Rank | Score |
|---------------------|-------|
| 1 | 100 |
| 2 | 50 |
| 3 | 33 |
| 10 | 10 |
| Not found | 0 |

**Example:**
```csharp
var metric = new MRRMetric(maxRank: 10);

var context = new EvaluationContext
{
    RelevantDocumentIds = ["doc1", "doc2"],
    RetrievedDocumentIds = ["docA", "docB", "doc1", "docC"]  // doc1 at rank 3
};

var result = await metric.EvaluateAsync(context);
// Score: 33 (1/3 = 0.333)
```

**AdditionalData:**
- `first_relevant_rank` - Position of first relevant doc (0 if none)
- `max_rank` - Maximum rank considered
- `reciprocal_rank` - The 1/rank value

---

## Embedding Metrics ($)

Fast semantic similarity using vector embeddings - 10-100x faster than LLM evaluation.

### embed_answer_similarity

**Purpose:** Measures semantic similarity between response and ground truth.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Ground Truth |
| Cost | ~$0.0001/eval |
| Default Threshold | 70 |

**When to Use:**
- You have ground truth answers
- Speed/cost are priorities
- Semantic meaning matters more than exact wording

**Example:**
```csharp
var embeddings = new OpenAIEmbeddings("text-embedding-3-small");
var metric = new AnswerSimilarityMetric(embeddings, passingThreshold: 80);

var context = new EvaluationContext
{
    Output = "Paris is the capital of France.",
    GroundTruth = "The capital of France is Paris."
};

var result = await metric.EvaluateAsync(context);
// Score: ~92 (high semantic similarity)
```

**Limitations:**
- Cannot detect subtle factual errors (wrong numbers)
- May miss negation differences ("is" vs "is not")

---

### embed_response_context

**Purpose:** Measures how grounded the response is in the retrieved context.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Context |
| Cost | ~$0.0001/eval |
| Default Threshold | 50 (lower - responses may extend context) |

**When to Use:**
- Quick grounding check
- Detecting hallucination beyond context
- Pre-filter before expensive LLM evaluation

**Example:**
```csharp
var metric = new ResponseContextSimilarityMetric(embeddings);

var context = new EvaluationContext
{
    Context = "Contoso sells widgets and gadgets in three product lines.",
    Output = "Contoso offers widgets, gadgets, and enterprise solutions."
};

var result = await metric.EvaluateAsync(context);
// High score = response grounded in context
```

---

### embed_query_context

**Purpose:** Measures relevance of retrieved context to the user's query.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Input, Context |
| Cost | ~$0.0001/eval |
| Default Threshold | 70 |

**When to Use:**
- Evaluating retriever quality
- Debugging poor RAG responses
- Retrieval system benchmarks

**Example:**
```csharp
var metric = new QueryContextSimilarityMetric(embeddings);

var context = new EvaluationContext
{
    Input = "How do I reset my password?",
    Context = "To reset your password, navigate to Settings > Security..."
};

var result = await metric.EvaluateAsync(context);
// High score = context is relevant to query
```

---

## LLM-Based Metrics ($$$)

Highest accuracy evaluation using LLM-as-judge patterns.

### llm_faithfulness

**Purpose:** Measures whether the response is grounded in context (no hallucinations).

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Context |
| Cost | ~$0.01-0.05/eval |

**When to Use:**
- Validating RAG systems don't hallucinate
- Production quality sampling
- High-stakes applications

**Example:**
```csharp
var metric = new FaithfulnessMetric(chatClient);

var context = new EvaluationContext
{
    Input = "What is the capital of France?",
    Output = "Paris is the capital of France and has a population of 2.1 million.",
    Context = "France is a country in Europe. Its capital is Paris."
};

var result = await metric.EvaluateAsync(context);
// Score depends on whether "2.1 million" is in context
```

---

### llm_relevance

**Purpose:** Measures how relevant the response is to the user's question.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Input only |
| Cost | ~$0.01-0.05/eval |

**When to Use:**
- General quality checks
- Detecting off-topic responses
- Any LLM output evaluation

---

### llm_context_precision

**Purpose:** Measures how much of the retrieved context is actually useful.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Context |
| Cost | ~$0.01-0.05/eval |

**When to Use:**
- Optimizing retrieval quality
- Reducing context noise
- Improving retrieval efficiency

---

### llm_context_recall

**Purpose:** Measures whether context contains all information needed for the correct answer.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Context, Ground Truth |
| Cost | ~$0.01-0.05/eval |

**When to Use:**
- Evaluating retrieval completeness
- Ensuring no critical information missed
- Testing retrieval coverage

---

### llm_answer_correctness

**Purpose:** Measures factual correctness compared to ground truth.

| Property | Value |
|----------|-------|
| Interface | `IRAGMetric` |
| Requires | Ground Truth |
| Cost | ~$0.01-0.05/eval |

**When to Use:**
- QA system evaluation
- Fact-checking responses
- Regression testing with known answers

---

## Cost Optimization Strategy

### Tiered Evaluation Approach

```csharp
// TIER 1: CI/CD - FREE metrics only
var ciMetrics = new IMetric[]
{
    new RecallAtKMetric(k: 10),
    new MRRMetric()
};

// TIER 2: Development - Add embedding metrics
var devMetrics = ciMetrics.Concat(new IMetric[]
{
    new AnswerSimilarityMetric(embeddings),
    new ResponseContextSimilarityMetric(embeddings)
});

// TIER 3: Production Sampling - Full LLM evaluation
var prodMetrics = devMetrics.Concat(new IMetric[]
{
    new FaithfulnessMetric(chatClient),
    new AnswerCorrectnessMetric(chatClient)
});
```

### Fast-Then-Deep Pattern

```csharp
// Quick embedding check first
var embedScore = await embedMetric.EvaluateAsync(context);

if (embedScore.Score < 60)
{
    // Very low - skip expensive LLM eval
    return TestResult.Fail("Response not grounded in context");
}

if (embedScore.Score < 85)
{
    // Borderline - escalate to LLM
    var llmScore = await faithfulnessMetric.EvaluateAsync(context);
    return llmScore;
}

return embedScore;  // High confidence pass
```

### Sampling for Production

```csharp
var sampleRate = 0.10;  // 10% of traffic

if (Random.Shared.NextDouble() < sampleRate)
{
    // Run expensive metrics on sample only
    await faithfulnessMetric.EvaluateAsync(context);
    await answerCorrectnessMetric.EvaluateAsync(context);
}
```

---

## Complete RAG Evaluation Example

```csharp
using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Embeddings;

// Setup
var embeddings = new OpenAIEmbeddings("text-embedding-3-small");
var chatClient = GetAzureOpenAIChatClient();

// Define all RAG metrics
var metrics = new IMetric[]
{
    // FREE - Information Retrieval
    new RecallAtKMetric(k: 5),
    new MRRMetric(),
    
    // $ - Embedding-based
    new AnswerSimilarityMetric(embeddings),
    new ResponseContextSimilarityMetric(embeddings),
    new QueryContextSimilarityMetric(embeddings),
    
    // $$$ - LLM-based
    new FaithfulnessMetric(chatClient),
    new RelevanceMetric(chatClient),
    new ContextPrecisionMetric(chatClient),
    new ContextRecallMetric(chatClient),
    new AnswerCorrectnessMetric(chatClient)
};

// Prepare evaluation context
var context = new EvaluationContext
{
    Input = "What is AgentEval?",
    Output = "AgentEval is a .NET evaluation toolkit for AI agents.",
    Context = "AgentEval is the comprehensive .NET evaluation toolkit for AI agents, built for Microsoft Agent Framework.",
    GroundTruth = "AgentEval is a .NET toolkit for evaluating AI agents.",
    RelevantDocumentIds = ["doc-agenteval-readme", "doc-agenteval-quickstart"],
    RetrievedDocumentIds = ["doc-agenteval-readme", "doc-other", "doc-agenteval-quickstart"]
};

// Run all metrics
Console.WriteLine("Metric                    Score  Passed");
Console.WriteLine("─────────────────────────────────────────");

foreach (var metric in metrics)
{
    var result = await metric.EvaluateAsync(context);
    var status = result.Passed ? "✅" : "❌";
    Console.WriteLine($"{metric.Name,-25} {result.Score,5:F0}  {status}");
}
```

**Sample Output:**
```
Metric                    Score  Passed
─────────────────────────────────────────
code_recall_at_k            100  ✅
code_mrr                    100  ✅
embed_answer_similarity      94  ✅
embed_response_context       89  ✅
embed_query_context          85  ✅
llm_faithfulness             95  ✅
llm_relevance                90  ✅
llm_context_precision        88  ✅
llm_context_recall           92  ✅
llm_answer_correctness       96  ✅
```

---

## Data Requirements

| Metric | Input | Output | Context | Ground Truth | Doc IDs |
|--------|-------|--------|---------|--------------|---------|
| `code_recall_at_k` | - | - | - | - | ✅ |
| `code_mrr` | - | - | - | - | ✅ |
| `embed_answer_similarity` | - | ✅ | - | ✅ | - |
| `embed_response_context` | - | ✅ | ✅ | - | - |
| `embed_query_context` | ✅ | - | ✅ | - | - |
| `llm_faithfulness` | ✅ | ✅ | ✅ | - | - |
| `llm_relevance` | ✅ | ✅ | - | - | - |
| `llm_context_precision` | ✅ | - | ✅ | - | - |
| `llm_context_recall` | ✅ | - | ✅ | ✅ | - |
| `llm_answer_correctness` | ✅ | ✅ | - | ✅ | - |

---

## See Also

- [Implementing Embeddings](embedding-metrics.md) - Technical guide for `IAgentEvalEmbeddings`
- [Metrics Reference](metrics-reference.md) - Quick reference for all metrics
- [Evaluation Guide](evaluation-guide.md) - Choosing metrics for your use case
- [Naming Conventions](naming-conventions.md) - Metric naming standards

---

*Last updated: January 2026*

````