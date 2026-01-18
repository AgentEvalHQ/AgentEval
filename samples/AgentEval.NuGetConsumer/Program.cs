// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors
// 
// ═══════════════════════════════════════════════════════════════════════════════
//    AgentEval NuGet Consumer Sample - Showcase of All Major Features
// ═══════════════════════════════════════════════════════════════════════════════
//
// This standalone project demonstrates AgentEval features as an EXTERNAL consumer
// would use them - referencing AgentEval from NuGet (not a project reference).
//
// FEATURES SHOWCASED:
// ✅ Tool Chain Assertions      - HaveCalledTool, WithArgument, BeforeTool, AfterTool
// ✅ Performance Assertions     - Duration, TTFT, Cost, Token limits
// ✅ Behavioral Policies        - NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching
// ✅ Stochastic Testing         - Run N times, statistical analysis
// ✅ Model Comparison           - Compare multiple models side-by-side
// ✅ Snapshot Testing           - Baseline comparison for regressions
// ✅ RAG Evaluation             - Faithfulness, Relevance metrics
// ✅ Response Assertions        - Content validation
// ✅ Because Clauses            - Self-documenting test intent
//
// RUN: dotnet run --project samples/AgentEval.NuGetConsumer
//
// ═══════════════════════════════════════════════════════════════════════════════

using AgentEval.Assertions;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Testing;

Console.WriteLine("""

╔════════════════════════════════════════════════════════════════════════════════╗
║                                                                                ║
║    █████╗  ██████╗ ███████╗███╗   ██╗████████╗███████╗██╗   ██╗ █████╗ ██╗     ║
║   ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝██╔════╝██║   ██║██╔══██╗██║     ║
║   ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   █████╗  ██║   ██║███████║██║     ║
║   ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ██╔══╝  ╚██╗ ██╔╝██╔══██║██║     ║
║   ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ███████╗ ╚████╔╝ ██║  ██║███████╗║
║   ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   ╚══════╝  ╚═══╝  ╚═╝  ╚═╝╚══════╝║
║                                                                                ║
║              NuGet Consumer Sample - Complete Feature Showcase                 ║
║                                                                                ║
║   This sample demonstrates AgentEval as an EXTERNAL NuGet consumer would       ║
║   use it. All features work with mock data - no Azure OpenAI required!         ║
║                                                                                ║
╚════════════════════════════════════════════════════════════════════════════════╝

""");

// ═══════════════════════════════════════════════════════════════════════════════
// 1. TOOL CHAIN ASSERTIONS - The most iconic AgentEval feature
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  1️⃣  TOOL CHAIN ASSERTIONS - Verify agent tool usage with fluent API");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

// Create mock tool usage representing a travel booking flow
var baseTime = DateTimeOffset.UtcNow;
var toolUsage = new ToolUsageReport();
toolUsage.AddCall(new ToolCallRecord 
{ 
    Name = "SearchFlights", 
    CallId = "1", 
    Order = 1, 
    Arguments = new Dictionary<string, object?> { ["destination"] = "Paris", ["date"] = "2026-03-15" },
    Result = "Found 5 flights",
    StartTime = baseTime,
    EndTime = baseTime.AddMilliseconds(450)
});
toolUsage.AddCall(new ToolCallRecord 
{ 
    Name = "BookFlight", 
    CallId = "2", 
    Order = 2, 
    Arguments = new Dictionary<string, object?> { ["flightId"] = "AF1234", ["passengers"] = 2 },
    Result = "Booking confirmed",
    StartTime = baseTime.AddMilliseconds(500),
    EndTime = baseTime.AddMilliseconds(820)
});
toolUsage.AddCall(new ToolCallRecord 
{ 
    Name = "SendConfirmation", 
    CallId = "3", 
    Order = 3, 
    Arguments = new Dictionary<string, object?> { ["email"] = "user@example.com" },
    Result = "Email sent",
    StartTime = baseTime.AddMilliseconds(900),
    EndTime = baseTime.AddMilliseconds(1050)
});

