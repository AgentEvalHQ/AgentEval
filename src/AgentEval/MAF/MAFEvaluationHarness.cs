// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.MAF;

/// <summary>
/// evaluation harness for Microsoft Agent Framework (MAF) agents.
/// Provides comprehensive testing, evaluation, and metrics collection.
/// </summary>
public class MAFEvaluationHarness : IStreamingEvaluationHarness, IBatchEvaluationHarness
{
    private readonly IEvaluator? _evaluator;
    private readonly IAgentEvalLogger _logger;
    
    /// <summary>
    /// Create a evaluation harness without AI-powered evaluation using console logging.
    /// </summary>
    public MAFEvaluationHarness(bool verbose = true)
        : this(evaluator: null, verbose ? new ConsoleAgentEvalLogger(LogLevel.Debug) : NullAgentEvalLogger.Instance)
    {
    }
    
    /// <summary>
    /// Create a evaluation harness with AI-powered evaluation.
    /// </summary>
    /// <param name="evaluatorClient">Chat client to use for AI evaluation.</param>
    /// <param name="verbose">Whether to print verbose output.</param>
    public MAFEvaluationHarness(IChatClient evaluatorClient, bool verbose = true)
        : this(new ChatClientEvaluator(evaluatorClient), verbose ? new ConsoleAgentEvalLogger(LogLevel.Debug) : NullAgentEvalLogger.Instance)
    {
    }
    
    /// <summary>
    /// Create a evaluation harness with a custom evaluator and logger.
    /// </summary>
    public MAFEvaluationHarness(IEvaluator? evaluator, IAgentEvalLogger logger)
    {
        _evaluator = evaluator;
        _logger = logger ?? NullAgentEvalLogger.Instance;
    }
    
    /// <summary>
    /// Create a evaluation harness with a custom evaluator (uses console logger).
    /// </summary>
    public MAFEvaluationHarness(IEvaluator evaluator, bool verbose = true)
        : this(evaluator, verbose ? new ConsoleAgentEvalLogger(LogLevel.Debug) : NullAgentEvalLogger.Instance)
    {
    }
    
