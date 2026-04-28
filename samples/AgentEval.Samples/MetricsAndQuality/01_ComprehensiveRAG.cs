// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Retrieval;
using AgentEval.Embeddings;

namespace AgentEval.Samples;

/// <summary>
/// Sample B1: Comprehensive RAG - Build and Evaluate a Complete RAG System
/// 
/// This demonstrates:
/// - PART 1: Building a RAG system (document loading, chunking, embedding, vector storage)
/// - PART 2: RAG query pipeline (retrieval, context assembly, generation)
/// - PART 3: LLM-based evaluation (5 metrics: Faithfulness, Relevance, Precision, Recall, Correctness)
/// - PART 4: Embedding-based evaluation (3 metrics: AnswerSimilarity, ResponseContext, QueryContext)
/// - PART 5: Cost optimization (LLM vs Embedding metrics comparison)
/// - PART 6: Information Retrieval metrics (Recall@K, MRR - FREE code-based metrics)
/// 
/// Environment Variables Required:
/// - AZURE_OPENAI_ENDPOINT: Your Azure OpenAI endpoint
/// - AZURE_OPENAI_API_KEY: Your Azure OpenAI API key
/// - AZURE_OPENAI_DEPLOYMENT: Chat model (e.g., gpt-4o)
/// - AZURE_OPENAI_EMBEDDING_DEPLOYMENT: Embedding model (e.g., text-embedding-ada-002)
/// 
/// ⏱️ Time to understand: 15 minutes
/// </summary>
public static class ComprehensiveRAG
{
    // Knowledge base documents for our RAG system
    private static readonly (string Id, string Text)[] KnowledgeBase =
    [
        ("doc1", "France is a country in Western Europe. It is known for its rich history, art, and culture. The official language is French, and the currency is the Euro."),
        ("doc2", "Paris is the capital and largest city of France. It has a population of over 2 million in the city proper and over 12 million in the metropolitan area."),
        ("doc3", "The Eiffel Tower is a wrought-iron lattice tower in Paris, France. It was constructed from 1887 to 1889 as the entrance arch for the 1889 World's Fair."),
        ("doc4", "The Louvre Museum in Paris is the world's largest art museum. It houses over 35,000 works of art including the Mona Lisa and the Venus de Milo."),
        ("doc5", "French cuisine is renowned worldwide. Popular dishes include croissants, baguettes, coq au vin, and crème brûlée. France is also famous for its wines and cheeses."),
        ("doc6", "The French Revolution began in 1789 and fundamentally changed France. It led to the end of the monarchy and the rise of democratic ideals."),
        ("doc7", "Mont Blanc is the highest peak in the Alps and Western Europe at 4,808 meters. It is located on the border between France and Italy."),
        ("doc8", "The Palace of Versailles was the principal royal residence of France from 1682 until the French Revolution. It is known for the Hall of Mirrors and its vast gardens.")
    ];

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured || !AIConfig.IsEmbeddingConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        IChatClient chatClient = CreateChatClient();
        IChatClient evaluatorClient = CreateEvaluatorClient();
        IAgentEvalEmbeddings embeddingClient = CreateEmbeddingClient();

        Console.WriteLine("\n🏗️ PART 1: BUILDING THE RAG SYSTEM\n");

        Console.WriteLine("   📚 Step 1.1: Loading knowledge base documents...\n");
        foreach (var (id, text) in KnowledgeBase.Take(3))
        {
            Console.WriteLine($"      [{id}] {Truncate(text, 60)}...");
        }
        Console.WriteLine($"      ... and {KnowledgeBase.Length - 3} more documents\n");

        Console.WriteLine("   🔢 Step 1.2: Generating embeddings for all documents...");
        var texts = KnowledgeBase.Select(d => d.Text).ToArray();
        var embeddings = await embeddingClient.GetEmbeddingsAsync(texts);
        
