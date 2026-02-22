// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Testing;

/// <summary>
/// Result of running a conversational test case.
/// </summary>
public class ConversationResult
{
    /// <summary>The test case that was executed.</summary>
    public required ConversationalTestCase TestCase { get; init; }
    
    /// <summary>Whether the conversation completed successfully.</summary>
    public bool Success { get; set; }
    
    /// <summary>The actual conversation turns including agent responses.</summary>
    public List<Turn> ActualTurns { get; set; } = new();
    
    /// <summary>Tools that were actually called during the conversation.</summary>
    public List<string> ToolsCalled { get; set; } = new();
    
    /// <summary>Total duration of the conversation.</summary>
    public TimeSpan Duration { get; set; }
    
    /// <summary>Duration per turn.</summary>
    public List<TimeSpan> TurnDurations { get; set; } = new();
    
    /// <summary>Any error that occurred.</summary>
    public string? Error { get; set; }
    
    /// <summary>Assertion results.</summary>
    public List<AssertionResult> Assertions { get; set; } = new();
}

/// <summary>
/// Result of a single assertion check.
/// </summary>
public record AssertionResult(string Name, bool Passed, string? Message = null);

/// <summary>
/// Runs scripted multi-turn conversations against an agent.
/// </summary>
public class ConversationRunner
{
    private readonly IChatClient _chatClient;
    private readonly ConversationRunnerOptions _options;

    /// <summary>
    /// Initializes a new conversation runner.
    /// </summary>
    /// <param name="chatClient">The chat client to use for agent interactions.</param>
    /// <param name="options">Optional configuration options.</param>
    public ConversationRunner(IChatClient chatClient, ConversationRunnerOptions? options = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? new ConversationRunnerOptions();
    }

    /// <summary>
    /// Runs a single conversational test case.
    /// </summary>
    public async Task<ConversationResult> RunAsync(
        ConversationalTestCase testCase, 
        CancellationToken ct = default)
    {
        var result = new ConversationResult { TestCase = testCase };
        var startTime = DateTime.UtcNow;
        var messages = new List<ChatMessage>();

        try
        {
            foreach (var turn in testCase.Turns)
            {
                ct.ThrowIfCancellationRequested();
                
                var turnStart = DateTime.UtcNow;

                switch (turn.Role.ToLowerInvariant())
                {
                    case "system":
                        messages.Add(new ChatMessage(ChatRole.System, turn.Content));
                        result.ActualTurns.Add(turn);
                        break;

                    case "user":
                        messages.Add(new ChatMessage(ChatRole.User, turn.Content));
                        result.ActualTurns.Add(turn);
                        
                        // Get agent response
                        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct);
                        
                        // Process response
                        var assistantContent = response.Text ?? "";
                        var toolCalls = ExtractToolCalls(response);
                        
                        messages.Add(new ChatMessage(ChatRole.Assistant, assistantContent));
                        result.ActualTurns.Add(Turn.Assistant(assistantContent, toolCalls.ToArray()));
                        
                        foreach (var tc in toolCalls)
                        {
                            result.ToolsCalled.Add(tc.Name);
                        }
                        break;

                    case "tool":
                        // Tool responses are injected as-is (simulated tool execution)
                        messages.Add(new ChatMessage(ChatRole.Tool, turn.Content));
                        result.ActualTurns.Add(turn);
                        break;

                    case "assistant":
                        // Expected assistant turns are for validation, not sent to model
                        // We just record them for comparison
                        break;
                }

                result.TurnDurations.Add(DateTime.UtcNow - turnStart);
            }

            result.Duration = DateTime.UtcNow - startTime;
            
            // Run assertions
            RunAssertions(testCase, result);
            result.Success = result.Assertions.All(a => a.Passed);
        }
        catch (OperationCanceledException)
        {
            result.Error = "Conversation was cancelled";
            result.Success = false;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.Success = false;
        }

        return result;
    }

    /// <summary>
    /// Runs multiple conversational test cases.
    /// </summary>
    public async Task<IReadOnlyList<ConversationResult>> RunAllAsync(
        IEnumerable<ConversationalTestCase> testCases,
        CancellationToken ct = default)
    {
        var results = new List<ConversationResult>();
        
        foreach (var testCase in testCases)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunAsync(testCase, ct);
            results.Add(result);
        }

        return results;
    }

    private static List<ToolCallInfo> ExtractToolCalls(ChatResponse response)
    {
        var toolCalls = new List<ToolCallInfo>();
        
        foreach (var message in response.Messages)
        {
            if (message.Contents != null)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        var args = new Dictionary<string, object?>();
                        if (fcc.Arguments != null)
                        {
                            foreach (var kvp in fcc.Arguments)
                            {
                                args[kvp.Key] = kvp.Value;
                            }
                        }
                        toolCalls.Add(new ToolCallInfo(fcc.Name, args, fcc.CallId));
                    }
                }
            }
        }

        return toolCalls;
    }

    private void RunAssertions(ConversationalTestCase testCase, ConversationResult result)
    {
        // Check expected tools
        if (testCase.ExpectedTools != null && testCase.ExpectedTools.Count > 0)
        {
            var missingTools = testCase.ExpectedTools.Except(result.ToolsCalled).ToList();
            var allToolsCalled = missingTools.Count == 0;
            
            result.Assertions.Add(new AssertionResult(
                "ExpectedTools",
                allToolsCalled,
                allToolsCalled ? null : $"Missing tools: {string.Join(", ", missingTools)}"
            ));
        }

        // Check duration constraint
        if (testCase.MaxDuration.HasValue)
        {
            var withinLimit = result.Duration <= testCase.MaxDuration.Value;
            result.Assertions.Add(new AssertionResult(
                "MaxDuration",
                withinLimit,
                withinLimit ? null : $"Duration {result.Duration} exceeded limit {testCase.MaxDuration.Value}"
            ));
        }

        // Check conversation completeness (all user turns got responses)
        var userTurnCount = testCase.Turns.Count(t => t.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        var assistantTurnCount = result.ActualTurns.Count(t => t.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
        var allResponded = assistantTurnCount >= userTurnCount;
        
        result.Assertions.Add(new AssertionResult(
            "ConversationCompleteness",
            allResponded,
            allResponded ? null : $"Expected {userTurnCount} responses, got {assistantTurnCount}"
        ));
    }
}

/// <summary>
/// Configuration options for the conversation runner.
/// </summary>
public class ConversationRunnerOptions
{
    /// <summary>Timeout per turn.</summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(30);
    
    /// <summary>Whether to continue running turns after an error.</summary>
    public bool ContinueOnError { get; set; } = false;
    
    /// <summary>Maximum number of retries per turn.</summary>
    public int MaxRetries { get; set; } = 0;
}