    /// <inheritdoc/>
    public async Task<TestResult> RunEvaluationAsync(
        IEvaluableAgent agent,
        TestCase testCase,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvaluationOptions();
        var result = new TestResult { TestName = testCase.Name };
        var metrics = options.TrackPerformance ? new PerformanceMetrics { WasStreaming = false } : null;
        var timeline = ToolCallTimeline.Create(testCase.Name);

        try
        {
            _logger.LogDebug($"📥 Input: \"{Truncate(testCase.Input, 100)}\"");

            // Start timing
            if (metrics != null)
            {
                metrics.StartTime = DateTimeOffset.UtcNow;
            }
            timeline.StartedAt = DateTimeOffset.UtcNow;

            // Run the agent
            var response = await agent.InvokeAsync(testCase.Input, cancellationToken);
            
            // End timing
            if (metrics != null)
            {
                metrics.EndTime = DateTimeOffset.UtcNow;
                metrics.ModelUsed = options.ModelName;
            }
            timeline.TotalDuration = DateTimeOffset.UtcNow - timeline.StartedAt;

            // Extract tool usage if tracking
            if (options.TrackTools && response.RawMessages != null)
            {
                result.ToolUsage = ToolUsageExtractor.Extract(response.RawMessages);
                
                // Populate timeline from tool usage
                PopulateTimelineFromToolUsage(timeline, result.ToolUsage);
                
                if (metrics != null)
                {
                    metrics.ToolCallCount = result.ToolUsage.Count;
                    metrics.TotalToolTime = result.ToolUsage.TotalToolTime;
                }
                
                if (_logger.IsEnabled(LogLevel.Debug) && result.ToolUsage.Count > 0)
                {
                    _logger.LogDebug($"🔧 Tools Called: {result.ToolUsage.Count}");
                }
            }
            
            // Extract token usage and calculate cost
            if (metrics != null)
            {
                if (response.TokenUsage != null)
                {
                    // Use actual token usage from provider
                    metrics.PromptTokens = response.TokenUsage.PromptTokens;
                    metrics.CompletionTokens = response.TokenUsage.CompletionTokens;
                    metrics.TokensAreEstimated = false;
                }
                else
                {
                    // Fallback: estimate tokens from text when provider doesn't return usage
                    // This is common with Azure OpenAI via MAF SDK
                    metrics.PromptTokens = ModelPricing.EstimatePromptTokens(null, testCase.Input);
                    metrics.CompletionTokens = ModelPricing.EstimateTokensFromText(result.ActualOutput);
                    metrics.TokensAreEstimated = true;
                }
                
                // Calculate estimated cost (works with actual or estimated tokens)
                if (metrics.ModelUsed != null && metrics.PromptTokens.HasValue && metrics.CompletionTokens.HasValue)
                {
                    metrics.EstimatedCost = ModelPricing.EstimateCost(
                        metrics.ModelUsed,
                        metrics.PromptTokens.Value,
                        metrics.CompletionTokens.Value);
                }
            }
            
            // Capture performance metrics
            if (metrics != null)
            {
                result.Performance = metrics;
                _logger.LogDebug($"⏱️ {metrics}");
            }

            _logger.LogDebug($"📤 Output: {Truncate(response.Text, 200)}");

            // Evaluate with AI if criteria provided and evaluator available
            if (options.EvaluateResponse && _evaluator != null && testCase.EvaluationCriteria?.Any() == true)
            {
                var evaluation = await _evaluator.EvaluateAsync(
                    testCase.Input,
                    response.Text,
                    testCase.EvaluationCriteria,
                    cancellationToken);

                result.Score = evaluation.OverallScore;
                result.Passed = evaluation.OverallScore >= testCase.PassingScore;
                result.Details = evaluation.Summary;
                result.Suggestions = evaluation.Improvements.ToList();
                result.CriteriaResults = evaluation.CriteriaResults.ToList();
            }
            else
            {
                // No AI criteria - check for non-empty response and ExpectedOutputContains
                result.Passed = !string.IsNullOrWhiteSpace(response.Text);
                
                // Validate ExpectedOutputContains if provided
                if (result.Passed && !string.IsNullOrEmpty(testCase.ExpectedOutputContains))
                {
                    result.Passed = response.Text.Contains(testCase.ExpectedOutputContains, StringComparison.OrdinalIgnoreCase);
                    if (!result.Passed)
                    {
                        result.Details = $"Output did not contain expected substring: \"{testCase.ExpectedOutputContains}\"";
                    }
                }
                
                result.Score = result.Passed ? 100 : 0;
                if (string.IsNullOrEmpty(result.Details))
                {
                    result.Details = result.Passed ? "Agent produced output" : "Agent produced empty output";
                }
            }

            result.ActualOutput = response.Text;
            result.Timeline = timeline;
            
            // Build failure report if test failed
            if (!result.Passed)
            {
                result.Failure = BuildFailureReport(result, testCase, timeline);
                LogFailure(result);
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Score = 0;
            result.Details = $"Error: {ex.Message}";
            result.Error = ex;
            timeline.TotalDuration = DateTimeOffset.UtcNow - timeline.StartedAt;
            result.Timeline = timeline;
            
            // Build failure report for exception
            result.Failure = BuildExceptionFailureReport(ex, testCase, timeline);
            LogFailure(result);
        }

        return result;
    }
    
    /// <inheritdoc/>
    public async Task<TestResult> RunEvaluationStreamingAsync(
        IStreamableAgent agent,
        TestCase testCase,
        StreamingOptions? streamingOptions = null,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvaluationOptions();
        var result = new TestResult { TestName = testCase.Name };
        var metrics = new PerformanceMetrics { WasStreaming = true, ModelUsed = options.ModelName };
        var toolCalls = new Dictionary<string, ToolCallRecord>();
        var responseText = new System.Text.StringBuilder();
        var isFirstToken = true;
        var toolOrder = 0;
        var timeline = ToolCallTimeline.Create(testCase.Name);

        try
        {
            _logger.LogDebug($"📥 Input: \"{Truncate(testCase.Input, 100)}\"");
            _logger.LogDebug("🌊 Streaming...");

            metrics.StartTime = DateTimeOffset.UtcNow;
            timeline.StartedAt = DateTimeOffset.UtcNow;

            await foreach (var chunk in agent.InvokeStreamingAsync(testCase.Input, cancellationToken))
            {
                // Handle text content
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    if (isFirstToken)
                    {
                        metrics.TimeToFirstToken = DateTimeOffset.UtcNow - metrics.StartTime;
                        timeline.TimeToFirstToken = metrics.TimeToFirstToken.Value;
                        isFirstToken = false;
                        streamingOptions?.OnFirstToken?.Invoke(metrics.TimeToFirstToken.Value);
                    }
                    
                    responseText.Append(chunk.Text);
                    streamingOptions?.OnTextChunk?.Invoke(chunk.Text);
                }
                
                // Handle tool call start
                if (chunk.ToolCallStarted != null)
                {
                    toolOrder++;
                    var record = new ToolCallRecord
                    {
                        Name = chunk.ToolCallStarted.Name,
                        CallId = chunk.ToolCallStarted.CallId,
                        Arguments = chunk.ToolCallStarted.Arguments,
                        StartTime = DateTimeOffset.UtcNow,
                        Order = toolOrder
                    };
                    toolCalls[chunk.ToolCallStarted.CallId] = record;
                    
                    _logger.LogDebug($"🔧 Tool start: {record.Name}");
                    streamingOptions?.OnToolStart?.Invoke(record);
                }
                
                // Handle tool call complete
                if (chunk.ToolCallCompleted != null)
                {
                    if (toolCalls.TryGetValue(chunk.ToolCallCompleted.CallId, out var existingRecord))
                    {
                        existingRecord.EndTime = DateTimeOffset.UtcNow;
                        existingRecord.Result = chunk.ToolCallCompleted.Result;
                        existingRecord.Exception = chunk.ToolCallCompleted.Exception;
                        
                        var duration = existingRecord.Duration?.TotalMilliseconds ?? 0;
                        var level = existingRecord.HasError ? LogLevel.Warning : LogLevel.Debug;
                        _logger.Log(level, $"→ Tool done: {existingRecord.Name} ({duration:F0}ms)");
                        
                        // Add to timeline
                        timeline.AddInvocation(new ToolInvocation
                        {
                            ToolName = existingRecord.Name,
                            StartTime = (existingRecord.StartTime ?? timeline.StartedAt) - timeline.StartedAt,
                            Duration = existingRecord.Duration ?? TimeSpan.Zero,
                            Succeeded = !existingRecord.HasError,
                            ErrorMessage = existingRecord.Exception?.Message,
                            Arguments = existingRecord.GetArgumentsAsJson(),
                            Result = existingRecord.Result?.ToString()
                        });
                        
                        streamingOptions?.OnToolComplete?.Invoke(existingRecord);
                    }
                }
                
                // Extract token usage from final chunk
                if (chunk.IsComplete)
                {
                    if (chunk.Usage != null)
                    {
                        // Use actual token usage from provider
                        metrics.PromptTokens = chunk.Usage.PromptTokens;
                        metrics.CompletionTokens = chunk.Usage.CompletionTokens;
                        metrics.TokensAreEstimated = false;
                    }
                    else
                    {
                        // Fallback: estimate tokens when provider doesn't return usage
                        // This is common with Azure OpenAI streaming
                        metrics.PromptTokens = ModelPricing.EstimatePromptTokens(null, testCase.Input);
                        metrics.CompletionTokens = ModelPricing.EstimateTokensFromText(responseText.ToString());
                        metrics.TokensAreEstimated = true;
                    }
                    
                    // Calculate cost (works with actual or estimated tokens)
                    if (metrics.ModelUsed != null && metrics.PromptTokens.HasValue && metrics.CompletionTokens.HasValue)
                    {
                        metrics.EstimatedCost = ModelPricing.EstimateCost(
                            metrics.ModelUsed,
                            metrics.PromptTokens.Value,
                            metrics.CompletionTokens.Value);
                    }
                }
            }

            metrics.EndTime = DateTimeOffset.UtcNow;
            timeline.TotalDuration = metrics.EndTime - metrics.StartTime;
            
            // Build tool usage report
            result.ToolUsage = new ToolUsageReport();
            foreach (var call in toolCalls.Values.OrderBy(c => c.Order))
            {
                result.ToolUsage.AddCall(call);
            }
            
            // Calculate tool timing
            metrics.ToolCallCount = result.ToolUsage.Count;
            metrics.TotalToolTime = result.ToolUsage.TotalToolTime;
            
            result.Performance = metrics;
            result.Timeline = timeline;
            streamingOptions?.OnMetricsUpdate?.Invoke(metrics);

            _logger.LogDebug($"⏱️ {metrics}");
            _logger.LogDebug($"📤 Output: {Truncate(responseText.ToString(), 200)}");

            // Evaluate with AI if criteria provided
            if (options.EvaluateResponse && _evaluator != null && testCase.EvaluationCriteria?.Any() == true)
            {
                var evaluation = await _evaluator.EvaluateAsync(
                    testCase.Input,
                    responseText.ToString(),
                    testCase.EvaluationCriteria,
                    cancellationToken);

                result.Score = evaluation.OverallScore;
                result.Passed = evaluation.OverallScore >= testCase.PassingScore;
                result.Details = evaluation.Summary;
                result.Suggestions = evaluation.Improvements.ToList();
                result.CriteriaResults = evaluation.CriteriaResults.ToList();
            }
            else
            {
                // No AI criteria - check for non-empty response and ExpectedOutputContains
                var outputText = responseText.ToString();
                result.Passed = !string.IsNullOrWhiteSpace(outputText);
                
                // Validate ExpectedOutputContains if provided
                if (result.Passed && !string.IsNullOrEmpty(testCase.ExpectedOutputContains))
                {
                    result.Passed = outputText.Contains(testCase.ExpectedOutputContains, StringComparison.OrdinalIgnoreCase);
                    if (!result.Passed)
                    {
                        result.Details = $"Output did not contain expected substring: \"{testCase.ExpectedOutputContains}\"";
                    }
                }
                
                result.Score = result.Passed ? 100 : 0;
                if (string.IsNullOrEmpty(result.Details))
                {
                    result.Details = result.Passed ? "Agent produced output" : "Agent produced empty output";
                }
            }

            result.ActualOutput = responseText.ToString();
            
            // Build failure report if test failed
            if (!result.Passed)
            {
                result.Failure = BuildFailureReport(result, testCase, timeline);
                LogFailure(result);
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Score = 0;
            result.Details = $"Error: {ex.Message}";
            result.Error = ex;
            metrics.EndTime = DateTimeOffset.UtcNow;
            timeline.TotalDuration = metrics.EndTime - metrics.StartTime;
            result.Performance = metrics;
            result.Timeline = timeline;
            
            // Build failure report for exception
            result.Failure = BuildExceptionFailureReport(ex, testCase, timeline);
            LogFailure(result);
        }

        return result;
    }
    
    /// <summary>
    /// Run multiple test cases against an agent.
    /// </summary>
    public async Task<TestSummary> RunEvaluationSuiteAsync(
        string suiteName,
        IEvaluableAgent agent,
        IEnumerable<TestCase> testCases,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LogSuiteHeader(suiteName);

        var results = new List<TestResult>();
        foreach (var testCase in testCases)
        {
            _logger.LogInformation($"\n🧪 Test: {testCase.Name}");
            _logger.LogDebug(new string('-', 60));

            var result = await RunEvaluationAsync(agent, testCase, options, cancellationToken);
            results.Add(result);

            LogTestResult(result);
        }

        var summary = new TestSummary(suiteName, results);
        LogSummary(summary);

        return summary;
    }
    
    /// <inheritdoc/>
    public async Task<TestSummary> RunBatchAsync(
        IEvaluableAgent agent,
        IEnumerable<DatasetTestCase> testCases,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvaluationOptions();
        var results = new List<TestResult>();

        foreach (var datasetCase in testCases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var testCase = datasetCase.ToTestCase();
            _logger.LogInformation($"\n🧪 Test: {testCase.Name}");
            _logger.LogDebug(new string('-', 60));

            var result = await RunEvaluationAsync(agent, testCase, options, cancellationToken);
            results.Add(result);

            LogTestResult(result);
        }

        var summary = new TestSummary("BatchEvaluation", results);
        LogSummary(summary);

        return summary;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS - LOGGING
    // ═══════════════════════════════════════════════════════════════════════════
    
    private void LogSuiteHeader(string suiteName)
    {
        _logger.LogInformation("╔══════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation($"║  TEST SUITE: {suiteName.PadRight(52)}║");
        _logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝");
    }
    
    private void LogTestResult(TestResult result)
    {
        if (result.Passed)
        {
            _logger.LogInformation($"\n✅ PASSED - Score: {result.Score}/100");
            _logger.LogDebug($"   {result.Details}");
        }
        else
        {
            // Failure is already logged via LogFailure when result.Failure is set
            if (result.Failure == null)
            {
                _logger.LogError($"\n❌ FAILED - Score: {result.Score}/100");
                _logger.LogError($"   {result.Details}");
                
                if (result.Suggestions?.Any() == true)
                {
                    _logger.LogWarning("   💡 Suggestions:");
                    foreach (var suggestion in result.Suggestions.Take(3))
                    {
                        _logger.LogWarning($"      • {suggestion}");
                    }
                }
            }
        }
    }
    
    private void LogSummary(TestSummary summary)
    {
        _logger.LogInformation("\n╔══════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║                        TEST SUMMARY                              ║");
        _logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝\n");

        _logger.LogInformation($"   Suite: {summary.SuiteName}");
        _logger.LogInformation($"   Tests Passed: {summary.PassedCount}/{summary.TotalCount}");
        _logger.LogInformation($"   Average Score: {summary.AverageScore:F1}/100");

        foreach (var result in summary.Results)
        {
            var status = result.Passed ? "✅" : "❌";
            var level = result.Passed ? LogLevel.Information : LogLevel.Error;
            _logger.Log(level, $"   {status} {result.TestName}: {result.Score}/100");
        }

        if (summary.AllPassed)
        {
            _logger.LogInformation("\n   🎉 All tests passed!\n");
        }
        else
        {
            _logger.LogWarning($"\n   ⚠️ {summary.FailedCount} test(s) need attention.\n");
        }
    }
    
    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
    
    // ═══════════════════════════════════════════════════════════════════════════
    // TIMELINE & FAILURE REPORT HELPERS
    // ═══════════════════════════════════════════════════════════════════════════
    
    private static void PopulateTimelineFromToolUsage(ToolCallTimeline timeline, ToolUsageReport? toolUsage)
    {
        if (toolUsage == null) return;
        
        foreach (var call in toolUsage.Calls.OrderBy(c => c.Order))
        {
            var invocation = new ToolInvocation
            {
                ToolName = call.Name,
                StartTime = (call.StartTime ?? timeline.StartedAt) - timeline.StartedAt,
                Duration = call.Duration ?? TimeSpan.Zero,
                Succeeded = !call.HasError,
                ErrorMessage = call.Exception?.Message,
                Arguments = call.GetArgumentsAsJson(),
                Result = call.Result?.ToString()
            };
            timeline.AddInvocation(invocation);
        }
    }
    
    private static FailureReport BuildFailureReport(TestResult result, TestCase testCase, ToolCallTimeline timeline)
    {
        // Determine the primary failure reason for the headline
        var headline = result.Score < testCase.PassingScore
            ? $"Test '{testCase.Name}' scored {result.Score}/100 (threshold: {testCase.PassingScore})"
            : $"Test '{testCase.Name}' did not meet passing criteria";
        
        var report = new FailureReport
        {
            WhyItFailed = headline,
            MetricName = testCase.Name,
            Score = result.Score,
            Timeline = timeline
        };
        
        // Add score-related reason
        if (result.Score < testCase.PassingScore)
        {
            report.AddReason("Score", $"Score {result.Score} below passing threshold {testCase.PassingScore}", FailureSeverity.Error);
        }
        
        // Add empty output reason
        if (string.IsNullOrWhiteSpace(result.ActualOutput))
        {
            report.AddReason("Output", "Agent produced empty or whitespace-only output", FailureSeverity.Critical);
            report.AddSuggestion("Verify Input", "Verify agent is receiving input correctly", 0.7);
            report.AddSuggestion("Check Tools", "Check if agent tools are configured properly", 0.6);
        }
        
        // Add failed criteria reasons
        if (result.CriteriaResults?.Any() == true)
        {
            foreach (var criteria in result.CriteriaResults.Where(c => !c.Met))
            {
                report.AddReason("Criteria", $"'{criteria.Criterion}' not met: {criteria.Explanation}", FailureSeverity.Error);
            }
        }
        
        // Add tool failure reasons
        foreach (var tool in timeline.Invocations.Where(i => !i.Succeeded))
        {
            report.AddReason("ToolError", $"Tool '{tool.ToolName}' failed: {tool.ErrorMessage ?? "Unknown error"}", FailureSeverity.Error);
        }
        if (timeline.Invocations.Any(i => !i.Succeeded))
        {
            report.AddSuggestion("Review Tools", "Review tool implementations for errors", 0.8);
        }
        
        // Add suggestions from evaluation
        if (result.Suggestions?.Any() == true)
        {
            foreach (var suggestion in result.Suggestions.Distinct())
            {
                report.AddSuggestion("Evaluation Suggestion", suggestion, 0.5);
            }
        }
        
        // If no reasons were added, add a generic one
        if (!report.Reasons.Any())
        {
            report.AddReason("Unknown", result.Details ?? "Test did not meet passing criteria", FailureSeverity.Error);
        }
        
        return report;
    }
    
    private static FailureReport BuildExceptionFailureReport(Exception ex, TestCase testCase, ToolCallTimeline timeline)
    {
        var report = new FailureReport
        {
            WhyItFailed = $"Test '{testCase.Name}' failed with exception: {ex.GetType().Name}",
            MetricName = testCase.Name,
            Score = 0,
            Timeline = timeline
        };
        
        // Add exception reason
        report.AddReason(new FailureReason
        {
            Category = "Exception",
            Description = ex.Message,
            Severity = FailureSeverity.Critical,
            Source = testCase.Name,
            TechnicalDetails = ex.StackTrace
        });
        
        if (ex.InnerException != null)
        {
            report.AddReason("InnerException", $"Inner exception: {ex.InnerException.Message}", FailureSeverity.Error);
        }
        
        // Add base suggestions
        report.AddSuggestion("Check Configuration", "Check agent configuration and initialization", 0.6);
        report.AddSuggestion("Verify Dependencies", "Verify all required dependencies are available", 0.5);
        report.AddSuggestion("Review Stack Trace", "Review the stack trace for the root cause", 0.8);
        
        // Add specific suggestions based on exception type
        if (ex is HttpRequestException)
        {
            report.AddSuggestion("Check Network", "Check network connectivity and API endpoint availability", 0.7);
            report.AddSuggestion("Verify API Keys", "Verify API keys and authentication are configured correctly", 0.7);
        }
        else if (ex is TaskCanceledException or OperationCanceledException)
        {
            report.AddSuggestion("Increase Timeout", "Increase timeout if operation is timing out", 0.8);
            report.AddSuggestion("Check Cancellation", "Check if cancellation was requested intentionally", 0.5);
        }
        else if (ex is ArgumentException or ArgumentNullException)
        {
            report.AddSuggestion("Verify Input", "Verify test case input is valid", 0.7);
            report.AddSuggestion("Check Parameters", "Check if required parameters are provided", 0.6);
        }
        
        return report;
    }
    
    private void LogFailure(TestResult result)
    {
        if (result.Failure == null) return;
        
        _logger.LogError("═══════════════════════════════════════════════════════════════");
        _logger.LogError($"❌ TEST FAILED: {result.TestName}");
        _logger.LogError("═══════════════════════════════════════════════════════════════");
        
        // Reasons
        _logger.LogError("\n📋 FAILURE REASONS:");
        foreach (var reason in result.Failure.Reasons)
        {
            var severityIcon = reason.Severity switch
            {
                FailureSeverity.Critical => "🔴",
                FailureSeverity.Error => "🟠",
                FailureSeverity.Warning => "🟡",
                _ => "⚪"
            };
            _logger.LogError($"   {severityIcon} [{reason.Category}] {reason.Description}");
        }
        
        // Timeline if present
        if (result.Timeline != null && result.Timeline.Invocations.Any())
        {
            _logger.LogInformation("\n📊 TOOL CALL TIMELINE:");
            _logger.LogInformation(result.Timeline.ToAsciiDiagram());
        }
        
        // Suggestions
        if (result.Failure.Suggestions.Any())
        {
            _logger.LogWarning("\n💡 SUGGESTIONS:");
            foreach (var suggestion in result.Failure.Suggestions)
            {
                _logger.LogWarning($"   → {suggestion.Title}: {suggestion.Description}");
            }
        }
        
        // Stack trace for exceptions (use result.Error since FailureReport doesn't store Exception)
        if (result.Error != null)
        {
            _logger.LogDebug("\n🔍 STACK TRACE:");
            _logger.LogDebug(result.Error.StackTrace ?? "(no stack trace)");
        }
        
        _logger.LogError("═══════════════════════════════════════════════════════════════\n");
    }
}
