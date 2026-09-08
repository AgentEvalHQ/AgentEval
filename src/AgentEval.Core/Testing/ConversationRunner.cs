// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;

namespace AgentEval.Testing;

// This file declares the obsolete AgentEval.Testing.AssertionResult and is its remaining consumer,
// so CS0618 here is expected rather than actionable. Callers outside this file still get the
// deprecation warning that points them at AgentEval.Output.AssertionResult.
#pragma warning disable CS0618

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
/// <remarks>
/// <para>
/// <b>Obsolete alias.</b> The canonical assertion-result type is
/// <see cref="AgentEval.Output.AssertionResult"/>, which carries the same three members plus a
/// three-valued <see cref="AgentEval.Output.AssertionOutcome"/> so a check that could not decide
/// is not reported as one that held. This record stays for source compatibility and converts to
/// and from the canonical type implicitly, so existing code keeps compiling:
/// </para>
/// <code>
/// AgentEval.Output.AssertionResult canonical = new AssertionResult("Name", true);
/// AssertionResult legacy = canonical;
/// </code>
/// </remarks>
[Obsolete("Use AgentEval.Output.AssertionResult, which adds a three-valued Outcome " +
          "(Passed / Failed / Inconclusive). This type converts to and from it implicitly.")]
public record AssertionResult(string Name, bool Passed, string? Message = null)
{
    /// <summary>Converts to the canonical <see cref="AgentEval.Output.AssertionResult"/>.</summary>
    /// <param name="result">The legacy result. May be <see langword="null"/>.</param>
    public static implicit operator AgentEval.Output.AssertionResult?(AssertionResult? result)
        => result is null ? null : new AgentEval.Output.AssertionResult(result.Name, result.Passed, result.Message);

    /// <summary>Converts from the canonical <see cref="AgentEval.Output.AssertionResult"/>.</summary>
    /// <param name="result">The canonical result. May be <see langword="null"/>.</param>
    /// <remarks>
    /// Lossy by construction: this shape has no place to put
    /// <see cref="AgentEval.Output.AssertionOutcome.Inconclusive"/>, which arrives here as
    /// <c>Passed == false</c> — the safe direction.
    /// </remarks>
    public static implicit operator AssertionResult?(AgentEval.Output.AssertionResult? result)
        => result is null ? null : new AssertionResult(result.Assertion, result.Passed, result.Message);

    /// <summary>Converts to the canonical type explicitly.</summary>
    /// <returns>The canonical result.</returns>
    public AgentEval.Output.AssertionResult ToAssertionResult()
        => new(Name, Passed, Message);
}

/// <summary>
/// Runs scripted multi-turn conversations against an evaluable agent.
/// </summary>
/// <remarks>
/// Accepts any <see cref="IEvaluableAgent"/> implementation. If you have an 
/// <c>IChatClient</c>, wrap it with <see cref="ChatClientAgentAdapter"/> first.
/// </remarks>
public class ConversationRunner
{
    private readonly IEvaluableAgent _agent;
    private readonly ConversationRunnerOptions _options;

    /// <summary>
    /// Initializes a new conversation runner.
    /// </summary>
    /// <param name="agent">The evaluable agent to converse with.</param>
    /// <param name="options">Optional configuration options.</param>
    public ConversationRunner(IEvaluableAgent agent, ConversationRunnerOptions? options = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
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
        var aborted = false;

        try
        {
            foreach (var turn in testCase.Turns)
            {
                ct.ThrowIfCancellationRequested();
                var turnStart = DateTime.UtcNow;

                if (string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    result.ActualTurns.Add(turn);

                    AgentResponse agentResponse;
                    try
                    {
                        // Honor MaxRetries on agent invocation (BUG-36 — was a dead option).
                        agentResponse = await InvokeWithRetryAsync(turn.Content, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Preserve the FULL exception and record an explicit failed assertion so
                        // callers can distinguish an infrastructure crash from a legitimate failure —
                        // previously the error was reduced to ex.Message with empty Assertions
                        // (BUG-36).
                        result.Error = ex.ToString();
                        result.Assertions.Add(new AssertionResult(
                            "AgentInvocation",
                            false,
                            $"Agent invocation failed after {_options.MaxRetries + 1} attempt(s): " +
                            $"{ex.GetType().Name}: {ex.Message}"));
                        result.TurnDurations.Add(DateTime.UtcNow - turnStart);

                        // Honor ContinueOnError (BUG-36 — was a dead option): keep processing the
                        // remaining turns, otherwise stop and finalize with the recorded failure.
                        if (_options.ContinueOnError)
                            continue;
                        aborted = true;
                        break;
                    }

                    var toolCalls = ExtractToolCalls(agentResponse);
                    result.ActualTurns.Add(Turn.Assistant(agentResponse.Text, toolCalls.ToArray()));
                    foreach (var tc in toolCalls)
                    {
                        result.ToolsCalled.Add(tc.Name);
                    }

                    result.TurnDurations.Add(DateTime.UtcNow - turnStart);
                }
                else
                {
                    switch (turn.Role.ToLowerInvariant())
                    {
                        case "system": // System prompts should be configured on the agent directly.
                        case "tool":   // Tool responses are handled internally by the agent.
                            result.ActualTurns.Add(turn);
                            break;
                        case "assistant":
                            // Expected assistant turns are for validation, not sent to agent.
                            break;
                    }

                    result.TurnDurations.Add(DateTime.UtcNow - turnStart);
                }
            }

            result.Duration = DateTime.UtcNow - startTime;
            if (aborted)
            {
                // An explicit AgentInvocation failure assertion was already recorded above.
                result.Success = false;
            }
            else
            {
                RunAssertions(testCase, result);
                result.Success = result.Assertions.All(a => a.Passed);
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "Conversation was cancelled";
            result.Success = false;
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected (non-invocation) error: preserve the full exception and record an explicit
            // failed assertion rather than reducing it to ex.Message with empty Assertions (BUG-36).
            result.Error = ex.ToString();
            result.Assertions.Add(new AssertionResult("RunnerError", false, $"{ex.GetType().Name}: {ex.Message}"));
            result.Success = false;
        }

        return result;
    }

    /// <summary>
    /// Invokes the agent, retrying up to <see cref="ConversationRunnerOptions.MaxRetries"/> times on
    /// failure before rethrowing the last exception. <see cref="OperationCanceledException"/> is
    /// never retried (BUG-36).
    /// </summary>
    private async Task<AgentResponse> InvokeWithRetryAsync(string content, CancellationToken ct)
    {
        var attempts = _options.MaxRetries + 1;
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _agent.InvokeAsync(content, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex; // retry until attempts are exhausted
            }
        }

        throw last!; // all attempts failed
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

    private static List<ToolCallInfo> ExtractToolCalls(AgentResponse response)
    {
        var toolCalls = new List<ToolCallInfo>();
        
        if (response.RawMessages == null)
            return toolCalls;

        foreach (var rawMsg in response.RawMessages)
        {
            if (rawMsg is ChatMessage chatMessage && chatMessage.Contents != null)
            {
                foreach (var content in chatMessage.Contents)
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

#pragma warning restore CS0618