        if (embeddings == null || embeddings.Count == 0)
        {
            throw new InvalidOperationException($"Embedding generation returned no results for {texts.Length} documents. " +
                "Check your Azure OpenAI embedding deployment configuration.");
        }
        
        if (embeddings.Count != texts.Length)
        {
            throw new InvalidOperationException($"Expected {texts.Length} embeddings but got {embeddings.Count}. " +
                "This may indicate an issue with the embedding provider.");
        }
        
        Console.WriteLine($"      ✅ Generated {embeddings.Count} embeddings (dimension: {embeddings[0].Length})\n");

        Console.WriteLine("   💾 Step 1.3: Storing in MemoryVectorStore...");
        var vectorStore = new MemoryVectorStore();
        for (int i = 0; i < KnowledgeBase.Length; i++)
        {
            vectorStore.Add(KnowledgeBase[i].Id, KnowledgeBase[i].Text, embeddings[i]);
        }
        Console.WriteLine($"      ✅ Indexed {vectorStore.Count} documents in vector store\n");

        PrintSectionComplete("RAG system built successfully!");

        Console.WriteLine("\n🔍 PART 2: RAG QUERY PIPELINE\n");

        var userQuery = "What is the capital of France and what famous landmarks are there?";
        var groundTruth = "Paris is the capital of France. Famous landmarks include the Eiffel Tower, the Louvre Museum, and the Palace of Versailles.";

        Console.WriteLine($"   ❓ User Query: \"{userQuery}\"\n");

        // Step 2.1: Embed the query
        Console.WriteLine("   🔢 Step 2.1: Embedding the query...");
        var queryEmbeddings = await embeddingClient.GetEmbeddingsAsync([userQuery]);
        
        if (queryEmbeddings == null || queryEmbeddings.Count == 0)
        {
            throw new InvalidOperationException("Query embedding generation returned no results.");
        }
        
        var queryEmbedding = queryEmbeddings[0];
        Console.WriteLine("      ✅ Query embedded\n");

        // Step 2.2: Retrieve relevant documents
        Console.WriteLine("   📥 Step 2.2: Retrieving relevant documents (top 3)...");
        var searchResults = vectorStore.Search(queryEmbedding, topK: 3);
        Console.WriteLine();
        foreach (var result in searchResults)
        {
            Console.WriteLine($"      [{result.Id}] Score: {result.Score:F3} - {Truncate(result.Text, 50)}...");
        }

        // Assemble context from retrieved documents
        var retrievedContext = string.Join("\n\n", searchResults.Select(r => r.Text));
        Console.WriteLine($"\n      ✅ Retrieved {searchResults.Count} documents for context\n");

        Console.WriteLine("   🤖 Step 2.3: Generating response with LLM...");
        var ragPrompt = $"""
            Answer the following question using ONLY the provided context. 
            Do not make up information not present in the context.
            
            Context:
            {retrievedContext}
            
            Question: {userQuery}
            
            Answer:
            """;

        var llmResponse = await chatClient.GetResponseAsync(ragPrompt);
        var generatedResponse = llmResponse.Text;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n      📝 Generated Response:");
        Console.WriteLine($"      \"{generatedResponse}\"\n");
        Console.ResetColor();

        PrintSectionComplete("RAG pipeline executed successfully!");

        Console.WriteLine("\n📊 PART 3: LLM-BASED EVALUATION (5 Metrics)\n");

        var llmResults = new List<(string Name, MetricResult Result)>();

        var evalContext = new EvaluationContext
        {
            Input = userQuery,
            Output = generatedResponse,
            Context = retrievedContext,
            GroundTruth = groundTruth
        };

        Console.WriteLine("   💡 LLM-based metrics use GPT to evaluate quality with nuanced understanding.");
        Console.WriteLine("   💰 Cost: ~$0.002-0.01 per evaluation | ⏱️ Latency: ~2-5 seconds each\n");

