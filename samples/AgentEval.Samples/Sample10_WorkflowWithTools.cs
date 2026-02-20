// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.ComponentModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using AgentEval.Models.Serialization;

namespace AgentEval.Samples;

/// <summary>
/// Sample 10: Workflow With Tools — TripPlanner Pipeline
/// 
/// This demonstrates:
/// - MAF Workflow where multiple agents use tools (function calling)
/// - TripPlannerAgent with GetInfoAbout tool (city information)
/// - FlightReservationAgent with SearchFlights + BookFlight tools
/// - HotelReservationAgent with BookHotel tool
/// - TripPlannerPresenterAgent that formats the final itinerary
/// - Evaluating tool usage within a workflow pipeline
/// 
/// ⏱️ Time to understand: 15 minutes
/// ⏱️ Time to run: ~60–120 seconds (4 sequential LLM calls with tools)
/// </summary>
public static class Sample10_WorkflowWithTools
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine("📝 Step 1: Building TripPlanner MAF Workflow with tools...\n");

        var (workflow, executorIds) = CreateTripPlannerWorkflow();

        Console.WriteLine($"   Workflow name : {workflow.Name}");
        Console.WriteLine($"   Start executor: {workflow.StartExecutorId}");
        Console.WriteLine($"   Executors     : {string.Join(" → ", executorIds)}");
        Console.WriteLine($"   Mode          : 🚀 REAL (Azure OpenAI — {AIConfig.ModelDeployment})");
        Console.WriteLine($"   Tools         : GetInfoAbout, SearchFlights, BookFlight, BookHotel\n");

        Console.WriteLine("📝 Step 2: Creating MAFWorkflowAdapter.FromMAFWorkflow()...\n");

        var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
            workflow,
            "TripPlanner",
            executorIds,
            workflowType: "PromptChaining");

        Console.WriteLine($"   Adapter name   : {workflowAdapter.Name}");
        Console.WriteLine($"   Graph nodes    : {workflowAdapter.GraphDefinition?.Nodes.Count ?? 0}");
        Console.WriteLine($"   Graph edges    : {workflowAdapter.GraphDefinition?.Edges.Count ?? 0}");
        Console.WriteLine($"   Entry node     : {workflowAdapter.GraphDefinition?.EntryNodeId}");
        Console.WriteLine($"   Exit node(s)   : {string.Join(", ", workflowAdapter.GraphDefinition?.ExitNodeIds ?? [])}\n");

        Console.WriteLine("📝 Step 3: Creating workflow test case...\n");

        var testCase = new WorkflowTestCase
        {
            Name = "TripPlanner — Tokyo & Beijing Trip",
            Input = "Plan a 7-day trip visiting both Tokyo and Beijing. I need city information, flights between them, and hotel bookings for each city.",
            Description = "Tests the TripPlanner workflow with tool-calling agents",
            ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
            StrictExecutorOrder = true,
            MaxDuration = TimeSpan.FromMinutes(5),
            ExpectedTools = ["GetInfoAbout", "SearchFlights", "BookFlight", "BookHotel"],
            Tags = ["trip-planner", "tools", "workflow", "maf-workflowbuilder"]
        };

        Console.WriteLine($"   Test     : {testCase.Name}");
        Console.WriteLine($"   Input    : \"{testCase.Input[..Math.Min(80, testCase.Input.Length)]}...\"");
        Console.WriteLine($"   Flow     : {string.Join(" → ", testCase.ExpectedExecutors!)}");
        Console.WriteLine($"   Timeout  : {testCase.MaxDuration!.Value.TotalSeconds}s\n");

        Console.WriteLine("📝 Step 4: Running TripPlanner workflow...\n");
        Console.WriteLine("   ⏳ Executing real LLM calls with tool invocations...\n");

        var harness = new WorkflowEvaluationHarness(verbose: true);
        var testOptions = new WorkflowTestOptions
        {
            Timeout = TimeSpan.FromMinutes(5),
            Verbose = true
        };

        WorkflowTestResult testResult;
        try
        {
            testResult = await harness.RunWorkflowTestAsync(workflowAdapter, testCase, testOptions);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Workflow test failed: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("   💡 Tip: Ensure your Azure OpenAI deployment supports tool calling.");
            return;
        }

        Console.WriteLine("\n📊 DETAILED WORKFLOW RESULTS:");
        Console.WriteLine(new string('═', 80));

        if (testResult.ExecutionResult != null)
        {
            var result = testResult.ExecutionResult;

            Console.WriteLine($"   🎯 Overall : {(testResult.Passed ? "✅ PASSED" : "❌ FAILED")}");
            Console.WriteLine($"   ⏱️ Duration: {result.TotalDuration.TotalMilliseconds:F0}ms ({result.TotalDuration.TotalSeconds:F1}s)");
            Console.WriteLine($"   🔗 Steps   : {result.Steps.Count}");
            Console.WriteLine($"   ❌ Errors   : {result.Errors?.Count ?? 0}\n");

            // Execution timeline
            Console.WriteLine("   📈 EXECUTION TIMELINE:");
            foreach (var step in result.Steps)
            {
                var startMs = step.StartOffset.TotalMilliseconds;
                var durMs = step.Duration.TotalMilliseconds;

                Console.WriteLine($"      {step.StepIndex + 1}. [{startMs:F0}ms] {step.ExecutorId} ({durMs:F0}ms)");

                if (!string.IsNullOrEmpty(step.Output))
                {
                    var snippet = step.Output.Length > 120 ? step.Output[..117] + "..." : step.Output;
                    Console.WriteLine($"         → \"{snippet}\"");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("\n🔧 HARNESS-LEVEL TOOL TRACKING:");
        Console.WriteLine(new string('─', 80));

        if (testResult.ExecutionResult != null)
        {
            var result = testResult.ExecutionResult;

            if (result.ToolUsage != null)
            {
                Console.WriteLine($"   📊 ToolUsage report: {result.ToolUsage.Count} total tool calls");
                Console.WriteLine($"   🔧 Unique tools: {string.Join(", ", result.ToolUsage.UniqueToolNames)}");
                Console.WriteLine($"   ⏱️ Total tool time: {result.ToolUsage.TotalToolTime.TotalMilliseconds:F0}ms");
                Console.WriteLine($"   ❌ Has errors: {result.ToolUsage.HasErrors}\n");

                // Per-executor breakdown
                foreach (var step in result.Steps.Where(s => s.HasToolCalls))
                {
                    Console.WriteLine($"   📌 {step.ExecutorId}: {step.ToolCalls!.Count} tool call(s)");
                    foreach (var tc in step.ToolCalls!)
                    {
                        var duration = tc.Duration?.TotalMilliseconds ?? 0;
                        var resultSnippet = tc.Result?.ToString() ?? "(no result)";
                        if (resultSnippet.Length > 80) resultSnippet = resultSnippet[..77] + "...";
                        Console.WriteLine($"      🔧 {tc.Name} ({duration:F0}ms) → {resultSnippet}");
                    }
                }

                // Tool call order across workflow
                Console.WriteLine($"\n   📋 Call order: {string.Join(" → ", result.ToolUsage.ToolNames)}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("   ⚠️ No tool calls captured at harness level.");
                Console.WriteLine("   This could mean the MAF workflow did not emit tool content events.");
                Console.ResetColor();
            }

            // Timeline display
            if (result.Timeline != null)
            {
                Console.WriteLine($"\n   📈 TOOL TIMELINE:");
                Console.WriteLine($"      Total: {result.Timeline.TotalToolCalls} calls, {result.Timeline.TotalDuration.TotalMilliseconds:F0}ms total");
                Console.WriteLine($"      Tool time: {result.Timeline.TotalToolTime.TotalMilliseconds:F0}ms ({result.Timeline.ToolTimePercentage:F1}%)");
                Console.WriteLine(result.Timeline.ToAsciiDiagram(80));
            }
        }

        Console.WriteLine("\n📝 Step 7: Workflow assertions...\n");

        if (testResult.ExecutionResult != null)
        {
            try
            {
                var result = testResult.ExecutionResult;

                // Basic workflow structure assertions
                result.Should()
                    .HaveStepCount(4, because: "TripPlanner pipeline has 4 agents")
                    .HaveExecutedInOrder("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
                    .HaveCompletedWithin(TimeSpan.FromMinutes(5), because: "reasonable time with tool calls")
                    .HaveNoErrors(because: "clean execution is required")
                    .HaveNonEmptyOutput()
                    .Validate();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Workflow structure assertions PASSED!\n");
                Console.ResetColor();

                // Per-executor assertions
                result.Should()
                    .ForExecutor("TripPlanner")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("FlightReservation")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("HotelReservation")
                        .HaveNonEmptyOutput()
                        .And()
                    .ForExecutor("Presenter")
                        .HaveNonEmptyOutput()
                        .And()
                    .Validate();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Per-executor assertions PASSED!\n");
                Console.ResetColor();

                // ── Tool-level assertions (harness-level tracking) ──
                // These assertions verify tool usage captured at the workflow event bridge level.
                // If no tool events were captured (ToolUsage is null), we skip gracefully.
                if (result.ToolUsage != null)
                {
                    result.Should()
                        .HaveCalledTool("GetInfoAbout", because: "TripPlanner must research cities")
                            .WithoutError()
                        .And()
                        .HaveCalledTool("SearchFlights")
                            .BeforeTool("BookFlight", because: "can't book without search results")
                            .WithoutError()
                        .And()
                        .HaveCalledTool("BookFlight")
                            .WithoutError()
                        .And()
                        .HaveCalledTool("BookHotel", because: "must book hotels")
                            .WithoutError()
                        .And()
                        .HaveNoToolErrors(because: "all tools must succeed for quality output")
                        .HaveAtLeastTotalToolCalls(4, because: "workflow uses at least 4 tool calls")
                        .Validate();

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("   ✅ Tool-level assertions PASSED!\n");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("   ⚠️ Tool-level assertions SKIPPED (no tool events captured).\n");
                    Console.ResetColor();
                }

                // Graph structure assertions
                result.Should()
                    .HaveGraphStructure()
                    .HaveNodes("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
                    .HaveEntryPoint("TripPlanner", because: "trip planning starts the pipeline")
                    .HaveTraversedEdge("FlightReservation", "HotelReservation")
                    .HaveUsedEdgeType(EdgeType.Sequential)
                    .HaveExecutionPath("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
                    .Validate();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Graph structure assertions PASSED!\n");
                Console.ResetColor();
            }
            catch (WorkflowAssertionException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   ❌ Workflow assertion failed: {ex.Message}");
                if (ex.Data.Contains("Suggestions"))
                    Console.WriteLine($"   💡 Suggestion: {ex.Data["Suggestions"]}");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        Console.WriteLine("📝 Step 8: Generating workflow visualization...\n");

        if (testResult.ExecutionResult != null)
        {
            var mermaid = WorkflowSerializer.ToMermaid(testResult.ExecutionResult);
            Console.WriteLine("   🎨 Mermaid diagram:");
            Console.ForegroundColor = ConsoleColor.Cyan;
            foreach (var line in mermaid.Split('\n').Take(15))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Console.WriteLine($"      {line}");
            }
            Console.ResetColor();
            Console.WriteLine("\n   💡 Copy the full diagram to https://mermaid.live for visualization!");

            var timeline = WorkflowSerializer.ToTimelineJson(testResult.ExecutionResult);
            Console.WriteLine($"\n   📊 Timeline JSON generated: {timeline.Length} characters\n");
        }

        PrintKeyTakeaways();
    }

    // ── WORKFLOW CREATION ──

    /// <summary>
    /// Builds the TripPlanner MAF Workflow:
    /// TripPlanner → FlightReservation → HotelReservation → Presenter
    /// </summary>
    private static (Workflow workflow, string[] executorIds) CreateTripPlannerWorkflow()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        // ── TripPlanner Agent (GetInfoAbout tool) ──
        var tripPlanner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TripPlanner",
            Description = "Gathers city information and plans the trip itinerary",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a trip planning specialist. Given a travel request:
                    
                    1. Use the GetInfoAbout tool to gather information about each city mentioned
                    2. Create a day-by-day itinerary outline based on the city information
                    3. Include key attractions, cultural tips, and logistics overview
                    4. Note the cities that need flights and hotel bookings
                    
                    Call GetInfoAbout for EACH city mentioned in the request.
                    Your output will be passed to the flight reservation agent.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(GetInfoAbout)
                ]
            }
        });

        // ── FlightReservation Agent (SearchFlights + BookFlight tools) ──
        var flightAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "FlightReservation",
            Description = "Searches and books flights between cities",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a flight reservation specialist. Given a trip plan:
                    
                    1. Use SearchFlights to find available flights between the cities
                    2. Select the best option and use BookFlight to make the reservation
                    3. Summarize the booked flights with times—prices—confirmation numbers
                    
                    Call SearchFlights first, then BookFlight for each leg of the journey.
                    Your output will be passed to the hotel reservation agent.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(SearchFlights),
                    AIFunctionFactory.Create(BookFlight)
                ]
            }
        });

        // ── HotelReservation Agent (BookHotel tool) ──
        var hotelAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "HotelReservation",
            Description = "Books hotels for each city in the trip",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a hotel reservation specialist. Given a trip plan with booked flights:
                    
                    1. Use BookHotel to reserve accommodation for each city
                    2. Choose appropriate dates based on the flight schedule
                    3. Summarize booked hotels with names, dates, and confirmation numbers
                    
                    Call BookHotel for EACH city that needs accommodation.
                    Your output will be passed to the trip presenter.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(BookHotel)
                ]
            }
        });

        // ── Presenter Agent (no tools — formats the final output) ──
        var presenter = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Presenter",
            Description = "Presents the finalized trip plan in a nice format",
            ChatOptions = new() { Instructions = """
                You are a professional trip presentation specialist. Given all the trip details
                (city info, flights, hotels):
                
                1. Create a beautiful, well-organized trip itinerary
                2. Include day-by-day schedule with flights, hotels, and activities
                3. Add practical tips, estimated budgets, and important notes
                4. Use clear formatting with sections, bullet points, and highlights
                
                Deliver a publication-ready travel itinerary document.
                """ }
        });

        // ── Bind agents as workflow executors ──
        var tripPlannerBinding = tripPlanner.BindAsExecutor(emitEvents: true);
        var flightBinding = flightAgent.BindAsExecutor(emitEvents: true);
        var hotelBinding = hotelAgent.BindAsExecutor(emitEvents: true);
        var presenterBinding = presenter.BindAsExecutor(emitEvents: true);

        // ── Build sequential workflow ──
        var workflow = new WorkflowBuilder(tripPlannerBinding)
            .AddEdge(tripPlannerBinding, flightBinding)
            .AddEdge(flightBinding, hotelBinding)
            .AddEdge(hotelBinding, presenterBinding)
            .WithOutputFrom(presenterBinding)
            .WithName("TripPlanner")
            .WithDescription("Trip planning pipeline: plan → flights → hotels → present")
            .Build();

        return (workflow, ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"]);
    }

    // ── TOOLS ──

    [Description("Gets information about a city including attractions, culture, weather, and travel tips.")]
    public static async Task<string> GetInfoAbout(
        [Description("The name of the city to get information about")] string city)
    {
        Console.WriteLine($"   🌍 GetInfoAbout(\"{city}\")");
        await Task.Delay(100); // Simulate API latency

        var result = city.ToLowerInvariant() switch
        {
            "tokyo" => """
                Tokyo, Japan — A vibrant metropolis blending ultra-modern with traditional.
                🏯 Top attractions: Senso-ji Temple, Shibuya Crossing, Meiji Shrine, Akihabara, Tokyo Skytree
                🍣 Food: World-class sushi, ramen, tempura. Tsukiji Outer Market is a must.
                🚄 Transport: Efficient subway/rail. Get a Suica card. JR Pass for bullet trains.
                🌸 Best time: March-May (cherry blossoms) or Oct-Nov (autumn foliage).
                💰 Budget: ~$150-250/day mid-range. Hotels from $80-200/night.
                🗣️ Language: Japanese. English limited outside tourist areas. Translation apps help.
                """,
            "beijing" => """
                Beijing, China — Ancient capital with 3,000+ years of history.
                🏯 Top attractions: Great Wall (Mutianyu section), Forbidden City, Temple of Heaven, Summer Palace
                🥟 Food: Peking duck, dumplings (jiaozi), hot pot, street food at Wangfujing.
                🚇 Transport: Extensive subway system. DiDi app for taxis. Traffic can be heavy.
                🌤️ Best time: Sep-Oct (clear skies, mild temps) or April-May (spring).
                💰 Budget: ~$80-150/day mid-range. Hotels from $50-150/night.
                🗣️ Language: Mandarin Chinese. English very limited. WeChat essential for payments.
                """,
            _ => $"""
                {city} — A wonderful travel destination.
                🏛️ Attractions: Various cultural and historical sites.
                🍽️ Food: Local cuisine worth exploring.
                🚌 Transport: Public transportation available.
                💰 Budget: Varies by season and accommodation choice.
                """
        };

        return result;
    }

    [Description("Searches for available flights between two cities on a given date.")]
    public static async Task<string> SearchFlights(
        [Description("The departure city")] string fromCity,
        [Description("The destination city")] string toCity,
        [Description("The travel date (e.g., '2025-03-15')")] string date)
    {
        Console.WriteLine($"   ✈️ SearchFlights(\"{fromCity}\" → \"{toCity}\", {date})");
        await Task.Delay(150); // Simulate search time

        var result = $"""
            Available flights from {fromCity} to {toCity} on {date}:
            
            1. ✈️ Flight AE-101 | Depart: 08:30 → Arrive: 12:45 | $450 | Direct | Economy
            2. ✈️ Flight AE-205 | Depart: 14:00 → Arrive: 18:15 | $380 | Direct | Economy
            3. ✈️ Flight AE-309 | Depart: 20:30 → Arrive: 00:45+1 | $320 | Direct | Economy
            
            Recommended: AE-205 (best price-time balance)
            """;

        return result;
    }

    [Description("Books a specific flight. Returns a confirmation number.")]
    public static async Task<string> BookFlight(
        [Description("The flight number to book (e.g., 'AE-205')")] string flightNumber,
        [Description("Number of passengers")] int passengers = 1)
    {
        Console.WriteLine($"   🎫 BookFlight(\"{flightNumber}\", passengers={passengers})");
        await Task.Delay(100); // Simulate booking

        var confirmationCode = $"CONF-{flightNumber}-{Random.Shared.Next(10000, 99999)}";
        var result = $"""
            ✅ Flight {flightNumber} booked successfully!
            Confirmation: {confirmationCode}
            Passengers: {passengers}
            Status: CONFIRMED
            E-ticket will be sent to registered email.
            """;

        return result;
    }

    [Description("Books a hotel in the specified city for given dates. Returns confirmation details.")]
    public static async Task<string> BookHotel(
        [Description("The city to book a hotel in")] string city,
        [Description("Check-in date (e.g., '2025-03-15')")] string checkIn,
        [Description("Check-out date (e.g., '2025-03-18')")] string checkOut,
        [Description("Number of guests")] int guests = 1)
    {
        Console.WriteLine($"   🏨 BookHotel(\"{city}\", {checkIn} → {checkOut}, guests={guests})");
        await Task.Delay(100); // Simulate booking

        var hotelName = city.ToLowerInvariant() switch
        {
            "tokyo" => "Shinjuku Grand Hotel",
            "beijing" => "Beijing Imperial Garden Hotel",
            _ => $"{city} Central Hotel"
        };

        var pricePerNight = city.ToLowerInvariant() switch
        {
            "tokyo" => 180,
            "beijing" => 120,
            _ => 100
        };

        var confirmationCode = $"HTL-{city[..3].ToUpperInvariant()}-{Random.Shared.Next(10000, 99999)}";
        var result = $"""
            ✅ Hotel booked successfully!
            🏨 Hotel: {hotelName}
            📍 City: {city}
            📅 Check-in: {checkIn} | Check-out: {checkOut}
            👤 Guests: {guests}
            💰 Rate: ${pricePerNight}/night
            🔖 Confirmation: {confirmationCode}
            Status: CONFIRMED
            """;

        return result;
    }

    // ── UI HELPERS ──

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🧳 SAMPLE 10: WORKFLOW WITH TOOLS — TripPlanner Pipeline                   ║
║   TripPlanner → FlightReservation → HotelReservation → Presenter             ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE 10 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample runs a real TripPlanner MAF workflow with tool-calling agents. │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT     - Your Azure OpenAI endpoint                   │
   │    AZURE_OPENAI_API_KEY      - Your API key                                 │
   │    AZURE_OPENAI_DEPLOYMENT   - Chat model (e.g., gpt-4o)                    │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              🎯 KEY TAKEAWAYS                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  1. Agents in workflows can use tools via ChatOptions.Tools:                    │
