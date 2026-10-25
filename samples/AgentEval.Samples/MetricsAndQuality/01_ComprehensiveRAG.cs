// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Retrieval;
using AgentEval.Embeddings;
using static AgentEval.Samples.ComprehensiveRagExampleData;

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
/// - A chat provider (AI_INFERENCE_PROVIDER and its variables; see AIConfig)
/// - An embedding model on the SAME provider: BITDEER_EMBEDDING_MODEL (default BAAI/bge-m3),
///   OPENAI_EMBEDDING_MODEL (default text-embedding-3-small), or, with no default,
///   AZURE_OPENAI_EMBEDDING_DEPLOYMENT, FOUNDRY_EMBEDDING_MODEL, OPENAI_COMPATIBLE_EMBEDDING_MODEL
///
/// The embedding thresholds (70 / 50 / 50) are raw cosine similarity × 100, fixed in Core. Cosine ranges
/// differ by embedding model, so a score means something only next to the model that produced it; the
/// sample prints the model with the scores. Measured on BAAI/bge-m3 (2026-10-21), they separate on-topic
/// from off-topic text but not a right answer from a wrong one on the same topic: the reference scored
/// 77 against "Lyon is the capital ... the Colosseum, Big Ben" and 33 against an unrelated sentence.
///
/// ⏱️ Time to understand: 15 minutes
/// </summary>
public static class ComprehensiveRAG
{
    public static async Task RunAsync()
    {
        PrintHeader();
        Console.WriteLine($"   Reference dataset: {DatasetVersion}\n");

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            SampleOutcome.Skip("no chat provider configured");
            return;
        }
        if (!AIConfig.IsEmbeddingConfigured)
        {
            PrintMissingCredentialsBox();
            SampleOutcome.Skip($"no embedding model configured: {AIConfig.EmbeddingSettings.Diagnostic}");
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
        Console.WriteLine($"      ... and {KnowledgeBase.Count - 3} more documents\n");

        Console.WriteLine("   🔢 Step 1.2: Generating embeddings for all documents...");
        var texts = KnowledgeBase.Select(d => d.Text).ToArray();
        var embeddings = await embeddingClient.GetEmbeddingsAsync(texts);
        
        if (embeddings == null || embeddings.Count == 0)
        {
            throw new InvalidOperationException($"Embedding generation returned no results for {texts.Length} documents. " +
                $"Check the embedding model ({AIConfig.EmbeddingSettings.ModelIdentity}).");
        }
        
        if (embeddings.Count != texts.Length)
        {
            throw new InvalidOperationException($"Expected {texts.Length} embeddings but got {embeddings.Count}. " +
                "This may indicate an issue with the embedding provider.");
        }
        
        Console.WriteLine($"      ✅ Generated {embeddings.Count} embeddings (dimension: {embeddings[0].Length})\n");

        Console.WriteLine("   💾 Step 1.3: Storing in MemoryVectorStore...");
        var vectorStore = new MemoryVectorStore();
        for (int i = 0; i < KnowledgeBase.Count; i++)
        {
            vectorStore.Add(KnowledgeBase[i].Id, KnowledgeBase[i].Text, embeddings[i]);
        }
        Console.WriteLine($"      ✅ Indexed {vectorStore.Count} documents in vector store\n");

        PrintSectionComplete("RAG system built successfully!");

        Console.WriteLine("\n🔍 PART 2: RAG QUERY PIPELINE\n");

        var userQuery = Query;
        var groundTruth = GroundTruth;

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

        PrintSectionSummary("LLM-based evaluation", llmResults);

        Console.WriteLine("\n⚡ PART 4: EMBEDDING-BASED EVALUATION (3 Metrics)\n");

        Console.WriteLine("   💡 Embedding-based metrics use vector similarity - much faster and cheaper!");
        Console.WriteLine("   💰 Cost: ~$0.0001 per evaluation | ⏱️ Latency: ~0.1 seconds each");
        Console.WriteLine($"   🔢 Embedding model: {AIConfig.EmbeddingSettings.ModelIdentity}. Score = cosine similarity × 100, passing");
        Console.WriteLine("      at 70 (answer) and 50 (context) for every model. Cosine ranges differ by model, so compare");
        Console.WriteLine("      these scores only with scores from the same model. They measure topical closeness: a wrong");
        Console.WriteLine("      answer on the same topic can still pass, which is what Answer Correctness (3.5) is for.\n");

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

        PrintSectionSummary("Embedding-based evaluation", embedResults);

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
            var status = Status(result);
            Console.WriteLine($"   │  {name,-30} │  {Score(result),5}  │  {status,-13} │");
        }
        Console.WriteLine("   └─────────────────────────────────────────────────────────────┘\n");

