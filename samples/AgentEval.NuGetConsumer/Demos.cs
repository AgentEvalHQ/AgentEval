// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

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
    // 1. TOOL CHAIN ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunToolAssertionsDemo(bool useMock)
    {
        ShowSection("1️⃣  TOOL CHAIN ASSERTIONS", "Verify agent tool usage with fluent API");

        ToolUsageReport toolUsage;
        string response;

        if (useMock)
        {
            // MOCK: Use pre-built test data
            toolUsage = MockDataFactory.CreateTravelBookingToolUsage();
            response = MockDataFactory.CreateAgentResponse();
        }
        else
        {
            // REAL: Execute actual agent with STREAMING for full metrics
            var agent = AgentFactory.CreateTravelAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: true);
            var testCase = new TestCase
            {
                Name = "Travel Booking Flow",
                Input = "Search for flights to Paris on March 15th, 2026, book the cheapest one, and send confirmation to user@example.com"
            };
            
            // Use streaming to capture TTFT and per-tool timing
            var result = await harness.RunTestStreamingAsync(
                agent, 
                testCase,
                streamingOptions: new StreamingOptions
                {
                    OnFirstToken = ttft => Console.WriteLine($"      ⚡ First token: {ttft.TotalMilliseconds:F0}ms"),
                    OnToolStart = tool => Console.WriteLine($"      🔧 Tool started: {tool.Name}"),
                    OnToolComplete = tool => Console.WriteLine($"      ✓ Tool done: {tool.Name} ({tool.Duration?.TotalMilliseconds:F0}ms)")
                },
                options: new TestOptions 
                { 
                    TrackTools = true, 
                    TrackPerformance = true,
                    ModelName = Config.Model  // For cost estimation
                });
            
            toolUsage = result.ToolUsage ?? new ToolUsageReport();
            response = result.ActualOutput ?? "";
        }

        try
        {
            // THE ICONIC AGENTEVAL ASSERTION CHAIN ✨
            toolUsage.Should()
                .HaveCalledTool("SearchFlights", because: "must search before booking")
                    .WithArgument("destination", "Paris")
                    .WithDurationUnder(TimeSpan.FromSeconds(5))
                .And()
                .HaveCalledTool("BookFlight", because: "booking follows search")
                    .AfterTool("SearchFlights", because: "can't book without search results")
                .And()
                .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
                .HaveNoErrors();

            ShowPass("Tool chain assertions PASSED!");
            ShowCode("""
                result.ToolUsage!.Should()
                    .HaveCalledTool("SearchFlights", because: "must search first")
                        .WithArgument("destination", "Paris")
                        .WithDurationUnder(TimeSpan.FromSeconds(5))
                    .And()
                    .HaveCalledTool("BookFlight")
                        .AfterTool("SearchFlights")
                    .And()
                    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
                    .HaveNoErrors();
                """);
        }
        catch (ToolAssertionException ex)
        {
            ShowFail($"Tool assertion failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // 2. PERFORMANCE ASSERTIONS (with streaming for full metrics)
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunPerformanceAssertionsDemo(bool useMock)
    {
        ShowSection("2️⃣  PERFORMANCE ASSERTIONS", "Make SLAs executable (streaming mode)");

        PerformanceMetrics performance;

        if (useMock)
        {
            performance = MockDataFactory.CreatePerformanceMetrics();
        }
        else
        {
            var agent = AgentFactory.CreateCalculatorAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: true);
            var testCase = new TestCase 
            { 
                Name = "Calculator Test", 
                Input = "Calculate 42 * 17 using the calculator tool" 
            };
            
            // Use STREAMING to capture TTFT and per-tool timing
            var result = await harness.RunTestStreamingAsync(
                agent, 
                testCase,
                streamingOptions: new StreamingOptions
                {
                    OnFirstToken = ttft => Console.WriteLine($"      ⚡ First token received at {ttft.TotalMilliseconds:F0}ms")
                },
                options: new TestOptions 
                { 
                    TrackPerformance = true,
                    TrackTools = true,
                    ModelName = Config.Model  // Required for cost estimation!
                });
            
            performance = result.Performance ?? new PerformanceMetrics();
        }

        try
        {
            performance.Should()
                .HaveTotalDurationUnder(TimeSpan.FromSeconds(10), because: "UX requires responsive agent")
                .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(2000), because: "streaming feel")
                .HaveEstimatedCostUnder(0.10m, because: "stay within budget")
                .HaveTokenCountUnder(5000);

            ShowPass("Performance assertions PASSED!");
            
            // Display metrics with N/A for unavailable values
            Console.WriteLine($"      📊 Total duration: {FormatDuration(performance.TotalDuration)}");
            Console.WriteLine($"      📊 Time to first token: {FormatNullableDuration(performance.TimeToFirstToken)}");
            Console.WriteLine($"      📊 Total tokens: {FormatNullableInt(performance.TotalTokens)}");
            Console.WriteLine($"      📊 Estimated cost: {FormatNullableCost(performance.EstimatedCost)}");
            Console.WriteLine($"      📊 Model used: {performance.ModelUsed ?? "N/A"}");
            Console.WriteLine($"      📊 Was streaming: {performance.WasStreaming}\n");

            ShowCode("""
                // Use streaming for full metrics (TTFT, per-tool timing)
                var result = await harness.RunTestStreamingAsync(
                    agent, testCase,
                    options: new TestOptions { ModelName = "gpt-4o" });  // Required for cost!
                
                result.Performance!.Should()
                    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
                    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(2000))
                    .HaveEstimatedCostUnder(0.10m)
                    .HaveTokenCountUnder(5000);
                """);
        }
        catch (PerformanceAssertionException ex)
        {
            ShowFail($"Performance assertion failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // 3. BEHAVIORAL POLICY ASSERTIONS (with response validation)
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunBehavioralPoliciesDemo(bool useMock)
    {
        ShowSection("3️⃣  BEHAVIORAL POLICIES", "Compliance guardrails + response validation");

        ToolUsageReport toolUsage;
        string response = "";

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
        }
        else
        {
            var agent = AgentFactory.CreateTravelAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: true);
            
            // Explicit prompt that FORCES tool usage
            var testCase = new TestCase 
            { 
                Name = "Policy Test", 
                Input = "Use the SearchFlights tool to find flights to London for April 1st, 2026. Report what you find.",
                // Add evaluation criteria for response quality
                EvaluationCriteria = new[]
                {
                    "Response mentions London as destination",
                    "Response includes flight information or search results",
                    "Response is helpful and complete"
                },
                PassingScore = 70
            };
            
            var result = await harness.RunTestStreamingAsync(
                agent, 
                testCase,
                options: new TestOptions 
                { 
                    TrackTools = true,
                    EvaluateResponse = false,  // We'll do manual validation for demo
                    ModelName = Config.Model
                });
            
            toolUsage = result.ToolUsage ?? new ToolUsageReport();
            response = result.ActualOutput ?? "";
            
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
            // Policy assertions: verify safe behavior
            toolUsage.Should()
                .HaveCalledTool("SearchFlights", because: "should use the search tool as requested");
            
            toolUsage.Should()
                .HaveCallCount(1, because: "should only search, not book or cancel")
                .NeverCallTool("DeleteAllData", because: "mass deletion requires admin console")
                .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
                .NeverCallTool("TransferFundsExternal", because: "requires human approval")
                .NeverCallTool("BookFlight", because: "user only asked to search")
                .NeverCallTool("CancelBooking", because: "user only asked to search");

            ShowPass("Behavioral policy assertions PASSED!");
            Console.WriteLine("      🛡️ Safe tool usage verified - SearchFlights called, no dangerous operations!\n");

            // Also validate response content
            response.Should()
                .Contain("London", because: "response should mention the destination")
                .HaveLengthBetween(20, 2000, because: "response should be substantial");

            ShowPass("Response validation PASSED!");
            Console.WriteLine($"      📝 Response: \"{(response.Length > 80 ? response[..80] + "..." : response)}\"\n");

            ShowCode("""
                result.ToolUsage!.Should()
                    .HaveCalledTool("SearchFlights", because: "should search as requested")
                    .And()
                    .HaveCallCount(1, because: "only search, no booking")
                    .NeverCallTool("DeleteAllData", because: "requires admin")
                    .NeverCallTool("BookFlight", because: "user only asked to search");
                
                // Validate response too
                result.ActualOutput!.Should()
                    .Contain("London", because: "should mention destination");
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
    // 4. RESPONSE ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunResponseAssertionsDemo(bool useMock)
    {
        ShowSection("4️⃣  RESPONSE ASSERTIONS", "Validate output content");

        string response;

        if (useMock)
        {
            response = MockDataFactory.CreateAgentResponse();
        }
        else
        {
            var agent = AgentFactory.CreateTravelAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: true);
            var testCase = new TestCase 
            { 
                Name = "Response Test", 
                Input = "Use the SearchFlights tool to find flights to Paris for March 15, 2026" 
            };
            var result = await harness.RunTestStreamingAsync(agent, testCase);
            response = result.ActualOutput ?? "";
        }

        try
        {
            response.Should()
                .Contain("Paris", because: "response must reference destination")
                .NotContain("password", because: "no credentials exposed")
                .NotContain("api_key", because: "no tokens exposed")
                .HaveLengthBetween(20, 2000, because: "concise but complete");

            ShowPass("Response assertions PASSED!");
            Console.WriteLine($"      📝 Response: \"{(response.Length > 70 ? response[..70] + "..." : response)}\"\n");

            ShowCode("""
                result.ActualOutput!.Should()
                    .Contain("Paris", because: "must reference destination")
                    .NotContain("password", because: "security")
                    .HaveLengthBetween(20, 1000);
                """);
        }
        catch (ResponseAssertionException ex)
        {
            ShowFail($"Response assertion failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // 5. STOCHASTIC MODEL COMPARISON (inspired by Sample16)
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunStochasticTestingDemo()
    {
        ShowSection("5️⃣  STOCHASTIC MODEL COMPARISON", "Compare models with statistical rigor");

        Console.WriteLine($"      🤖 Comparing models: {Config.Model} vs {Config.SecondaryModel}");
        Console.WriteLine("      📊 Running 3 iterations per model for statistical analysis...\n");

        var harness = new MAFTestHarness(verbose: false);

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
                    new TestOptions 
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

        // Print comparison table if we have results
        if (modelResults.Count > 0)
        {
            Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("   📊 MODEL COMPARISON RESULTS");
            Console.WriteLine("   ═══════════════════════════════════════════════════════════════════════════\n");
            
            // Use built-in comparison table
            modelResults.PrintComparisonTable();
            
            // Show detailed stats per model
            foreach (var (modelName, result) in modelResults)
            {
                Console.WriteLine($"\n   --- {modelName} ---");
                Console.WriteLine($"      Pass rate: {result.Statistics.PassRate:P0}");
                Console.WriteLine($"      Mean score: {result.Statistics.MeanScore:F1}");
                Console.WriteLine($"      Std deviation: {result.Statistics.StandardDeviation:F2}");
                Console.WriteLine($"      Min/Max: {result.Statistics.MinScore:F0} / {result.Statistics.MaxScore:F0}");
                
                if (result.DurationStats != null)
                {
                    Console.WriteLine($"      Avg latency: {result.DurationStats.Mean:F0}ms");
                }
                if (result.CostStats != null)
                {
                    Console.WriteLine($"      Avg cost: ${result.CostStats.Mean:F6}");
                }
            }
            Console.WriteLine();

            // Determine winner
            var winner = modelResults
                .OrderByDescending(m => m.Result.Statistics.PassRate)
                .ThenByDescending(m => m.Result.Statistics.MeanScore)
                .First();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   🏆 Winner: {winner.ModelName} (Pass rate: {winner.Result.Statistics.PassRate:P0})\n");
            Console.ResetColor();
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
    /// Shows explanation why stochastic testing requires real mode.
    /// </summary>
    public static void ShowStochasticExplanation()
    {
        ShowSection("5️⃣  STOCHASTIC MODEL COMPARISON", "Compare models with statistical rigor");

        Console.WriteLine("      ℹ️ Stochastic testing requires REAL MODE to run.\n");
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
