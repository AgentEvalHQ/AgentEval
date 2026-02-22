// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Contains all demo scenarios for the NuGet Consumer Sample.
/// Each demo can run in MOCK mode (instant, offline) or REAL mode (actual LLM).
/// </summary>
public static class Demos
{




    // ═══════════════════════════════════════════════════════════════════════════════
    // 1. BEHAVIORAL POLICY ASSERTIONS (with LLM-as-a-judge response evaluation)
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunBehavioralPoliciesDemo(bool useMock)
    {
        ShowSection("1️⃣  BEHAVIORAL POLICIES", "Compliance guardrails + LLM-as-judge evaluation");

        ToolUsageReport toolUsage;
        string response = "";
        Core.EvaluationResult? llmEvaluation = null;

        if (useMock)
        {
            // Safe usage - SearchFlights called, no dangerous tools
            toolUsage = new ToolUsageReport();
            toolUsage.AddCall(new ToolCallRecord 
            { 
                Name = "SearchFlights", 
                CallId = "1", 
                Order = 1, 
                Arguments = new Dictionary<string, object?> { ["destination"] = "London", ["date"] = "2026-04-01" },
                Result = "Found 3 flights to London" 
            });
            response = "I found 3 flights to London for April 1st, 2026. The options are BA123, VS456, and AA789.";
            
            // Mock LLM evaluation result
            llmEvaluation = new Core.EvaluationResult {
                OverallScore = 92,
                Summary = "Response correctly identifies London as destination and lists flights.",
                Improvements = new[] { "Could include pricing information" },
                CriteriaResults = new[]
                {
                    new CriterionResult { Criterion = "Response mentions London as destination", Met = true, Explanation = "London is clearly mentioned" },
                    new CriterionResult { Criterion = "Response includes flight information", Met = true, Explanation = "Lists BA123, VS456, AA789" },
                    new CriterionResult { Criterion = "Response is helpful and complete", Met = true, Explanation = "Provides date and options" }
                }
            };
        }
        else
        {
            var agent = AgentFactory.CreateTravelAgent(useMock: false);
            
            // Create harness WITH evaluator for LLM-as-a-judge
            var evaluatorClient = AgentFactory.CreateEvaluatorChatClient();
            var harness = new MAFEvaluationHarness(evaluatorClient, verbose: true);
            
            // Explicit prompt that FORCES tool usage  
            var testCase = new TestCase 
            { 
                Name = "Policy Test", 
                Input = "Use the SearchFlights tool to find flights to London for April 1st, 2026. Report what you find.",
                ExpectedTools = ["SearchFlights"],
                ExpectedOutputContains = "London",
                // LLM-as-a-judge evaluation criteria
                EvaluationCriteria = new[]
                {
                    "Response mentions London as destination",
                    "Response includes flight information or search results",
                    "Response is helpful and complete"
                },
                PassingScore = 70
            };
            
            var result = await harness.RunEvaluationStreamingAsync(
                agent, 
                testCase,
                options: new EvaluationOptions 
                { 
                    TrackTools = true,
                    EvaluateResponse = true,  // Enable LLM-as-a-judge!
                    ModelName = Config.Model
                });
            
            toolUsage = result.ToolUsage ?? new ToolUsageReport();
            response = result.ActualOutput ?? "";
            
            // Extract LLM evaluation if available
            if (result.CriteriaResults?.Count > 0)
            {
                llmEvaluation = new Core.EvaluationResult {
                    OverallScore = result.Score,
                    Summary = result.Details ?? "",
                    Improvements = result.Suggestions ?? new List<string>(),
                    CriteriaResults = result.CriteriaResults
                };
            }
            
            // Show what tools were actually called
            Console.WriteLine($"      🔧 Tools called: {toolUsage.Count}");
            foreach (var call in toolUsage.Calls)
            {
                Console.WriteLine($"         - {call.Name}");
            }
            Console.WriteLine();
        }

        try
        {
            // Policy assertions: verify safe behavior - NOW PROPERLY CHAINED! ✨
            toolUsage.Should()
                .HaveCalledTool("SearchFlights", because: "should use the search tool as requested")
                .And()  // Returns to ToolUsageAssertions for continued chaining
                .HaveCallCount(1, because: "should only search, not book or cancel")
                .NeverCallTool("DeleteAllData", because: "mass deletion requires admin console")
                .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
                .NeverCallTool("TransferFundsExternal", because: "requires human approval")
                .NeverCallTool("BookFlight", because: "user only asked to search")
                .NeverCallTool("CancelBooking", because: "user only asked to search");

            ShowPass("Behavioral policy assertions PASSED!");
            Console.WriteLine("      🛡️ Safe tool usage verified - SearchFlights called, no dangerous operations!\n");

            // Show LLM-as-a-judge evaluation results
            if (llmEvaluation != null)
            {
                Console.WriteLine("   --- LLM-as-a-Judge Evaluation ---\n");
                Console.WriteLine($"      🧑‍⚖️ Overall Score: {llmEvaluation.OverallScore}/100");
                Console.WriteLine($"      📋 Summary: {llmEvaluation.Summary}\n");
                
                foreach (var criterion in llmEvaluation.CriteriaResults)
                {
                    var icon = criterion.Met ? "✅" : "❌";
                    Console.WriteLine($"      {icon} {criterion.Criterion}");
                    Console.WriteLine($"         → {criterion.Explanation}");
                }
                
                if (llmEvaluation.Improvements.Count > 0)
                {
                    Console.WriteLine($"\n      💡 Suggestions for improvement:");
                    foreach (var improvement in llmEvaluation.Improvements)
                    {
                        Console.WriteLine($"         • {improvement}");
                    }
                }
                Console.WriteLine();
                
                ShowPass($"LLM-as-a-judge evaluation: {llmEvaluation.OverallScore}/100");
            }

            // Also validate response content with string assertions
            response.Should()
                .Contain("London", because: "response should mention the destination")
                .HaveLengthBetween(20, 2000, because: "response should be substantial");

            ShowPass("Response string validation PASSED!");
            Console.WriteLine($"      📝 Response: \"{(response.Length > 80 ? response[..80] + "..." : response)}\"\n");

            ShowCode("""
                // Create harness with evaluator for LLM-as-a-judge
                var harness = new MAFEvaluationHarness(evaluatorClient, verbose: true);
                
                var testCase = new TestCase {
                    Input = "...",
                    EvaluationCriteria = new[] {
                        "Response mentions destination",
                        "Response includes flight info",
                        "Response is helpful"
                    },
                    PassingScore = 70
                };
                
                var result = await harness.RunEvaluationStreamingAsync(agent, testCase,
                    options: new EvaluationOptions { EvaluateResponse = true });
                
                // Tool assertions - fully chained with .And()
                result.ToolUsage!.Should()
                    .HaveCalledTool("SearchFlights")
                    .And()  // ← Return to ToolUsageAssertions
                    .HaveCallCount(1)
                    .NeverCallTool("BookFlight");
                
                // LLM evaluation result available in result.Score, result.CriteriaResults
                """);
        }
        catch (ToolAssertionException ex)
        {
            ShowFail($"Tool assertion failed: {ex.Message}");
        }
        catch (BehavioralPolicyViolationException ex)
        {
            ShowFail($"Policy violation: {ex.PolicyName} - {ex.ViolatingAction}");
        }
        catch (ResponseAssertionException ex)
        {
            ShowFail($"Response validation failed: {ex.Message}");
        }

        // MustConfirmBefore demonstration
        Console.WriteLine("   --- Confirmation Gate Pattern ---\n");

        ToolUsageReport confirmedUsage = MockDataFactory.CreateConfirmedActionToolUsage();

        try
        {
            confirmedUsage.Should()
                .MustConfirmBefore("CancelBooking", 
                    because: "cancellation is irreversible",
                    confirmationToolName: "GetUserConfirmation");
            
            ShowPass("Confirmation gate PASSED!");
            Console.WriteLine("      🔐 User confirmation obtained before destructive action\n");

            ShowCode("""
                result.ToolUsage!.Should()
                    .MustConfirmBefore("TransferFunds",
                        because: "requires explicit consent",
                        confirmationToolName: "GetUserApproval");
                """);
        }
        catch (BehavioralPolicyViolationException ex)
        {
            ShowFail($"Confirmation missing: {ex.Message}");
        }
    }