        Console.WriteLine("   ┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("   │  EMBEDDING-BASED METRICS        │  SCORE  │  STATUS        │");
        Console.WriteLine("   ├─────────────────────────────────────────────────────────────┤");
        foreach (var (name, result) in embedResults)
        {
            var status = Status(result);
            Console.WriteLine($"   │  {name,-30} │  {Score(result),5}  │  {status,-13} │");
        }
        Console.WriteLine("   └─────────────────────────────────────────────────────────────┘\n");

        // The verdict. A failed accuracy dimension (the answer is not grounded in its context, or does not match the
        // reference) fails the run; a quality dimension below its threshold warns and names it.
        RecordOutcome("Faithfulness", faithResult, accuracy: true);
        RecordOutcome("Relevance", relevResult, accuracy: false);
        RecordOutcome("Context Precision", precisionResult, accuracy: false);
        RecordOutcome("Context Recall", recallResult, accuracy: false);
        RecordOutcome("Answer Correctness", correctnessResult, accuracy: true);
        var embeddingModel = $" on {AIConfig.EmbeddingSettings.ModelIdentity}";
        RecordOutcome("Answer Similarity", simResult, accuracy: false, embeddingModel);
        RecordOutcome("Response-Context Similarity", rcResult, accuracy: false, embeddingModel);
        RecordOutcome("Query-Context Similarity", qcResult, accuracy: false, embeddingModel);

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
        irContext.SetProperty("RelevantDocumentIds", RelevantDocumentIds);

        Console.WriteLine("   📈 RECALL@K METRIC");
        Console.WriteLine("   Measures: How many relevant docs are in top K results?\n");
        Console.WriteLine($"      Retrieved (top 3): {string.Join(", ", searchResults.Take(3).Select(r => r.Id))}");
        Console.WriteLine($"      Relevant docs: {string.Join(", ", RelevantDocumentIds)}");
        
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

        // The retrieval ranked by the embedding model: below threshold is a retrieval-quality warning.
        RecordOutcome("Recall@3", irRecallResult, accuracy: false, embeddingModel);
        RecordOutcome("MRR", irMrrResult, accuracy: false, embeddingModel);

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
        Console.WriteLine("   • AI_INFERENCE_PROVIDER and that provider's variables - the chat model (see AIConfig)");
        Console.WriteLine("   • The embedding model on the same provider - BITDEER_EMBEDDING_MODEL (default BAAI/bge-m3),");
        Console.WriteLine("     OPENAI_EMBEDDING_MODEL (default text-embedding-3-small), AZURE_OPENAI_EMBEDDING_DEPLOYMENT,");
        Console.WriteLine("     FOUNDRY_EMBEDDING_MODEL or OPENAI_COMPATIBLE_EMBEDDING_MODEL (no default)");

        Console.WriteLine("\n🎉 Sample complete!\n");
    }

    #region Client Creation

    private static IChatClient CreateChatClient()
    {
        return AIConfig.CreateChatClient(AIConfig.ModelDeployment);
    }

    private static IChatClient CreateEvaluatorClient()
    {
        return AIConfig.CreateChatClient(AIConfig.ModelDeployment);
    }