        Console.WriteLine("   📝 3.1 FAITHFULNESS (Hallucination Detection)");
        Console.WriteLine("      ❓ Is the response grounded in the provided context?\n");
        var faithfulness = new FaithfulnessMetric(evaluatorClient);
        var faithResult = await faithfulness.EvaluateAsync(evalContext);
        PrintMetricResult(faithResult, "Faithfulness");
        llmResults.Add(("Faithfulness", faithResult));

        Console.WriteLine("\n   📝 3.2 RELEVANCE (Response Quality)");
        Console.WriteLine("      ❓ Does the response address the user's question?\n");
        var relevance = new RelevanceMetric(evaluatorClient);
        var relevResult = await relevance.EvaluateAsync(evalContext);
        PrintMetricResult(relevResult, "Relevance");
        llmResults.Add(("Relevance", relevResult));

        Console.WriteLine("\n   📝 3.3 CONTEXT PRECISION (Retrieval Quality)");
        Console.WriteLine("      ❓ Was all retrieved context actually useful?\n");
        var precision = new ContextPrecisionMetric(evaluatorClient);
        var precisionResult = await precision.EvaluateAsync(evalContext);
        PrintMetricResult(precisionResult, "Context Precision");
        llmResults.Add(("Context Precision", precisionResult));

        Console.WriteLine("\n   📝 3.4 CONTEXT RECALL (Retrieval Coverage)");
        Console.WriteLine("      ❓ Did we retrieve all the information needed to answer?\n");
        var recall = new ContextRecallMetric(evaluatorClient);
        var recallResult = await recall.EvaluateAsync(evalContext);
        PrintMetricResult(recallResult, "Context Recall");
        llmResults.Add(("Context Recall", recallResult));

        Console.WriteLine("\n   📝 3.5 ANSWER CORRECTNESS (Ground Truth Comparison)");
        Console.WriteLine("      ❓ Does the response match the expected answer?\n");
        var correctness = new AnswerCorrectnessMetric(evaluatorClient);
        var correctnessResult = await correctness.EvaluateAsync(evalContext);
        PrintMetricResult(correctnessResult, "Answer Correctness");
        llmResults.Add(("Answer Correctness", correctnessResult));

        PrintSectionComplete($"LLM-based evaluation complete! Average: {llmResults.Average(r => r.Result.Score):F1}/100");

        Console.WriteLine("\n⚡ PART 4: EMBEDDING-BASED EVALUATION (3 Metrics)\n");

        Console.WriteLine("   💡 Embedding-based metrics use vector similarity - much faster and cheaper!");
        Console.WriteLine("   💰 Cost: ~$0.0001 per evaluation | ⏱️ Latency: ~0.1 seconds each\n");

        var embedResults = new List<(string Name, MetricResult Result)>();

        // 4.1 Answer Similarity - Quick Correctness Check
        Console.WriteLine("   📝 4.1 ANSWER SIMILARITY (Quick Correctness)");
        Console.WriteLine("      ❓ How semantically similar is the response to the ground truth?\n");
        var answerSimilarity = new AnswerSimilarityMetric(embeddingClient);
        var simResult = await answerSimilarity.EvaluateAsync(evalContext);
        PrintMetricResult(simResult, "Answer Similarity");
        embedResults.Add(("Answer Similarity", simResult));

        // 4.2 Response-Context Similarity - Grounding Check
        Console.WriteLine("\n   📝 4.2 RESPONSE-CONTEXT SIMILARITY (Grounding Check)");
        Console.WriteLine("      ❓ How well is the response grounded in the context?\n");
        var responseContext = new ResponseContextSimilarityMetric(embeddingClient);
        var rcResult = await responseContext.EvaluateAsync(evalContext);
        PrintMetricResult(rcResult, "Response-Context Similarity");
        embedResults.Add(("Response-Context", rcResult));