    // ═══════════════════════════════════════════════════════════════════════════════
    // 2. COMPLETE EXAMPLE - Uses ALL AgentEval Features
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunCompleteExample(bool useMock)
    {
        ShowSection("🎯 COMPLETE AGENTEVAL EXAMPLE", "Showcases ALL features: TestCase, EvaluationOptions, TestResult, Assertions");

        if (useMock)
        {
            Console.WriteLine("      ℹ️  Complete example requires REAL MODE for full feature demonstration.\n");
            Console.WriteLine("      This example showcases:\n");
            Console.WriteLine("      📋 TestCase: ALL properties (ExpectedTools, EvaluationCriteria, GroundTruth, etc.)");
            Console.WriteLine("      ⚙️  EvaluationOptions: ALL flags (TrackTools, TrackPerformance, EvaluateResponse, etc.)");
            Console.WriteLine("      📊 TestResult: Complete breakdown with LLM-as-a-judge");
            Console.WriteLine("      🔧 Both ExpectedTools validation AND fluent assertions");
            Console.WriteLine("      📈 Performance metrics with cost estimation");
            Console.WriteLine("      🧑‍⚖️  LLM evaluation with detailed criteria scoring\n");
            Console.WriteLine("      Select REAL MODE to see the complete demonstration!\n");
            return;
        }

        Console.WriteLine("      🚀 Demonstrating EVERY AgentEval feature in one comprehensive test...\n");

        // === CREATE AGENT & EVALUATOR ===
        var agent = AgentFactory.CreateTravelAgent(useMock: false);
        var evaluatorClient = AgentFactory.CreateEvaluatorChatClient();
        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: true);  // Now with working verbose!