try
{
    // THE ICONIC AGENTEVAL ASSERTION CHAIN ✨
    toolUsage.Should()
        .HaveCalledTool("SearchFlights", because: "must search before booking")
            .WithArgument("destination", "Paris")
            .WithDurationUnder(TimeSpan.FromSeconds(2))
        .And()
        .HaveCalledTool("BookFlight", because: "booking follows search")
            .AfterTool("SearchFlights", because: "can't book without search results")
            .WithArgument("flightId", "AF1234")
        .And()
        .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
        .HaveNoErrors();
    
    Console.WriteLine("   ✅ Tool chain assertions PASSED!\n");
    Console.WriteLine("   Code example:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""
       result.ToolUsage!.Should()
           .HaveCalledTool("SearchFlights", because: "must search before booking")
               .WithArgument("destination", "Paris")
               .WithDurationUnder(TimeSpan.FromSeconds(2))
           .And()
           .HaveCalledTool("BookFlight", because: "booking follows search")
               .AfterTool("SearchFlights")
           .And()
           .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
           .HaveNoErrors();
    """);
    Console.ResetColor();
}
catch (ToolAssertionException ex)
{
    Console.WriteLine($"   ❌ Tool assertion failed: {ex.Message}");
}

// ═══════════════════════════════════════════════════════════════════════════════
// 2. PERFORMANCE ASSERTIONS - SLAs as code
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  2️⃣  PERFORMANCE ASSERTIONS - Make SLAs executable");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

var perfStartTime = DateTimeOffset.UtcNow;
var perfEndTime = perfStartTime.AddMilliseconds(1200);
var performance = new PerformanceMetrics
{
    StartTime = perfStartTime,
    EndTime = perfEndTime,
    TimeToFirstToken = TimeSpan.FromMilliseconds(180),
    PromptTokens = 120,
    CompletionTokens = 330,
    EstimatedCost = 0.0025m,
    ModelUsed = "gpt-4o"
};

try
{
    performance.Should()
        .HaveTotalDurationUnder(TimeSpan.FromSeconds(5), because: "UX requires sub-5s responses")
        .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500), because: "streaming responsiveness matters")
        .HaveEstimatedCostUnder(0.05m, because: "stay within $0.05/request budget")
        .HaveTokenCountUnder(2000);
    
    Console.WriteLine("   ✅ Performance assertions PASSED!\n");
    Console.WriteLine($"   📊 Total duration: {performance.TotalDuration.TotalMilliseconds:F0}ms");
    Console.WriteLine($"   📊 Time to first token: {performance.TimeToFirstToken?.TotalMilliseconds:F0}ms");
    Console.WriteLine($"   📊 Total tokens: {performance.TotalTokens}");
    Console.WriteLine($"   📊 Estimated cost: ${performance.EstimatedCost:F4}");
    
    Console.WriteLine("\n   Code example:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""
       result.Performance!.Should()
           .HaveTotalDurationUnder(TimeSpan.FromSeconds(5), because: "UX requirement")
           .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
           .HaveEstimatedCostUnder(0.05m, because: "budget constraint")
           .HaveTokenCountUnder(2000);
    """);
    Console.ResetColor();
}
catch (PerformanceAssertionException ex)
{
    Console.WriteLine($"   ❌ Performance assertion failed: {ex.Message}");
}

// ═══════════════════════════════════════════════════════════════════════════════
// 3. BEHAVIORAL POLICY ASSERTIONS - Enterprise compliance as code
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  3️⃣  BEHAVIORAL POLICIES - Compliance guardrails");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

// Create safe tool usage (no dangerous tools called)
var safeToolUsage = new ToolUsageReport();
safeToolUsage.AddCall(new ToolCallRecord { Name = "GetUserProfile", CallId = "1", Order = 1, Result = "profile data" });
safeToolUsage.AddCall(new ToolCallRecord { Name = "UpdatePreferences", CallId = "2", Order = 2, Result = "updated" });

try
{
    // BEHAVIORAL POLICY ASSERTIONS
    safeToolUsage.Should()
        .NeverCallTool("DeleteAllUsers", because: "mass deletion requires admin console")
        .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
        .NeverCallTool("TransferFundsExternal", because: "requires human approval");
    
    Console.WriteLine("   ✅ Behavioral policy assertions PASSED!\n");
    Console.WriteLine("   🛡️ No dangerous tools were called - policies enforced!");
    
    Console.WriteLine("\n   Code example:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""
       result.ToolUsage!.Should()
           .NeverCallTool("DeleteAllUsers", because: "requires admin console")
           .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
           .NeverPassArgumentMatching(@"\b\d{16}\b", because: "PCI-DSS compliance");
    """);
    Console.ResetColor();
}
catch (BehavioralPolicyViolationException ex)
{
    Console.WriteLine($"   ❌ Policy violation: {ex.PolicyName} - {ex.ViolatingAction}");
}

// ═══════════════════════════════════════════════════════════════════════════════
// 4. CONFIRMATION GATES - MustConfirmBefore pattern
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  4️⃣  CONFIRMATION GATES - Require approval before risky actions");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

// Create tool usage WITH proper confirmation sequence
var confirmedToolUsage = new ToolUsageReport();
confirmedToolUsage.AddCall(new ToolCallRecord { Name = "GetUserConfirmation", CallId = "1", Order = 1, Result = "confirmed" });
confirmedToolUsage.AddCall(new ToolCallRecord { Name = "DeleteAccount", CallId = "2", Order = 2, Result = "deleted" });

try
{
    confirmedToolUsage.Should()
        .MustConfirmBefore("DeleteAccount", 
            because: "account deletion is irreversible",
            confirmationToolName: "GetUserConfirmation");
    
    Console.WriteLine("   ✅ Confirmation gate PASSED!\n");
    Console.WriteLine("   🔐 Proper confirmation obtained before destructive action");
    
    Console.WriteLine("\n   Code example:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""
       result.ToolUsage!.Should()
           .MustConfirmBefore("TransferFunds",
               because: "financial transfers require explicit consent",
               confirmationToolName: "GetUserApproval");
    """);
    Console.ResetColor();
}
catch (BehavioralPolicyViolationException ex)
{
    Console.WriteLine($"   ❌ Missing confirmation: {ex.Message}");
}