│     var agent = new ChatClientAgent(client, new ChatClientAgentOptions {        │
│         ChatOptions = new ChatOptions { Tools = [ ... ] }                       │
│     });                                                                         │
│                                                                                 │
│  2. Tools are defined as static methods with [Description]:                     │
│     AIFunctionFactory.Create(SearchFlights)                                    │
│                                                                                 │
│  3. Harness-level tool tracking captures tool calls automatically:              │
│     MAFWorkflowEventBridge intercepts FunctionCallContent /                    │
│     FunctionResultContent from MAF events and populates ToolCalls              │
│     on each ExecutorStep, plus ToolUsage and Timeline on the result.           │
│                                                                                 │
│  4. Workflow-level tool assertions validate the entire pipeline:                │
│     result.Should()                                                            │
│         .HaveCalledTool(""SearchFlights"")                                       │
│             .BeforeTool(""BookFlight"")                                          │
│             .WithoutError()                                                    │
│         .And()                                                                 │
│         .HaveCalledTool(""BookFlight"")                                          │
│             .WithoutError()                                                    │
│         .And()                                                                 │
│         .HaveNoToolErrors()                                                    │
│         .Validate();                                                           │
│                                                                                 │
│  5. Use .ForExecutor() for executor-specific checks:                            │
│     .ForExecutor(""Presenter"").HaveToolCallCount(0).And()                       │
│     .ForExecutor(""Flight"").HaveCompletedWithin(...).And()                      │
│                                                                                 │
│  6. Use .Done() to jump from tool→workflow (skip executor level):               │
│     .ForExecutor(""X"").HaveCalledTool(""Y"").WithoutError().Done()              │
│     // .Done() = .And().And() but cleaner                                      │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