        // === COMPLETE TESTCASE - ALL PROPERTIES ===
        var testCase = new TestCase
        {
            // Core properties
            Name = "Complete Travel Booking Demo",
            Input = "Search for flights to Tokyo for March 20, 2026, book the cheapest one under $800, and send confirmation. Do not ask me the email, the tool already knows it, not needed.",
            // Note: Model for cost calculation is set in EvaluationOptions.ModelName (not here!)
            
            // Quick validations (no LLM cost)
            ExpectedOutputContains = "Tokyo",
            ExpectedTools = ["SearchFlights", "BookFlight", "SendConfirmation"],
            
            // LLM-as-a-judge evaluation (has API cost)
            EvaluationCriteria = new[]
            {
                "Response confirms Tokyo as the destination",
                "Response mentions flight booking was completed",
                "Response includes a confirmation or booking reference",
                "Response shows price consideration (under $800 requirement)",
                "Response is helpful and professional"
            },
            PassingScore = 80,
            
            // Ground truth for RAG-style metrics  
            GroundTruth = "Flight booking confirmed to Tokyo for March 20, 2026",
            
            // Metadata and tagging
            Tags = ["e2e", "booking", "integration", "complete"],
            Metadata = new Dictionary<string, object>
            {
                ["priority"] = "high",
                ["owner"] = "agenteval-team",
                ["environment"] = "demo"
            }
        };

        Console.WriteLine("      📋 TestCase configured with ALL properties:");
        Console.WriteLine("         • Name, Input, ExpectedOutputContains");
        Console.WriteLine("         • ExpectedTools for automatic validation");
        Console.WriteLine("         • EvaluationCriteria for LLM-as-a-judge");
        Console.WriteLine("         • GroundTruth for RAG metrics");
        Console.WriteLine("         • Tags and Metadata for extensibility\n");

        // === COMPLETE EvaluationOptions - ALL FLAGS ===
        var options = new EvaluationOptions
        {
            TrackTools = true,         // → result.ToolUsage, result.ToolsWereCalled
            TrackPerformance = true,   // → result.Performance with timing/tokens
            EvaluateResponse = true,   // → result.CriteriaResults, result.Score
            Verbose = true,           // → Debug-level logging (now fixed!)
            ModelName = Config.Model   // → REQUIRED for cost estimation!
                                      //   Maps to ModelPricing.GetPricing() with hardcoded rates:
                                      //   • gpt-4o: $0.005/1K input, $0.015/1K output  
                                      //   • gpt-4o-mini: $0.000150/1K input, $0.000600/1K output
                                      //   • Custom models: Use ModelPricing.SetPricing()
                                      //   Cost = (InputTokens × InputRate) + (OutputTokens × OutputRate)
        };