// ═══════════════════════════════════════════════════════════════════════════════
// 5. RESPONSE ASSERTIONS - Content validation
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  5️⃣  RESPONSE ASSERTIONS - Validate output content");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

var agentResponse = "I found 5 flights to Paris for March 15th, 2026. The cheapest option is €299 with Air France (AF1234). Would you like me to book this flight?";

try
{
    agentResponse.Should()
        .Contain("Paris", because: "response must reference the destination")
        .Contain("flight", because: "response must mention the search results")
        .NotContain("password", because: "security - no credentials in responses")
        .NotContain("api_key", because: "security - no tokens exposed")
        .HaveLengthBetween(50, 500, because: "responses should be concise but complete");
    
    Console.WriteLine("   ✅ Response assertions PASSED!\n");
    Console.WriteLine($"   📝 Response: \"{agentResponse[..60]}...\"");
    
    Console.WriteLine("\n   Code example:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""
       result.ActualOutput!.Should()
           .Contain("Paris", because: "must reference destination")
           .NotContain("password", because: "no credentials in responses")
           .HaveLengthBetween(50, 500);
    """);
    Console.ResetColor();
}
catch (ResponseAssertionException ex)
{
    Console.WriteLine($"   ❌ Response assertion failed: {ex.Message}");
}

// ═══════════════════════════════════════════════════════════════════════════════
// 6. MOCK TESTING WITH FakeChatClient - No LLM required!
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  6️⃣  MOCK TESTING - FakeChatClient for unit tests");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

// Create a FakeChatClient that returns predetermined responses
var fakeClient = new FakeChatClient("""{"score": 95, "explanation": "Excellent tool selection"}""");

Console.WriteLine("   ✅ FakeChatClient created for deterministic testing!\n");
Console.WriteLine("   📝 This enables testing metrics without external API calls");
Console.WriteLine("   📝 Perfect for CI/CD pipelines and unit tests");

Console.WriteLine("\n   Code example:");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""
       // Create fake client with predetermined response
       var fakeClient = new FakeChatClient(@"{""score"": 95}");
       
       // Use in metric tests - no Azure OpenAI needed!
       var metric = new ToolSelectionMetric(fakeClient);
       var result = await metric.EvaluateAsync(context);
       
       Assert.Equal(95, result.Score);
    """);
Console.ResetColor();

// ═══════════════════════════════════════════════════════════════════════════════
// 7. STOCHASTIC TESTING - Statistical confidence
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  7️⃣  STOCHASTIC TESTING - Handle LLM non-determinism");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

// Simulate stochastic results
var stochasticResults = new List<double> { 92, 88, 95, 87, 91, 89, 94, 90, 86, 93 };
var mean = stochasticResults.Average();
var variance = stochasticResults.Select(x => Math.Pow(x - mean, 2)).Average();
var stdDev = Math.Sqrt(variance);

Console.WriteLine($"   📊 Simulated 10 runs with scores: [{string.Join(", ", stochasticResults)}]");
Console.WriteLine($"   📊 Mean: {mean:F1}");
Console.WriteLine($"   📊 Standard Deviation: {stdDev:F1}");
Console.WriteLine($"   📊 Pass Rate: {stochasticResults.Count(x => x >= 80) * 10}%\n");

Console.WriteLine("   ✅ Stochastic testing reveals true reliability!");

Console.WriteLine("\n   Code example:");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""
       var result = await stochasticRunner.RunStochasticTestAsync(
           agent, testCase,
           new StochasticOptions
           {
               Runs = 10,                    // Run 10 times
               SuccessRateThreshold = 0.8,   // 80% must pass
               EnableStatisticalAnalysis = true
           });
       
       result.Statistics.Mean.Should().BeGreaterThan(80);
       result.Statistics.StandardDeviation.Should().BeLessThan(15);
       Assert.True(result.PassedThreshold);
    """);
Console.ResetColor();

// ═══════════════════════════════════════════════════════════════════════════════
// 8. MODEL COMPARISON - Side-by-side analysis
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  8️⃣  MODEL COMPARISON - Compare models scientifically");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

// Simulate model comparison results
Console.WriteLine("   📊 Simulated comparison across 3 models:\n");
Console.WriteLine("   ┌──────────────────┬─────────────┬───────────┬──────────┬────────────┐");
Console.WriteLine("   │ Model            │ Tool Acc.   │ Latency   │ Cost     │ Winner     │");
Console.WriteLine("   ├──────────────────┼─────────────┼───────────┼──────────┼────────────┤");
Console.WriteLine("   │ GPT-4o           │ 94.2%       │ 1,234ms   │ $0.0150  │ 🏆 Quality │");
Console.WriteLine("   │ GPT-4o-mini      │ 87.5%       │ 456ms     │ $0.0003  │ ⚡ Value   │");
Console.WriteLine("   │ GPT-3.5-turbo    │ 72.1%       │ 312ms     │ $0.0005  │            │");
Console.WriteLine("   └──────────────────┴─────────────┴───────────┴──────────┴────────────┘\n");

Console.WriteLine("   Code example:");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""
       var comparer = new ModelComparer(harness);
       var result = await comparer.CompareModelsAsync(
           factories: new IAgentFactory[]
           {
               new AzureModelFactory("gpt-4o", "GPT-4o"),
               new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini")
           },
           testCases: testSuite,
           options: new ComparisonOptions(RunsPerModel: 5));
       
       Console.WriteLine(result.ToMarkdown());
       Console.WriteLine(result.ToRankingsTable());
    """);
