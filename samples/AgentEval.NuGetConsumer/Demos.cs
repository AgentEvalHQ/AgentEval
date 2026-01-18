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
            // REAL: Execute actual agent
            var agent = AgentFactory.CreateTravelAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: false);
            var testCase = new TestCase
            {
                Name = "Travel Booking Flow",
                Input = "Search for flights to Paris on March 15th, 2026, book the cheapest one, and send confirmation to user@example.com"
            };
            
            var result = await harness.RunTestAsync(agent, testCase, new TestOptions { TrackTools = true, TrackPerformance = true });
            
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
    // 2. PERFORMANCE ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunPerformanceAssertionsDemo(bool useMock)
    {
        ShowSection("2️⃣  PERFORMANCE ASSERTIONS", "Make SLAs executable");

        PerformanceMetrics performance;

        if (useMock)
        {
            performance = MockDataFactory.CreatePerformanceMetrics();
        }
        else
        {
            var agent = AgentFactory.CreateCalculatorAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: false);
            var testCase = new TestCase { Name = "Calculator Test", Input = "Calculate 42 * 17" };
            
            var result = await harness.RunTestAsync(agent, testCase, new TestOptions { TrackPerformance = true });
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
            Console.WriteLine($"      📊 Total duration: {performance.TotalDuration.TotalMilliseconds:F0}ms");
            Console.WriteLine($"      📊 Time to first token: {performance.TimeToFirstToken?.TotalMilliseconds:F0}ms");
            Console.WriteLine($"      📊 Total tokens: {performance.TotalTokens}");
            Console.WriteLine($"      📊 Estimated cost: ${performance.EstimatedCost:F4}\n");

            ShowCode("""
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
    // 3. BEHAVIORAL POLICY ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunBehavioralPoliciesDemo(bool useMock)
    {
        ShowSection("3️⃣  BEHAVIORAL POLICIES", "Compliance guardrails");

        ToolUsageReport toolUsage;

        if (useMock)
        {
            // Safe usage - no dangerous tools called
            toolUsage = new ToolUsageReport();
            toolUsage.AddCall(new ToolCallRecord { Name = "SearchFlights", CallId = "1", Order = 1, Result = "flights found" });
            toolUsage.AddCall(new ToolCallRecord { Name = "GetPricing", CallId = "2", Order = 2, Result = "pricing info" });
        }
        else
        {
            var agent = AgentFactory.CreateTravelAgent(useMock: false);
            var harness = new MAFTestHarness(verbose: false);
            var testCase = new TestCase { Name = "Policy Test", Input = "What flights are available to London?" };
            var result = await harness.RunTestAsync(agent, testCase, new TestOptions { TrackTools = true });
            toolUsage = result.ToolUsage ?? new ToolUsageReport();
        }

        try
        {
            toolUsage.Should()
                .NeverCallTool("DeleteAllData", because: "mass deletion requires admin console")
                .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
                .NeverCallTool("TransferFundsExternal", because: "requires human approval");

            ShowPass("Behavioral policy assertions PASSED!");
            Console.WriteLine("      🛡️ No dangerous tools were called - policies enforced!\n");

            ShowCode("""
                result.ToolUsage!.Should()
                    .NeverCallTool("DeleteAllData", because: "requires admin")
                    .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
                    .NeverPassArgumentMatching(@"\b\d{16}\b", because: "PCI-DSS");
                """);
        }
        catch (BehavioralPolicyViolationException ex)
        {
            ShowFail($"Policy violation: {ex.PolicyName} - {ex.ViolatingAction}");
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
            var harness = new MAFTestHarness(verbose: false);
            var testCase = new TestCase { Name = "Response Test", Input = "Find flights to Paris for March 15" };
            var result = await harness.RunTestAsync(agent, testCase);
            response = result.ActualOutput ?? "";
        }

        try
        {
            response.Should()
                .Contain("Paris", because: "response must reference destination")
                .NotContain("password", because: "no credentials exposed")
                .NotContain("api_key", because: "no tokens exposed")
                .HaveLengthBetween(20, 1000, because: "concise but complete");

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
    // 5. STOCHASTIC TESTING (Real mode only)
    // ═══════════════════════════════════════════════════════════════════════════════

    public static async Task RunStochasticTestingDemo()
    {
        ShowSection("5️⃣  STOCHASTIC TESTING", "Handle LLM non-determinism with statistical analysis");

        Console.WriteLine("      Running 5 iterations to measure variability...\n");

        var agent = AgentFactory.CreateCalculatorAgent(useMock: false);
        var harness = new MAFTestHarness(verbose: false);

        var testCase = new TestCase
        {
            Name = "Calculator Test",
            Input = "What is 25 times 4? Use the calculator tool.",
            ExpectedOutputContains = "100"
        };

        var options = new StochasticOptions(
            Runs: 5,
            SuccessRateThreshold: 0.8);

        var runner = new StochasticRunner(harness, new TestOptions { TrackTools = true, TrackPerformance = true });
        var result = await runner.RunStochasticTestAsync(agent, testCase, options);

        // Display results
        Console.WriteLine($"      📊 Runs completed: {result.IndividualResults.Count}");
        Console.WriteLine($"      📊 Success rate: {result.Statistics.PassRate * 100:F1}%");
        Console.WriteLine($"      📊 Threshold: {options.SuccessRateThreshold * 100:F0}%");
        Console.WriteLine($"      📊 Passed threshold: {(result.Passed ? "✅ YES" : "❌ NO")}\n");

        Console.WriteLine($"      📈 Mean score: {result.Statistics.MeanScore:F1}");
        Console.WriteLine($"      📈 Std deviation: {result.Statistics.StandardDeviation:F2}");
        Console.WriteLine($"      📈 Min: {result.Statistics.MinScore}, Max: {result.Statistics.MaxScore}\n");

        // Use the built-in print table from AgentEval
        result.PrintTable("Stochastic Results");

        ShowPass("Stochastic testing completed!");
        ShowCode("""
            var result = await stochasticRunner.RunStochasticTestAsync(
                agent, testCase,
                new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
            
            Assert.True(result.Passed);
            result.Statistics.MeanScore.Should().BeGreaterThan(80);
            """);
    }

    /// <summary>
    /// Shows explanation why stochastic testing requires real mode.
    /// </summary>
    public static void ShowStochasticExplanation()
    {
        ShowSection("5️⃣  STOCHASTIC TESTING", "Statistical analysis of LLM variability");

        Console.WriteLine("      ℹ️ Stochastic testing requires REAL MODE to run.\n");
        Console.WriteLine("      Why? LLMs have inherent non-determinism - the same prompt");
        Console.WriteLine("      can produce different responses. Stochastic testing runs");
        Console.WriteLine("      the same test N times and provides statistical analysis:\n");
        Console.WriteLine("      • Success rate across runs");
        Console.WriteLine("      • Mean, standard deviation, min/max scores");
        Console.WriteLine("      • Confidence that agent meets threshold\n");
        Console.WriteLine("      Select REAL MODE to see live stochastic testing!\n");

        ShowCode("""
            var result = await stochasticRunner.RunStochasticTestAsync(
                agent, testCase,
                new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));
            
            // Statistical analysis
            Console.WriteLine($"Success rate: {result.Statistics.PassRate:P0}");
            Console.WriteLine($"Mean: {result.Statistics.MeanScore:F1}");
            Console.WriteLine($"StdDev: {result.Statistics.StandardDeviation:F2}");
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
}