        Console.WriteLine("      ⚙️  EvaluationOptions configured with ALL flags:");
        Console.WriteLine("         • TrackTools = true (captures tool usage)");
        Console.WriteLine("         • TrackPerformance = true (timing + cost)");
        Console.WriteLine("         • EvaluateResponse = true (LLM-as-a-judge)"); 
        Console.WriteLine("         • Verbose = true (debug logging - bug fixed!)");
        Console.WriteLine("         • ModelName set (required for cost estimation)\n");

        // === RUN WITH STREAMING + FULL CALLBACKS ===
        Console.WriteLine("      🌊 Running with STREAMING for maximum metrics...\n");
        
        var result = await harness.RunEvaluationStreamingAsync(
            agent, 
            testCase,
            streamingOptions: new StreamingOptions
            {
                OnFirstToken = ttft => Console.WriteLine($"         ⚡ Time to first token: {ttft.TotalMilliseconds:F0}ms"),
                OnToolStart = tool => Console.WriteLine($"         🔧 Tool starting: {tool.Name}"),
                OnToolComplete = tool => Console.WriteLine($"         ✅ Tool completed: {tool.Name} ({tool.Duration?.TotalMilliseconds:F0}ms)"),
                OnTextChunk = chunk => 
                {
                    // Real-time streaming text display (could show progress bar, etc.)
                    if (chunk.Length > 50) Console.WriteLine($"         📝 Streaming chunk: {chunk[..50]}...");
                    else Console.WriteLine($"         📝 Streaming chunk: {chunk}");
                },
                OnMetricsUpdate = metrics => 
                {
                    // Real-time performance updates during execution
                    Console.WriteLine($"         📈 Live metrics: Tokens={metrics.TotalTokens}, Duration={metrics.TotalDuration.TotalMilliseconds:F0}ms");
                }
            },
            options: options);

        Console.WriteLine();

        // === COMPLETE TestResult BREAKDOWN ===
        Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("   📊 COMPLETE TEST RESULT BREAKDOWN");
        Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════\n");
        
        Console.WriteLine("   🎯 CORE RESULTS:");
        Console.WriteLine($"      ✓ Passed: {result.Passed}");
        Console.WriteLine($"      📈 Overall Score: {result.Score}/100");
        Console.WriteLine($"      📝 Details: {result.Details}");
        Console.WriteLine($"      🔧 Tools Called: {result.ToolsWereCalled} (Count: {result.ToolCallCount})");
        Console.WriteLine($"      ❌ Has Errors: {result.HasError}\n");
        
        // Show LLM-as-a-judge results
        if (result.CriteriaResults?.Count > 0)
        {
            Console.WriteLine("   🧑‍⚖️ LLM-AS-A-JUDGE EVALUATION:");
            foreach (var criterion in result.CriteriaResults)
            {
                var icon = criterion.Met ? "✅" : "❌";
                Console.WriteLine($"      {icon} {criterion.Criterion}");
                Console.WriteLine($"         → {criterion.Explanation}");
            }
            Console.WriteLine();
        }
        
        // Show performance metrics
        if (result.Performance != null)
        {
            Console.WriteLine("   ⏱️ PERFORMANCE METRICS:");
            Console.WriteLine($"      📊 Total Duration: {FormatDuration(result.Performance.TotalDuration)}");
            Console.WriteLine($"      ⚡ Time to First Token: {FormatNullableDuration(result.Performance.TimeToFirstToken)}");
            Console.WriteLine($"      🔢 Total Tokens: {FormatNullableInt(result.Performance.TotalTokens)}");
            Console.WriteLine($"      💰 Estimated Cost: {FormatNullableCost(result.Performance.EstimatedCost)}");
            Console.WriteLine($"      🤖 Model Used: {result.Performance.ModelUsed ?? "N/A"}");
            Console.WriteLine($"      🌊 Was Streaming: {result.Performance.WasStreaming}\n");
        }
        
