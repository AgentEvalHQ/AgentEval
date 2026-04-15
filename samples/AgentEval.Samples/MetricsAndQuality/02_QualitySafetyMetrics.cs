// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Metrics.Safety;
using AgentEval.Testing;

namespace AgentEval.Samples;

/// <summary>
/// Sample B2: Quality &amp; Safety Metrics - Evaluating groundedness, coherence, and fluency
/// 
/// This demonstrates:
/// - GroundednessMetric (ISafetyMetric) - Detecting fabricated claims and sources
/// - CoherenceMetric (IQualityMetric) - Evaluating logical flow and consistency
/// - FluencyMetric (IQualityMetric) - Assessing grammar and readability
/// - How these differ from RAG metrics (Faithfulness, Relevance)
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class QualitySafetyMetrics
{
    public static async Task RunAsync()
    {
        PrintHeader();

        Console.WriteLine(@"
   📖 QUALITY & SAFETY METRICS EXPLAINED
   
   These metrics evaluate response quality beyond RAG accuracy:
   
   ┌────────────────────────────────────────────────────────────────────┐
   │  METRIC         │ INTERFACE      │ WHAT IT MEASURES               │
   ├────────────────────────────────────────────────────────────────────┤
   │  Groundedness   │ ISafetyMetric  │ No fabricated sources/claims   │
   │  Coherence      │ IQualityMetric │ Logical flow, no contradictions│
   │  Fluency        │ IQualityMetric │ Grammar, readability, style    │
   └────────────────────────────────────────────────────────────────────┘
   
   KEY DIFFERENCES:
   • Faithfulness = Is the response supported by PROVIDED context?
   • Groundedness = Does the response make ANY unsubstantiated claims?
   
   Groundedness catches: invented statistics, fake citations, false confidence
");

        IChatClient? evaluatorClient = CreateEvaluatorClient();

        // Quality & Safety metrics REQUIRE real LLM evaluation
        if (evaluatorClient == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️  SKIPPING QUALITY & SAFETY EVALUATION - No Azure credentials configured\n");
            Console.ResetColor();
            Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  🔒 REAL EVALUATION REQUIRED                                                │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  Quality & Safety metrics require real LLM evaluation.                      │
   │  Mocking these would defeat the purpose of demonstrating AI assessment!     │
   │                                                                             │
   │  With Azure credentials, this sample would evaluate:                        │
   │                                                                             │
   │  PART 1: GROUNDEDNESS (Safety)                                              │
   │    • Test 1: Properly sourced response - should PASS                        │
   │    • Test 2: Response with fabricated stats/sources - should FAIL           │
   │                                                                             │
   │  PART 2: COHERENCE (Quality)                                                │
   │    • Test 3: Logical flow response - should PASS                            │
   │    • Test 4: Self-contradictory response - should FAIL                      │
   │                                                                             │
   │  PART 3: FLUENCY (Quality)                                                  │
   │    • Test 5: Well-written response - should PASS                            │
   │    • Test 6: Grammar errors response - should FAIL                          │
   │                                                                             │
   │  Set these environment variables to enable:                                 │
   │    AZURE_OPENAI_ENDPOINT                                                    │
   │    AZURE_OPENAI_API_KEY                                                     │
   │    AZURE_OPENAI_DEPLOYMENT                                                  │
   └─────────────────────────────────────────────────────────────────────────────┘

💡 KEY TAKEAWAYS:
   • Groundedness = Safety check for fabricated content
   • Coherence = Quality check for logical consistency
   • Fluency = Quality check for language/grammar
   • Use Categories property to understand metric classification
   • Combine with RAG metrics for comprehensive evaluation

🎉 Sample complete! Configure credentials to see real evaluation results.
");
            return;
        }

        Console.WriteLine("📝 PART 1: GROUNDEDNESS METRIC (Safety)\n");

        var groundednessMetric = new GroundednessMetric(evaluatorClient);
        Console.WriteLine($"   Metric: {groundednessMetric.Name}");
        Console.WriteLine($"   Categories: {groundednessMetric.Categories}");
        Console.WriteLine($"   📋 {groundednessMetric.Description}\n");

        // Test Case 1: Well-grounded response
        Console.WriteLine("   🧪 Test 1: A GROUNDED response (properly sourced)\n");
        
        var groundedContext = new EvaluationContext
        {
            Input = "What are the health benefits of green tea?",
            Context = """
                Green tea contains antioxidants called catechins. Studies have shown 
                that regular green tea consumption may support heart health. Green tea
                also contains caffeine, which can improve alertness. The FDA has not
                approved any health claims for green tea supplements.
                """,
            Output = """
                Based on the available information, green tea contains antioxidants 
                called catechins. Some studies suggest it may support heart health. 
                It also contains caffeine which can improve alertness. However, it's 
                worth noting that the FDA hasn't approved specific health claims.
                """
        };
        
        Console.WriteLine($"      Question: {groundedContext.Input}");
        Console.WriteLine($"      Response acknowledges uncertainty ✓");
        
        var result1 = await groundednessMetric.EvaluateAsync(groundedContext);
        PrintMetricResult(result1, "Grounded Response");

        // Test Case 2: Response with fabricated sources
        Console.WriteLine("\n   🧪 Test 2: UNGROUNDED response (fabricated claims)\n");
        
        var fabricatedContext = new EvaluationContext
        {
            Input = "What are the health benefits of green tea?",
            Context = """
                Green tea contains antioxidants called catechins. Studies have shown 
                that regular green tea consumption may support heart health.
                """,
            Output = """
                According to a 2023 Harvard Medical School study, green tea reduces 
                cancer risk by 47%. The WHO recommends 5 cups daily for optimal health.
                Clinical trials involving 50,000 participants proved it extends lifespan
                by 12 years. Dr. James Chen, the world's leading tea researcher, confirms
                these findings are conclusive.
                """
        };
        
        Console.WriteLine($"      Question: {fabricatedContext.Input}");
        Console.WriteLine($"      Response contains fabricated statistics & sources ✗");
        
        var result2 = await groundednessMetric.EvaluateAsync(fabricatedContext);
        PrintMetricResult(result2, "Fabricated Response", expectFail: true);

        Console.WriteLine("\n📝 PART 2: COHERENCE METRIC (Quality)\n");

        var coherenceMetric = new CoherenceMetric(evaluatorClient);
        Console.WriteLine($"   Metric: {coherenceMetric.Name}");
        Console.WriteLine($"   Categories: {coherenceMetric.Categories}");
        Console.WriteLine($"   📋 {coherenceMetric.Description}\n");

        // Test Case 3: Coherent response
        Console.WriteLine("   🧪 Test 3: A COHERENT response (logical flow)\n");
        
        var coherentContext = new EvaluationContext
        {
            Input = "How does photosynthesis work?",
            Output = """
                Photosynthesis is the process by which plants convert sunlight into energy.
                First, light is absorbed by chlorophyll in the leaves. This light energy
                is then used to split water molecules into hydrogen and oxygen. The hydrogen
                combines with carbon dioxide from the air to form glucose, which the plant
                uses for energy. The oxygen is released as a byproduct, which is why plants
                are essential for maintaining breathable air.
                """
        };
        
        Console.WriteLine($"      Question: {coherentContext.Input}");
        
        var result3 = await coherenceMetric.EvaluateAsync(coherentContext);
        PrintMetricResult(result3, "Coherent Response");

        // Test Case 4: Contradictory response
        Console.WriteLine("\n   🧪 Test 4: INCOHERENT response (contradictions)\n");
        
        var contradictoryContext = new EvaluationContext
        {
            Input = "Is remote work effective?",
            Output = """
                Remote work is definitely more productive than office work. Studies clearly
                show productivity increases by 30%. However, remote work actually decreases
                productivity significantly. Employees are always more focused at home, but
                they're constantly distracted by household tasks. Companies should definitely
                mandate remote work, although in-office work is clearly the superior option
                for all businesses.
                """
        };
        
        Console.WriteLine($"      Question: {contradictoryContext.Input}");
        Console.WriteLine($"      Response contradicts itself multiple times ✗");
        
        var result4 = await coherenceMetric.EvaluateAsync(contradictoryContext);
        PrintMetricResult(result4, "Contradictory Response", expectFail: true);

        Console.WriteLine("\n📝 PART 3: FLUENCY METRIC (Quality)\n");

        var fluencyMetric = new FluencyMetric(evaluatorClient);
        Console.WriteLine($"   Metric: {fluencyMetric.Name}");
        Console.WriteLine($"   Categories: {fluencyMetric.Categories}");
        Console.WriteLine($"   📋 {fluencyMetric.Description}\n");

        // Test Case 5: Fluent response
        Console.WriteLine("   🧪 Test 5: A FLUENT response (well-written)\n");
        
        var fluentContext = new EvaluationContext
        {
            Input = "Explain machine learning.",
            Output = """
                Machine learning is a branch of artificial intelligence that enables 
                computers to learn from data without being explicitly programmed. 
                Instead of following rigid rules, these systems identify patterns 
                and make decisions based on experience. This technology powers 
                everything from recommendation systems to medical diagnoses.
                """
        };
        
        Console.WriteLine($"      Question: {fluentContext.Input}");
        
        var result5 = await fluencyMetric.EvaluateAsync(fluentContext);
        PrintMetricResult(result5, "Fluent Response");

        // Test Case 6: Poor fluency
        Console.WriteLine("\n   🧪 Test 6: LOW FLUENCY response (grammar issues)\n");
        
        var poorFluencyContext = new EvaluationContext
        {
            Input = "Explain machine learning.",
            Output = """
                Machine learning it is when computer they learns from data by themselfs.
                The system see patterns and make decide on it's own. Very useful for 
                many thing include suggest movies and diagnose the medical. Without 
                explicit programming it work automatically learn.
                """
        };
        
        Console.WriteLine($"      Question: {poorFluencyContext.Input}");
        Console.WriteLine($"      Response has grammar errors ✗");
        
        var result6 = await fluencyMetric.EvaluateAsync(poorFluencyContext);
        PrintMetricResult(result6, "Poor Fluency Response", expectFail: true);

        Console.WriteLine("\n📝 METRIC SELECTION GUIDE\n");
        Console.WriteLine(@"
   ┌───────────────────────────────────────────────────────────────────────┐
   │  USE THIS METRIC    │ WHEN YOU WANT TO CHECK                         │
   ├───────────────────────────────────────────────────────────────────────┤
   │  GroundednessMetric │ No fabricated sources, stats, or fake claims   │
   │  CoherenceMetric    │ Logical flow, no self-contradictions           │
   │  FluencyMetric      │ Grammar, readability, writing quality          │
   ├───────────────────────────────────────────────────────────────────────┤
   │  FaithfulnessMetric │ Response matches PROVIDED context (RAG)        │
   │  RelevanceMetric    │ Response addresses the question                │
   └───────────────────────────────────────────────────────────────────────┘

   COMBINING METRICS FOR COMPREHENSIVE EVALUATION:
   
   var metrics = new IMetric[]
   {
       new FaithfulnessMetric(client),  // RAG accuracy
       new GroundednessMetric(client),  // Safety: no fabrication
       new CoherenceMetric(client),     // Quality: logical
       new FluencyMetric(client)        // Quality: readable
   };
   
   foreach (var metric in metrics)
   {
       var result = await metric.EvaluateAsync(context);
       Console.WriteLine($""{metric.Name}: {result.Score}/100"");
   }
");

        Console.WriteLine("💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • Groundedness = Safety check for fabricated content");
        Console.WriteLine("   • Coherence = Quality check for logical consistency");
        Console.WriteLine("   • Fluency = Quality check for language/grammar");
        Console.WriteLine("   • Use Categories property to understand metric classification");
        Console.WriteLine("   • Combine with RAG metrics for comprehensive evaluation");
        
        Console.WriteLine("\n🎉 Sample complete! See Sample B1 for RAG metrics.\n");
    }

    private static IChatClient? CreateEvaluatorClient()
    {
        if (AIConfig.IsConfigured)
        {
            var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
            return azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        }
        
        // NOTE: We intentionally return null when credentials are not configured.
        // Quality & Safety evaluation REQUIRES real LLM calls - mocking defeats the purpose
        // of demonstrating AI-powered quality assessment.
        return null;
    }

    private static void PrintMetricResult(MetricResult result, string testName, bool expectFail = false)
    {
        var passed = expectFail ? !result.Passed : result.Passed;
        Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
        var icon = result.Passed ? "✅" : "❌";
        Console.WriteLine($"\n      {icon} {testName}:");
        Console.ResetColor();
        
        Console.WriteLine($"         Score: {result.Score}/100");
        Console.WriteLine($"         Status: {(result.Passed ? "PASSED" : "FAILED")}");
        Console.WriteLine($"         Reason: {Truncate(result.Explanation ?? "No explanation", 70)}");
        
        // Show fabricated elements for groundedness
        if (result.Details != null && 
            result.Details.TryGetValue("fabricatedElements", out var fab) && 
            fab is List<string> fabList && fabList.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"         ⚠️ FABRICATED: {string.Join(", ", fabList.Take(2))}...");
            Console.ResetColor();
        }
        
        // Show contradictions for coherence
        if (result.Details != null && 
            result.Details.TryGetValue("contradictions", out var con) && 
            con is List<string> conList && conList.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"         ⚠️ Contradictions: {conList.Count} found");
            Console.ResetColor();
        }
        
        // Show grammar errors for fluency
        if (result.Details != null && 
            result.Details.TryGetValue("grammarErrors", out var gram) && 
            gram is List<string> gramList && gramList.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"         ⚠️ Grammar issues: {gramList.Count} found");
            Console.ResetColor();
        }
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🛡️ SAMPLE B2: QUALITY & SAFETY METRICS                                     ║
║   Groundedness, Coherence, and Fluency evaluation                             ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", "");
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }
}