    private static IAgentEvalEmbeddings CreateEmbeddingClient()
    {
        // Embeddings follow the chat provider: same host, same key, the model from that provider's variable.
        Console.WriteLine($"   🔗 Chat: {AIConfig.Describe()}");
        Console.WriteLine($"   🔢 Embeddings: {AIConfig.DescribeEmbeddings()}\n");
        return new MEAIEmbeddingAdapter(AIConfig.CreateEmbeddingGenerator());
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE B1 - needs a chat model AND an embedding model        │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  Embeddings follow AI_INFERENCE_PROVIDER, on the chat provider's host:      │
   │    BITDEER_EMBEDDING_MODEL              default BAAI/bge-m3                 │
   │    OPENAI_EMBEDDING_MODEL               default text-embedding-3-small      │
   │    AZURE_OPENAI_EMBEDDING_DEPLOYMENT    required                            │
   │    FOUNDRY_EMBEDDING_MODEL              required                            │
   │    OPENAI_COMPATIBLE_EMBEDDING_MODEL    required                            │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
        var why = AIConfig.IsConfigured ? AIConfig.EmbeddingSettings.Diagnostic : AIConfig.Settings.Diagnostic;
        if (why is not null)
            Console.WriteLine($"   ↳ {why}\n");
    }

    /// <summary>
    /// Records one metric's verdict. A failed accuracy dimension fails the run; a failed quality dimension warns,
    /// naming it; a metric that produced no measurement is recorded as not measured, never as either.
    /// </summary>
    private static void RecordOutcome(string name, MetricResult result, bool accuracy, string? where = null)
    {
        if (CouldNotMeasure(result))
        {
            SampleOutcome.Skip($"{name}{where}: not measured — {result.Explanation}");
            return;
        }
        if (result.Passed)
            return;

        var reason = $"{name} {result.Score:F1}/100{where} did not pass: {Truncate(result.Explanation ?? "", 160)}";
        if (accuracy)
            SampleOutcome.Fail(reason);
        else
            SampleOutcome.Warn(reason);
    }

    /// <summary>
    /// True when a metric could not produce a measurement: an input was not supplied, the judge's reply did not parse,
    /// or the judge or embedding call failed. Core reports each as not measured (<see cref="MetricResult.Measured"/>,
    /// with the cause in <see cref="MetricResult.NotMeasuredCause"/>); a judge that could not grade is not an answer that
    /// failed.
    /// </summary>
    private static bool CouldNotMeasure(MetricResult result) => !result.Measured;

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
        if (CouldNotMeasure(result))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"      ⚪ {metricName}: not measured");
        }
        else
        {
            Console.ForegroundColor = result.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"      {(result.Passed ? "✅" : "❌")} {metricName}: {result.Score:F1}/100");
        }
        Console.ResetColor();

        if (!string.IsNullOrEmpty(result.Explanation))
        {
            Console.WriteLine($"         📝 {Truncate(result.Explanation, 70)}");
        }
    }

    private static string Status(MetricResult result) =>
        CouldNotMeasure(result) ? "⚪ UNMEASURED" : result.Passed ? "✅ PASSED" : "❌ FAILED";

    // An unmeasured result has no score: "n/a", never the 0.0 Core stores beside it.
    private static string Score(MetricResult result) => CouldNotMeasure(result) ? "n/a" : result.Score.ToString("F1");

    /// <summary>
    /// How many metrics passed, never an average alone: an average of 67 reads like a result while three of five failed.
    /// The average covers only the measured metrics; a not-measured placeholder 0 is no score.
    /// </summary>
    private static void PrintSectionSummary(string section, List<(string Name, MetricResult Result)> results)
    {
        var measured = results.Where(r => !CouldNotMeasure(r.Result)).ToList();
        var passed = measured.Count(r => r.Result.Passed);
        var notMeasured = results.Count - measured.Count;
        var summary = $"{section}: {passed} of {results.Count} passed"
            + (notMeasured > 0 ? $", {notMeasured} not measured" : "")
            + (measured.Count > 0 ? $" (average of the measured {measured.Average(r => r.Result.Score):F1}/100)" : "");

        var allPassed = passed == results.Count;
        Console.ForegroundColor = allPassed ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"\n   {(allPassed ? "✅" : "⚠️")} {summary}");
        Console.ResetColor();
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