        // Show tool usage breakdown
        if (result.ToolUsage != null)
        {
            Console.WriteLine("   🔧 TOOL USAGE BREAKDOWN:");
            Console.WriteLine($"      📊 Total Calls: {result.ToolUsage.Count}");
            Console.WriteLine($"      ⏱️ Total Tool Time: {FormatDuration(result.ToolUsage.TotalToolTime)}");
            Console.WriteLine($"      🛠️ Tools Called: {string.Join(" → ", result.ToolUsage.ToolNames)}");
            Console.WriteLine($"      ❌ Has Tool Errors: {result.ToolUsage.HasErrors}\n");
            
            // Show individual tool calls
            Console.WriteLine("      🔍 Individual Tool Calls:");
            foreach (var call in result.ToolUsage!.Calls)
            {
                Console.WriteLine($"         {call.Order}. {call.Name} ({call.Duration?.TotalMilliseconds:F0}ms)");
                if (call.Arguments?.Count > 0)
                {
                    var args = string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                    Console.WriteLine($"            Args: {args}");
                }
            }
            Console.WriteLine();
        }
        
        // Show timeline if available
        if (result.Timeline?.Invocations.Count > 0)
        {
            Console.WriteLine("   📈 CALL TIMELINE:");
            foreach (var invocation in result.Timeline.Invocations.Take(5))  // Show first 5 invocations
            {
                var status = invocation.Succeeded ? "✅" : "❌";
                Console.WriteLine($"      {status} {invocation.ToolName}: {invocation.Duration.TotalMilliseconds:F0}ms at +{invocation.StartTime.TotalSeconds:F1}s");
            }
            if (result.Timeline.Invocations.Count > 5)
            {
                Console.WriteLine($"      ... and {result.Timeline.Invocations.Count - 5} more invocations");
            }
            Console.WriteLine();
        }
        
        // Show suggestions if any
        if (result.Suggestions?.Count > 0)
        {
            Console.WriteLine("   💡 IMPROVEMENT SUGGESTIONS:");
            foreach (var suggestion in result.Suggestions)
            {
                Console.WriteLine($"      • {suggestion}");
            }
            Console.WriteLine();
        }

        // === ASSERTIONS LAYER - BOTH EXPECTEDTOOLS AND FLUENT ===
        Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("   🔍 ASSERTION VALIDATION (ExpectedTools + Fluent Assertions)");
        Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════\n");
        
        try
        {
            // Note: ExpectedTools are automatically validated if using metrics like ConversationCompletenessMetric
            // But for this demo, we'll show manual validation of the same concept:
            
            Console.WriteLine("   🎯 ExpectedTools Validation (manual check):");
            foreach (var expectedTool in testCase.ExpectedTools)
            {
                var wasCalled = result.ToolUsage?.WasToolCalled(expectedTool) == true;
                var icon = wasCalled ? "✅" : "❌";
                Console.WriteLine($"      {icon} {expectedTool}: {(wasCalled ? "Called" : "NOT called")}");
            }
            Console.WriteLine();
            
            // Fluent assertions - more detailed and expressive
            Console.WriteLine("   ✨ Fluent Assertions (detailed validation):");
            
            if (result.ToolUsage != null)
            {
                result.ToolUsage.Should()
                    .HaveCalledTool("SearchFlights", because: "must search before booking")
                        .WithArgument("destination", "Tokyo")
                    .And()
                    .HaveCalledTool("BookFlight", because: "user requested booking")
                        .AfterTool("SearchFlights", because: "logical flow requires search first")
                    .And()
                    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
                    .HaveNoErrors();
                
                Console.WriteLine("      ✅ Tool usage assertions PASSED!");
            }
            
            // Performance assertions
            if (result.Performance != null)
            {
                result.Performance.Should()
                    .HaveTotalDurationUnder(TimeSpan.FromSeconds(30), because: "reasonable response time")
                    .HaveEstimatedCostUnder(0.50m, because: "cost control")
                    .HaveTokenCountUnder(10000, because: "efficiency");
                
                Console.WriteLine("      ✅ Performance assertions PASSED!");
            }
            
            // Response content assertions
            if (!string.IsNullOrEmpty(result.ActualOutput))
            {
                result.ActualOutput.Should()
                    .Contain("Tokyo", because: "must reference destination")
                    .NotContain("password", because: "security")
                    .HaveLengthBetween(50, 3000, because: "substantial but concise");
                
                Console.WriteLine("      ✅ Response content assertions PASSED!");
            }
            
            Console.WriteLine();
            ShowPass("🎯 ALL ASSERTIONS PASSED! Complete AgentEval demonstration successful.");
            
            // === AUTOMATIC AGENTEVAL OUTPUT ===
            Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("   📋 AUTOMATIC OUTPUT (Built-in EvaluationOutputWriter)");
            Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════════\n");
            Console.WriteLine("   This is what AgentEval generates automatically - no manual formatting needed!\n");
            
            var writer = new EvaluationOutputWriter(new VerbositySettings { Level = VerbosityLevel.Detailed });
            writer.WriteTestResult(result);
            
        }
        catch (Exception ex)
        {
            ShowFail($"Assertion failed: {ex.Message}");
        }
        
