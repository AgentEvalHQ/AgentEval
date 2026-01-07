// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using AgentEval.Models;
using AgentEval.Assertions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 11: Because Assertions - Self-documenting tests with intent clarity
/// 
/// This demonstrates:
/// - Using the 'because' parameter for clear failure messages
/// - Fluent assertion chaining with intent documentation
/// - Better debugging with contextual error messages
/// - Compliance and audit-friendly test documentation
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample11_BecauseAssertions
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ═══════════════════════════════════════════════════════════════
        // WHY "BECAUSE" MATTERS
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine(@"
   📖 WHY USE 'BECAUSE' PARAMETERS?
   
   The 'because' parameter serves multiple purposes:
   
   • DEBUGGING  - When tests fail, you know WHY the assertion exists
   • COMPLIANCE - Document regulatory requirements directly in tests
   • ONBOARDING - New team members understand test intent immediately
   • AUDITING   - Tests serve as living documentation of requirements
   
   Compare these error messages:
   
   ❌ WITHOUT BECAUSE:
   ""Expected tool 'verify_identity' to be called but it was not.""
   
   ✅ WITH BECAUSE:
   ""Expected tool 'verify_identity' to be called but it was not
    because KYC regulations require identity verification before transactions""
");

        await Task.Delay(1); // Keep async signature

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Tool Assertions with Because
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Tool assertions with 'because'...\n");
        
        var toolUsage = CreateSampleToolUsage();
        
        try
        {
            toolUsage.Should()
                .HaveCalledTool("verify_identity", 
                    because: "KYC regulations require identity verification before transactions")
                    .BeforeTool("transfer_funds",
                        because: "compliance requires verification before execution")
                    .And()
                .HaveCalledTool("check_balance", 
                    because: "overdraft protection policy requires balance check")
                    .WithoutError(because: "balance check must succeed for transaction to proceed")
                    .And()
                .HaveNoErrors(
                    because: "any tool failure must abort the transaction for data integrity");
            
            PrintSuccess("Tool assertions with 'because' passed!");
            ShowCodeExample(@"
   toolUsage.Should()
       .HaveCalledTool(""verify_identity"", 
           because: ""KYC regulations require identity verification"")
           .BeforeTool(""transfer_funds"", because: ""verification first"")
           .And()
       .HaveNoErrors(because: ""tool failures abort transaction"");
");
        }
        catch (ToolAssertionException ex)
        {
            PrintError($"Tool assertion failed: {ex.Message}");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Performance Assertions with Because
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 2: Performance assertions with 'because'...\n");
        
        var metrics = CreateSamplePerformanceMetrics();
        
        try
        {
            metrics.Should()
                .HaveTotalDurationUnder(TimeSpan.FromSeconds(2),
                    because: "SLA guarantees sub-2s response time for premium tier")
                .HaveTokenCountUnder(4000,
                    because: "cost optimization target limits tokens per request")
                .HaveEstimatedCostUnder(0.05m,
                    because: "budget constraint of $0.05 per transaction");
            
            PrintSuccess("Performance assertions with 'because' passed!");
            ShowCodeExample(@"
   metrics.Should()
       .HaveTotalDurationUnder(TimeSpan.FromSeconds(2),
           because: ""SLA guarantees sub-2s response time for premium tier"")
       .HaveEstimatedCostUnder(0.05m,
           because: ""budget constraint of $0.05 per transaction"");
");
        }
        catch (PerformanceAssertionException ex)
        {
            PrintError($"Performance assertion failed: {ex.Message}");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Response Assertions with Because
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 3: Response assertions with 'because'...\n");
        
        var response = CreateSampleResponse();
        
        try
        {
            response.Should()
                .NotBeEmpty(because: "silent failures are unacceptable in customer-facing flows")
                .Contain("confirmation", because: "users must receive explicit transaction confirmation")
                .NotContain("error", because: "successful transactions should not mention errors");
            
            PrintSuccess("Response assertions with 'because' passed!");
            ShowCodeExample(@"
   response.Should()
       .NotBeEmpty(because: ""silent failures are unacceptable"")
       .Contain(""confirmation"", 
           because: ""users must receive explicit confirmation"");
");
        }
        catch (ResponseAssertionException ex)
        {
            PrintError($"Response assertion failed: {ex.Message}");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Workflow Assertions with Because
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 4: Workflow assertions with 'because'...\n");
        
        var workflowResult = CreateSampleWorkflowResult();
        
        try
        {
            workflowResult.Should()
                .HaveStepCount(3, because: "payment flow requires exactly 3 steps: validate, process, confirm")
                .HaveCompletedWithin(TimeSpan.FromSeconds(5),
                    because: "payment timeout is 5 seconds per merchant agreement")
                .HaveNoErrors(because: "partial payment execution is not allowed")
                .ForExecutor("processor")
                    .HaveCalledTool("process_payment", because: "processor must call payment gateway")
                    .HaveCompletedWithin(TimeSpan.FromSeconds(2), 
                        because: "gateway timeout is 2 seconds")
                    .And()
                .Validate();
            
            PrintSuccess("Workflow assertions with 'because' passed!");
            ShowCodeExample(@"
   workflowResult.Should()
       .HaveStepCount(3, because: ""3 steps: validate, process, confirm"")
       .ForExecutor(""processor"")
           .HaveCalledTool(""process_payment"", 
               because: ""processor must call payment gateway"")
           .And()
       .Validate();
");
        }
        catch (WorkflowAssertionException ex)
        {
            PrintError($"Workflow assertion failed: {ex.Message}");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Show What Failure Messages Look Like
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 5: What failure messages look like...\n");
        
        DemonstrateFailureMessage();

        // ═══════════════════════════════════════════════════════════════
        // KEY TAKEAWAYS
        // ═══════════════════════════════════════════════════════════════
        PrintKeyTakeaways();
    }

    private static void DemonstrateFailureMessage()
    {
        Console.WriteLine("   When assertions fail, the 'because' parameter appears in the error:\n");
        
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(@"   ┌─────────────────────────────────────────────────────────────────────┐
   │  ToolAssertionException                                              │
   ├─────────────────────────────────────────────────────────────────────┤
   │                                                                     │
   │  Expected tool 'verify_identity' to be called but it was not        │
   │  because KYC regulations require identity verification              │
   │  before transactions                                                │
   │                                                                     │
   │  Actual tools called: [check_balance, transfer_funds]               │
   │                                                                     │
   │  Suggestions:                                                       │
   │    → Add identity verification step before transaction              │
   │    → Check if agent prompt includes KYC requirements                │
   │                                                                     │
   └─────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        
        Console.WriteLine("\n   Compare to without 'because':\n");
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(@"   ┌─────────────────────────────────────────────────────────────────────┐
   │  Expected tool 'verify_identity' to be called but it was not.       │
   │  Actual tools called: [check_balance, transfer_funds]               │
   └─────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        
        Console.WriteLine("\n   💡 The 'because' version tells you WHY this matters!");
    }

    private static ToolUsageReport CreateSampleToolUsage()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "verify_identity", CallId = "1", Order = 1, Result = "verified" });
        report.AddCall(new ToolCallRecord { Name = "check_balance", CallId = "2", Order = 2, Result = "sufficient" });
        report.AddCall(new ToolCallRecord { Name = "transfer_funds", CallId = "3", Order = 3, Result = "success" });
        return report;
    }

    private static PerformanceMetrics CreateSamplePerformanceMetrics()
    {
        return new PerformanceMetrics
        {
            StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-850),
            EndTime = DateTimeOffset.UtcNow,
            PromptTokens = 150,
            CompletionTokens = 280,
            EstimatedCost = 0.0043m
        };
    }

    private static string CreateSampleResponse()
    {
        return "Transaction complete. Your transfer of $500 to John Doe has been processed. " +
               "Confirmation number: TXN-2026-01-07-8472. Expected arrival: 1-2 business days.";
    }

    private static WorkflowExecutionResult CreateSampleWorkflowResult()
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "Payment processed successfully",
            TotalDuration = TimeSpan.FromMilliseconds(1200),
            Steps = [
                new ExecutorStep
                {
                    ExecutorId = "validator",
                    ExecutorName = "Payment Validator",
                    StepIndex = 0,
                    Output = "Validation passed",
                    Duration = TimeSpan.FromMilliseconds(200),
                    StartOffset = TimeSpan.Zero
                },
                new ExecutorStep
                {
                    ExecutorId = "processor",
                    ExecutorName = "Payment Processor",
                    StepIndex = 1,
                    Output = "Payment processed",
                    Duration = TimeSpan.FromMilliseconds(800),
                    StartOffset = TimeSpan.FromMilliseconds(200),
                    ToolCalls = [
                        new ToolCallRecord { Name = "process_payment", CallId = "1", Order = 1, Result = "success" }
                    ]
                },
                new ExecutorStep
                {
                    ExecutorId = "notifier",
                    ExecutorName = "Notification Agent",
                    StepIndex = 2,
                    Output = "Customer notified",
                    Duration = TimeSpan.FromMilliseconds(200),
                    StartOffset = TimeSpan.FromMilliseconds(1000)
                }
            ]
        };
    }

    private static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   ✅ {message}");
        Console.ResetColor();
    }

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"   ❌ {message}");
        Console.ResetColor();
    }

    private static void ShowCodeExample(string code)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(code);
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║              Sample 11: Because Assertions                                    ║
║                                                                               ║
║   Learn how to:                                                               ║
║   • Use 'because' for self-documenting tests                                  ║
║   • Create compliance-friendly test suites                                    ║
║   • Get better error messages when tests fail                                 ║
║   • Document business requirements in code                                    ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
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
│  1. Add 'because' to document WHY an assertion exists:                          │
│     .HaveCalledTool(""verify"", because: ""KYC requires verification"")           │
│                                                                                 │
│  2. The 'because' appears in failure messages for better debugging              │
│                                                                                 │
│  3. Use for compliance documentation:                                           │
│     .HaveStepCount(3, because: ""PCI-DSS requirement"")                          │
│                                                                                 │
│  4. Use for SLA documentation:                                                  │
│     .HaveTotalDurationUnder(TimeSpan.FromSeconds(2), because: ""SLA: 2s max"")   │
│                                                                                 │
│  5. Use for business rules:                                                     │
│     .NotContain(""error"", because: ""success path must not mention errors"")     │
│                                                                                 │
│  6. All assertion types support 'because':                                      │
│     • Tool assertions (.HaveCalledTool, .BeforeTool, etc.)                      │
│     • Performance assertions (.HaveTotalDurationUnder, etc.)                    │
│     • Response assertions (.Contain, .NotContain, etc.)                         │
│     • Workflow assertions (.HaveStepCount, .ForExecutor, etc.)                  │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
