// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using AgentEval.Models;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Factory for creating mock test data for demo mode.
/// </summary>
public static class MockDataFactory
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates a mock ToolUsageReport simulating a travel booking flow.
    /// </summary>
    public static ToolUsageReport CreateTravelBookingToolUsage()
    {
        var report = new ToolUsageReport();
        
        report.AddCall(new ToolCallRecord
        {
            Name = "SearchFlights",
            CallId = "call_1",
            Order = 1,
            Arguments = new Dictionary<string, object?> 
            { 
                ["destination"] = "Paris", 
                ["date"] = "2026-03-15" 
            },
            Result = "Found 5 flights to Paris",
            StartTime = BaseTime,
            EndTime = BaseTime.AddMilliseconds(450)
        });

        report.AddCall(new ToolCallRecord
        {
            Name = "BookFlight",
            CallId = "call_2",
            Order = 2,
            Arguments = new Dictionary<string, object?> 
            { 
                ["flightId"] = "AF1234", 
                ["passengers"] = 2 
            },
            Result = "Booking confirmed! Reference: BK123456",
            StartTime = BaseTime.AddMilliseconds(500),
            EndTime = BaseTime.AddMilliseconds(820)
        });

        report.AddCall(new ToolCallRecord
        {
            Name = "SendConfirmation",
            CallId = "call_3",
            Order = 3,
            Arguments = new Dictionary<string, object?> 
            { 
                ["email"] = "user@example.com" 
            },
            Result = "Confirmation email sent",
            StartTime = BaseTime.AddMilliseconds(900),
            EndTime = BaseTime.AddMilliseconds(1050)
        });

        return report;
    }

    /// <summary>
    /// Creates a mock ToolUsageReport with proper confirmation sequence.
    /// </summary>
    public static ToolUsageReport CreateConfirmedActionToolUsage()
    {
        var report = new ToolUsageReport();
        
        report.AddCall(new ToolCallRecord
        {
            Name = "GetUserConfirmation",
            CallId = "call_1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["action"] = "Cancel booking BK123456" },
            Result = "User confirmed",
            StartTime = BaseTime,
            EndTime = BaseTime.AddMilliseconds(100)
        });

        report.AddCall(new ToolCallRecord
        {
            Name = "CancelBooking",
            CallId = "call_2",
            Order = 2,
            Arguments = new Dictionary<string, object?> { ["bookingRef"] = "BK123456" },
            Result = "Booking cancelled",
            StartTime = BaseTime.AddMilliseconds(150),
            EndTime = BaseTime.AddMilliseconds(350)
        });

        return report;
    }

    /// <summary>
    /// Creates mock PerformanceMetrics.
    /// </summary>
    public static PerformanceMetrics CreatePerformanceMetrics()
    {
        return new PerformanceMetrics
        {
            StartTime = BaseTime,
            EndTime = BaseTime.AddMilliseconds(1250),
            TimeToFirstToken = TimeSpan.FromMilliseconds(185),
            PromptTokens = 145,
            CompletionTokens = 320,
            EstimatedCost = 0.0028m,
            ModelUsed = "gpt-4o",
            ToolCallCount = 3,
            TotalToolTime = TimeSpan.FromMilliseconds(550)
        };
    }

    /// <summary>
    /// Creates a sample agent response.
    /// </summary>
    public static string CreateAgentResponse() => """
        I found 5 flights to Paris for March 15th, 2026. The cheapest option is €259 with Iberia.
        I've booked 2 passengers on flight AF1234 (Air France, €299) departing at 10:00.
        Your booking reference is BK123456. A confirmation email has been sent to user@example.com.
        Is there anything else I can help you with?
        """;
}