        // 4.3 Query-Context Similarity - Retrieval Relevance
        Console.WriteLine("\n   📝 4.3 QUERY-CONTEXT SIMILARITY (Retrieval Relevance)");
        Console.WriteLine("      ❓ How relevant is the retrieved context to the query?\n");
        var queryContext = new QueryContextSimilarityMetric(embeddingClient);
        var qcResult = await queryContext.EvaluateAsync(evalContext);
        PrintMetricResult(qcResult, "Query-Context Similarity");
        embedResults.Add(("Query-Context", qcResult));

        PrintSectionComplete($"Embedding-based evaluation complete! Average: {embedResults.Average(r => r.Result.Score):F1}/100");

        Console.WriteLine("\n💰 PART 5: COST OPTIMIZATION COMPARISON\n");

        Console.WriteLine(@"
   ┌──────────────────────────────────────────────────────────────────────┐
   │  METRIC TYPE            │ COST/EVAL    │ LATENCY   │ BEST FOR        │
   ├──────────────────────────────────────────────────────────────────────┤
   │  🔷 LLM-based (5)       │ ~$0.01       │ ~2-5s     │ Quality gates,  │
   │     Faithfulness        │              │           │ Pre-production, │
   │     Relevance           │              │           │ Detailed        │
   │     Context Precision   │              │           │ analysis        │
   │     Context Recall      │              │           │                 │
   │     Answer Correctness  │              │           │                 │
   ├──────────────────────────────────────────────────────────────────────┤
   │  ⚡ Embedding-based (3)  │ ~$0.0001     │ ~0.1s     │ CI/CD,          │
   │     Answer Similarity   │              │           │ Development,    │
   │     Response-Context    │              │           │ High-volume,    │
   │     Query-Context       │              │           │ Real-time       │
   └──────────────────────────────────────────────────────────────────────┘
");

        // Print comparison table with actual results
        Console.WriteLine("   📊 RESULTS COMPARISON FROM THIS RUN:\n");
        Console.WriteLine("   ┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("   │  LLM-BASED METRICS              │  SCORE  │  STATUS        │");
        Console.WriteLine("   ├─────────────────────────────────────────────────────────────┤");
        foreach (var (name, result) in llmResults)
        {
            var status = result.Passed ? "✅ PASSED" : "❌ FAILED";
            Console.WriteLine($"   │  {name,-30} │  {result.Score,5:F1}  │  {status,-13} │");
        }
        Console.WriteLine("   └─────────────────────────────────────────────────────────────┘\n");

        Console.WriteLine("   ┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("   │  EMBEDDING-BASED METRICS        │  SCORE  │  STATUS        │");
        Console.WriteLine("   ├─────────────────────────────────────────────────────────────┤");
        foreach (var (name, result) in embedResults)
        {
            var status = result.Passed ? "✅ PASSED" : "❌ FAILED";
            Console.WriteLine($"   │  {name,-30} │  {result.Score,5:F1}  │  {status,-13} │");
        }
        Console.WriteLine("   └─────────────────────────────────────────────────────────────┘\n");

        // Print optimization strategies
        Console.WriteLine(@"
   🎯 OPTIMIZATION STRATEGIES:
   
   ┌─────────────────────────────────────────────────────────────────────┐
   │  SCENARIO                    │  RECOMMENDED METRICS                 │
   ├─────────────────────────────────────────────────────────────────────┤
   │  Local Development           │  Embedding metrics only (fast/free)  │
   │  CI/CD Pipeline              │  Embedding + sample LLM metrics      │
   │  Pre-Production Validation   │  All LLM metrics (quality gates)     │
   │  Production Monitoring       │  Embedding for volume, LLM sampling  │
   │  Regression Testing          │  Embedding for speed, LLM for depth  │
   │  A/B Testing at Scale        │  Embedding metrics (cost-effective)  │
   └─────────────────────────────────────────────────────────────────────┘

   💡 KEY INSIGHT: Use embedding metrics for 90% of evaluations,
      reserve LLM metrics for critical checkpoints and deep analysis.
");

        Console.WriteLine("📊 PART 6: INFORMATION RETRIEVAL METRICS (CODE-BASED - FREE!)\n");

        Console.WriteLine("   These metrics evaluate retrieval quality without any API calls!\n");

        // Recall@K: Measures if relevant documents are in top K results
        var irRecallMetric = new AgentEval.Metrics.Retrieval.RecallAtKMetric(k: 3);
        
        // MRR: Measures ranking quality (where is first relevant result?)
        var irMrrMetric = new AgentEval.Metrics.Retrieval.MRRMetric();

        // Create evaluation context with retrieval information
        var irContext = new EvaluationContext
        {
            Input = userQuery,
            Context = retrievedContext,
            Output = generatedResponse,
            GroundTruth = groundTruth
        };
        // Set retrieval properties
        irContext.SetProperty("RetrievedDocumentIds", searchResults.Select(r => r.Id).ToList());
        irContext.SetProperty("RelevantDocumentIds", new List<string> { "doc2", "doc3", "doc4" });

        Console.WriteLine("   📈 RECALL@K METRIC");
        Console.WriteLine("   Measures: How many relevant docs are in top K results?\n");
        Console.WriteLine($"      Retrieved (top 3): {string.Join(", ", searchResults.Take(3).Select(r => r.Id))}");
        Console.WriteLine($"      Relevant docs: doc2, doc3, doc4");
        
        var irRecallResult = await irRecallMetric.EvaluateAsync(irContext);
        Console.WriteLine($"      Recall@3 Score: {irRecallResult.Score:F1}/100");
        Console.WriteLine($"      Passed: {irRecallResult.Passed}");
        Console.WriteLine($"      Explanation: {irRecallResult.Explanation}\n");

        Console.WriteLine("   📉 MRR (MEAN RECIPROCAL RANK) METRIC");
        Console.WriteLine("   Measures: Where does the first relevant doc appear?\n");
        Console.WriteLine($"      Retrieved order: {string.Join(", ", searchResults.Select(r => r.Id))}");
        
        var irMrrResult = await irMrrMetric.EvaluateAsync(irContext);
        Console.WriteLine($"      MRR Score: {irMrrResult.Score:F1}/100");
        Console.WriteLine($"      Passed: {irMrrResult.Passed}");
        Console.WriteLine($"      Explanation: {irMrrResult.Explanation}");

        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────┐
   │  IR METRIC     │ COST  │ WHAT IT MEASURES                          │
   ├─────────────────────────────────────────────────────────────────────┤
   │  Recall@K      │ FREE  │ % of relevant docs in top K results       │
   │  MRR           │ FREE  │ Ranking position of first relevant doc    │
   ├─────────────────────────────────────────────────────────────────────┤
   │  🔜 COMING SOON                                                    │
   │  nDCG          │ FREE  │ Ranking quality with graded relevance     │
   │  Precision@K   │ FREE  │ % of top K that are relevant              │
   │  Hit Rate      │ FREE  │ Binary: any relevant in top K?            │
   └─────────────────────────────────────────────────────────────────────┘

   💡 KEY INSIGHT: Use IR metrics alongside semantic metrics for
      complete retrieval quality assessment. They're FREE to compute!
");

        Console.WriteLine("📋 SUMMARY: RAG EVALUATION METRICS REFERENCE");
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────┐
   │  METRIC                │ WHAT IT MEASURES          │ REQUIRES       │
   ├─────────────────────────────────────────────────────────────────────┤
   │  RETRIEVAL EVALUATION                                               │
   │  ├─ ContextPrecision   │ Retrieved chunks useful?  │ Context        │
   │  ├─ ContextRecall      │ All needed info found?    │ Context+Truth  │
   │  ├─ QueryContextSim    │ Retrieval relevant?       │ Context        │
   │  ├─ Recall@K (FREE!)   │ Relevant docs in top K?   │ Doc IDs        │
   │  └─ MRR (FREE!)        │ First relevant doc rank?  │ Doc IDs        │
   ├─────────────────────────────────────────────────────────────────────┤
   │  GENERATION EVALUATION                                              │
   │  ├─ Faithfulness       │ No hallucinations?        │ Context        │
   │  ├─ Relevance          │ Answers the question?     │ (none)         │
   │  └─ ResponseContextSim │ Grounded in context?      │ Context        │
   ├─────────────────────────────────────────────────────────────────────┤
   │  END-TO-END EVALUATION                                              │
   │  ├─ AnswerCorrectness  │ Matches ground truth?     │ GroundTruth    │
   │  └─ AnswerSimilarity   │ Semantically similar?     │ GroundTruth    │
   └─────────────────────────────────────────────────────────────────────┘
");

        Console.WriteLine("💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • Build RAG with MemoryVectorStore for development/testing");
        Console.WriteLine("   • Use ALL 8 metrics for comprehensive RAG evaluation");
        Console.WriteLine("   • LLM metrics give nuanced understanding, embedding metrics give speed");
        Console.WriteLine("   • Start with embedding metrics, add LLM metrics for quality gates");
        Console.WriteLine("   • ContextPrecision + Recall = retrieval quality");
        Console.WriteLine("   • Faithfulness + Relevance = generation quality");
        Console.WriteLine("   • AnswerCorrectness/Similarity = end-to-end quality");

        Console.WriteLine("\n🔧 ENVIRONMENT VARIABLES REQUIRED:");
        Console.WriteLine("   • AZURE_OPENAI_ENDPOINT - Your Azure OpenAI endpoint");
        Console.WriteLine("   • AZURE_OPENAI_API_KEY - Your API key");
        Console.WriteLine("   • AZURE_OPENAI_DEPLOYMENT - Chat model (e.g., gpt-4o)");
        Console.WriteLine("   • AZURE_OPENAI_EMBEDDING_DEPLOYMENT - Embedding model (e.g., text-embedding-ada-002)");

        Console.WriteLine("\n🎉 Sample complete!\n");
    }

    #region Client Creation

    private static IChatClient CreateChatClient()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        return azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
    }

    private static IChatClient CreateEvaluatorClient()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        return azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
    }

    private static IAgentEvalEmbeddings CreateEmbeddingClient()
    {
        Console.WriteLine($"   ℹ️  Using Azure OpenAI embeddings: {AIConfig.EmbeddingDeployment}\n");
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var embeddingClient = azureClient.GetEmbeddingClient(AIConfig.EmbeddingDeployment);
        return new MEAIEmbeddingAdapter(embeddingClient.AsIEmbeddingGenerator());
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE B1 - Credentials Required                            │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample requires both chat and embedding Azure OpenAI deployments.    │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT              - Your Azure OpenAI endpoint          │
   │    AZURE_OPENAI_API_KEY               - Your API key                        │
   │    AZURE_OPENAI_DEPLOYMENT            - Chat model (e.g., gpt-4o)           │
   │    AZURE_OPENAI_EMBEDDING_DEPLOYMENT  - Embedding model (e.g., ada-002)     │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    #endregion

    #region Helper Methods

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🏗️ SAMPLE B1: COMPREHENSIVE RAG - Build & Evaluate a Complete RAG System   ║
║   All 8 metrics: 5 LLM-based + 3 Embedding-based                             ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMetricResult(MetricResult result, string metricName)
    {
        var icon = result.Passed ? "✅" : "❌";
        Console.ForegroundColor = result.Passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"      {icon} {metricName}: {result.Score:F1}/100");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(result.Explanation))
        {
            Console.WriteLine($"         📝 {Truncate(result.Explanation, 70)}");
        }
    }

    private static void PrintSectionComplete(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n   ✅ {message}");
        Console.ResetColor();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", "").Trim();
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    #endregion
}