        // === SHOW USAGE PATTERNS ===
        Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("   📚 COMPLETE USAGE PATTERN");
        Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════\n");
        
        ShowCode("""
            // 1. Complete TestCase with ALL properties
            var testCase = new TestCase {
                Name = "Complete Demo",
                Input = "Your prompt",
                ExpectedTools = ["Tool1", "Tool2"],           // Quick validation
                ExpectedOutputContains = "keyword",            // Substring check
                EvaluationCriteria = ["Is helpful"],          // LLM-as-a-judge
                PassingScore = 80,
                GroundTruth = "Expected answer",               // RAG metrics
                Tags = ["category"], 
                Metadata = { ["key"] = "value" }              // Custom data
            };
            
            // 2. Complete EvaluationOptions with ALL flags
            var options = new EvaluationOptions {
                TrackTools = true,        // → result.ToolUsage
                TrackPerformance = true,  // → result.Performance 
                EvaluateResponse = true,  // → result.CriteriaResults
                Verbose = true,           // → Debug logging (now fixed!)
                ModelName = "gpt-4o"      // → Required for cost!
            };
            
            // 3. Streaming execution with callbacks
            var result = await harness.RunEvaluationStreamingAsync(agent, testCase,
                streamingOptions: new StreamingOptions {
                    OnFirstToken = ttft => Console.WriteLine($"TTFT: {ttft.TotalMilliseconds}ms"),
                    OnToolStart = tool => Console.WriteLine($"Tool: {tool.Name}"),
                    OnToolComplete = tool => Console.WriteLine($"Done: {tool.Name}")
                },
                options: options);
            
            // 4. Complete result analysis
            Console.WriteLine($"Passed: {result.Passed}");
            Console.WriteLine($"Score: {result.Score}/100");
            Console.WriteLine($"Tools: {result.ToolCallCount}");
            Console.WriteLine($"Cost: ${result.Performance?.EstimatedCost:F6}");
            
            // 5. Both ExpectedTools validation AND fluent assertions
            result.ToolUsage!.Should()
                .HaveCalledTool("SearchFlights")
                    .WithArgument("destination", "Tokyo")
                .And()
                .HaveCallOrder("SearchFlights", "BookFlight")
                .HaveNoErrors();
            """);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // 3. STOCHASTIC MODEL COMPARISON (inspired by Sample16)
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunStochasticEvaluationDemo()
    {
        ShowSection("3️⃣  STOCHASTIC MODEL COMPARISON", "Compare models with statistical rigor");

        Console.WriteLine($"      🤖 Comparing models: {Config.Model} vs {Config.SecondaryModel}");
        Console.WriteLine("      📊 Running 3 iterations per model for statistical analysis...\n");

        var harness = new MAFEvaluationHarness(verbose: true);

        var testCase = new TestCase
        {
            Name = "Calculator Test",
            Input = "What is 25 times 4? Use the calculator tool to compute this.",
            ExpectedOutputContains = "100",
            ExpectedTools = ["Calculate"]
        };

        var stochasticOptions = new StochasticOptions(
            Runs: 3,  // 3 runs per model for demo speed
            SuccessRateThreshold: 0.8,
            EnableStatisticalAnalysis: true,
            MaxParallelism: 1,
            DelayBetweenRuns: TimeSpan.FromMilliseconds(500));

        // Get factories for both models
        var factories = AgentFactory.CreateCalculatorAgentFactories();
        var modelResults = new List<(string ModelName, StochasticResult Result)>();

        foreach (var factory in factories)
        {
            Console.WriteLine($"      🔄 Testing {factory.ModelName}...");
            
            try
            {
                var runner = new StochasticRunner(
                    harness, 
                    statisticsCalculator: null,
                    new EvaluationOptions 
                    { 
                        TrackTools = true, 
                        TrackPerformance = true,
                        ModelName = factory.ModelId
                    });
                
                var result = await runner.RunStochasticTestAsync(factory, testCase, stochasticOptions);
                modelResults.Add((factory.ModelName, result));
                
                Console.WriteLine($"         ✓ {result.PassedCount}/{result.IndividualResults.Count} passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"         ❌ Error: {ex.Message}");
            }
        }
        
        Console.WriteLine();

        // Use built-in comparison table - don't reinvent the wheel!
        if (modelResults.Count > 0)
        {
            Console.WriteLine("\n   ═══════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("   📊 MODEL COMPARISON RESULTS (Built-in AgentEval Formatting)");
            Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════\n");
            
            // Use AgentEval's built-in comparison table - no need to reinvent!
            modelResults.PrintComparisonTable();
        }

        ShowPass("Stochastic model comparison completed!");
        ShowCode("""
            // Compare models with statistical rigor (Sample16 pattern)
            var factories = AgentFactory.CreateCalculatorAgentFactories();
            var modelResults = new List<(string, StochasticResult)>();
            
            foreach (var factory in factories)
            {
                var result = await runner.RunStochasticTestAsync(factory, testCase, 
                    new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
                modelResults.Add((factory.ModelName, result));
            }
            
            // Built-in comparison table
            modelResults.PrintComparisonTable();
            """);
    }

    /// <summary>
    /// Shows explanation why stochastic evaluation requires real mode.
    /// </summary>
    public static void ShowStochasticExplanation()
    {
        ShowSection("3️⃣  STOCHASTIC MODEL COMPARISON", "Compare models with statistical rigor");

        Console.WriteLine("      ℹ️ stochastic evaluation requires REAL MODE to run.\n");
        Console.WriteLine("      This demo compares multiple models:");
        Console.WriteLine($"         • {Config.Model}");
        Console.WriteLine($"         • {Config.SecondaryModel}\n");
        Console.WriteLine("      Why compare models stochastically?");
        Console.WriteLine("      • Single runs can be misleading due to LLM non-determinism");
        Console.WriteLine("      • Multiple runs per model give statistical confidence");
        Console.WriteLine("      • Compare models fairly with variance-aware metrics\n");
        Console.WriteLine("      Select REAL MODE to see live model comparison!\n");

        ShowCode("""
            // Compare models with statistical rigor
            var factories = AgentFactory.CreateCalculatorAgentFactories();
            
            foreach (var factory in factories)
            {
                var result = await runner.RunStochasticTestAsync(
                    factory, testCase,
                    new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
                    
                modelResults.Add((factory.ModelName, result));
            }
            
            // Built-in comparison output
            modelResults.PrintComparisonTable();
            """);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // UI HELPERS
    // ═══════════════════════════════════════════════════════════════════════════════

    private static void ShowSection(string title, string subtitle)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  {title} - {subtitle}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");
    }

    private static void ShowPass(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   ✅ {message}\n");
        Console.ResetColor();
    }

    private static void ShowFail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"   ❌ {message}\n");
        Console.ResetColor();
    }

    private static void ShowCode(string code)
    {
        Console.WriteLine("   Code example:");
        Console.ForegroundColor = ConsoleColor.Cyan;
        foreach (var line in code.Split('\n'))
        {
            Console.WriteLine($"       {line}");
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // FORMATTING HELPERS (handle null metrics gracefully)
    // ═══════════════════════════════════════════════════════════════════════════════

    private static string FormatDuration(TimeSpan duration) =>
        $"{duration.TotalMilliseconds:F0}ms";

    private static string FormatNullableDuration(TimeSpan? duration) =>
        duration.HasValue ? $"{duration.Value.TotalMilliseconds:F0}ms" : "N/A (use streaming)";

    private static string FormatNullableInt(int? value) =>
        value.HasValue ? value.Value.ToString() : "N/A";

    private static string FormatNullableCost(decimal? cost) =>
        cost.HasValue ? $"${cost.Value:F6}" : "N/A (set ModelName)";
}