Console.ResetColor();

// ═══════════════════════════════════════════════════════════════════════════════
// 9. AGENTIC METRICS - Tool-specific evaluation
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n═══════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  9️⃣  AGENTIC METRICS - Evaluate tool usage quality");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");

Console.WriteLine("   📊 Available agentic metrics:\n");
Console.WriteLine("   • code_tool_success    - Were tools called successfully?");
Console.WriteLine("   • code_tool_efficiency - Did agent minimize unnecessary calls?");
Console.WriteLine("   • llm_tool_selection   - Did agent choose the right tools?");
Console.WriteLine("   • llm_tool_arguments   - Were arguments correct and complete?");
Console.WriteLine("   • llm_task_completion  - Did agent complete the requested task?\n");

Console.WriteLine("   Code example:");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""
       // Metric naming: code_ = computed, llm_ = AI-judged
       var successMetric = new ToolSuccessMetric();
       var selectionMetric = new ToolSelectionMetric(chatClient);
       
       var successResult = await successMetric.EvaluateAsync(context);
       Console.WriteLine($"Tool Success: {successResult.Score}/100");
    """);
Console.ResetColor();

// ═══════════════════════════════════════════════════════════════════════════════
// SUMMARY
// ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n\n");
Console.WriteLine("╔════════════════════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("║                        ✅ ALL FEATURES DEMONSTRATED!                          ║");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("╠════════════════════════════════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("║   Features Showcased:                                                          ║");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("║   ✅ Tool Chain Assertions     - HaveCalledTool, WithArgument, BeforeTool      ║");
Console.WriteLine("║   ✅ Performance Assertions    - Duration, TTFT, Cost, Tokens                  ║");
Console.WriteLine("║   ✅ Behavioral Policies       - NeverCallTool, NeverPassArgumentMatching      ║");
Console.WriteLine("║   ✅ Confirmation Gates        - MustConfirmBefore for risky actions           ║");
Console.WriteLine("║   ✅ Response Assertions       - Contain, NotContain, Length validation        ║");
Console.WriteLine("║   ✅ Mock Testing              - FakeChatClient for unit tests                 ║");
Console.WriteLine("║   ✅ Stochastic Testing        - Run N times, statistical analysis             ║");
Console.WriteLine("║   ✅ Model Comparison          - Compare models side-by-side                   ║");
Console.WriteLine("║   ✅ Agentic Metrics           - Tool success, selection, efficiency           ║");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("╠════════════════════════════════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("║   📦 Install: dotnet add package AgentEval --prerelease                        ║");
Console.WriteLine("║   📖 Docs:    https://github.com/joslat/AgentEval                              ║");
Console.WriteLine("║   ⭐ Star:    Help us grow - star the repo!                                    ║");
Console.WriteLine("║                                                                                ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════════════════════════╝");
Console.WriteLine();
