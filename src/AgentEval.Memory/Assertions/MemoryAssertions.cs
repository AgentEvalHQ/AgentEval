// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using AgentEval.Assertions;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Assertions;

/// <summary>
/// Extension methods to enable fluent assertions on memory evaluation results.
/// Follows AgentEval's established assertion patterns.
/// </summary>
public static class MemoryAssertions
{
    /// <summary>
    /// Entry point for fluent assertions on MemoryEvaluationResult.
    /// </summary>
    [StackTraceHidden]
    public static MemoryEvaluationAssertions Should(this MemoryEvaluationResult result)
        => new(result);

    /// <summary>
    /// Entry point for fluent assertions on MemoryQueryResult.
    /// </summary>
    [StackTraceHidden]
    public static MemoryQueryAssertions Should(this MemoryQueryResult result)
        => new(result);
}

/// <summary>
/// Fluent assertions for MemoryEvaluationResult.
/// </summary>
public class MemoryEvaluationAssertions
{
    private readonly MemoryEvaluationResult _result;

    public MemoryEvaluationAssertions(MemoryEvaluationResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// Asserts that the overall memory score is at least the specified value.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions HaveOverallScoreAtLeast(double minimumScore, string? because = null)
    {
        if (_result.OverallScore < minimumScore)
        {
            var suggestions = new List<string>();
            
            if (_result.MissingFacts.Count > 0)
            {
                suggestions.Add($"Agent failed to recall {_result.MissingFacts.Count} facts: {string.Join(", ", _result.MissingFacts.Take(3).Select(f => $"'{f.Content}'"))}");
                suggestions.Add("Consider improving the agent's memory system or reducing information complexity");
            }
            
            if (_result.ForbiddenFound.Count > 0)
            {
                suggestions.Add($"Agent incorrectly recalled {_result.ForbiddenFound.Count} forbidden facts");
                suggestions.Add("Check memory isolation and temporal boundaries");
            }
            
            if (_result.PassedQueries < _result.TotalQueries)
            {
                var failedCount = _result.TotalQueries - _result.PassedQueries;
                suggestions.Add($"{failedCount} out of {_result.TotalQueries} queries failed their minimum thresholds");
            }

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected memory score to be at least {minimumScore}%, but was {_result.OverallScore:F1}%",
                expected: $"Score >= {minimumScore}%",
                actual: $"Score = {_result.OverallScore:F1}%",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that all memory queries passed their minimum score thresholds.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions HaveAllQueriesPassed(string? because = null)
    {
        var failedQueries = _result.QueryResults.Where(r => !r.Passed).ToArray();
        
        if (failedQueries.Length > 0)
        {
            var suggestions = new List<string>
            {
                $"Failed queries: {string.Join(", ", failedQueries.Take(3).Select(q => $"'{q.Query.Question}' ({q.Score:F1}%)"))}",
                "Consider adjusting query minimum score thresholds or improving agent memory"
            };
            
            if (failedQueries.Length > 3)
            {
                suggestions.Add($"... and {failedQueries.Length - 3} more queries failed");
            }

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected all {_result.TotalQueries} queries to pass, but {failedQueries.Length} failed",
                expected: $"All {_result.TotalQueries} queries passed",
                actual: $"{_result.PassedQueries}/{_result.TotalQueries} queries passed",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that at least the specified number of queries passed.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions HaveAtLeastQueriesPassed(int minimumPassed, string? because = null)
    {
        if (_result.PassedQueries < minimumPassed)
        {
            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected at least {minimumPassed} queries to pass, but only {_result.PassedQueries} passed",
                expected: $"At least {minimumPassed} queries passed",
                actual: $"{_result.PassedQueries} queries passed",
                suggestions: new[] { $"Success rate: {_result.SuccessRate:F1}%" },
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the agent remembered all specified facts.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions HaveRememberedFacts(IEnumerable<MemoryFact> expectedFacts, string? because = null)
    {
        var expected = expectedFacts.ToArray();
        var missing = expected.Except(_result.FoundFacts).ToArray();
        
        if (missing.Length > 0)
        {
            var suggestions = new List<string>
            {
                $"Missing facts: {string.Join(", ", missing.Take(3).Select(f => $"'{f.Content}'"))}",
                "Agent may have memory retention issues or facts were not established clearly"
            };
            
            if (missing.Length > 3)
            {
                suggestions.Add($"... and {missing.Length - 3} more facts were missing");
            }

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected agent to remember all {expected.Length} facts, but {missing.Length} were missing",
                expected: $"All {expected.Length} facts remembered",
                actual: $"{_result.FoundFacts.Count}/{expected.Length} facts remembered",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the agent did not recall any forbidden facts.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions NotHaveRecalledForbiddenFacts(string? because = null)
    {
        if (_result.ForbiddenFound.Count > 0)
        {
            var suggestions = new List<string>
            {
                $"Incorrectly recalled facts: {string.Join(", ", _result.ForbiddenFound.Take(3).Select(f => $"'{f.Content}'"))}",
                "Check memory isolation, temporal boundaries, or session reset implementation"
            };
            
            if (_result.ForbiddenFound.Count > 3)
            {
                suggestions.Add($"... and {_result.ForbiddenFound.Count - 3} more forbidden facts");
            }

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected no forbidden facts to be recalled, but {_result.ForbiddenFound.Count} were found",
                expected: "No forbidden facts recalled",
                actual: $"{_result.ForbiddenFound.Count} forbidden facts recalled",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the memory evaluation completed within the specified time.
    /// </summary>
    [StackTraceHidden]  
    public MemoryEvaluationAssertions HaveCompletedWithin(TimeSpan maxDuration, string? because = null)
    {
        if (_result.Duration > maxDuration)
        {
            var suggestions = new List<string>
            {
                $"Query count: {_result.TotalQueries}, Tokens used: {_result.TokensUsed}",
                "Consider reducing scenario complexity or using faster evaluation methods",
                "For simple cases, try QuickMemoryCheckAsync instead of full LLM evaluation"
            };

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected memory evaluation to complete within {maxDuration:g}, but took {_result.Duration:g}",
                expected: $"Duration <= {maxDuration:g}",
                actual: $"Duration = {_result.Duration:g}",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the memory evaluation used fewer than the specified number of tokens.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions HaveUsedFewerTokens(int maxTokens, string? because = null)
    {
        if (_result.TokensUsed > maxTokens)
        {
            var suggestions = new List<string>
            {
                $"Cost estimate: ${_result.EstimatedCost:F4}, Queries: {_result.TotalQueries}",
                "Consider using string-based matching for simple fact verification",
                "Reduce scenario complexity or query count to lower token usage"
            };

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected memory evaluation to use fewer than {maxTokens} tokens, but used {_result.TokensUsed}",
                expected: $"Tokens < {maxTokens}",
                actual: $"Tokens = {_result.TokensUsed}",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the memory evaluation cost less than the specified amount.
    /// </summary>
    [StackTraceHidden]
    public MemoryEvaluationAssertions HaveCostLessThan(decimal maxCost, string? because = null)
    {
        if (_result.EstimatedCost > maxCost)
        {
            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected memory evaluation to cost less than ${maxCost:F4}, but cost ${_result.EstimatedCost:F4}",
                expected: $"Cost < ${maxCost:F4}",
                actual: $"Cost = ${_result.EstimatedCost:F4}",
                suggestions: new[] 
                {
                    $"Tokens used: {_result.TokensUsed}, Queries: {_result.TotalQueries}",
                    "Use cheaper models or reduce LLM-based evaluations for cost optimization"
                },
                because: because
            ));
        }
        
        return this;
    }
}

/// <summary>
/// Fluent assertions for individual MemoryQueryResult.
/// </summary>
public class MemoryQueryAssertions
{
    private readonly MemoryQueryResult _result;

    public MemoryQueryAssertions(MemoryQueryResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// Asserts that the query passed its minimum score threshold.
    /// </summary>
    [StackTraceHidden]
    public MemoryQueryAssertions HavePassed(string? because = null)
    {
        if (!_result.Passed)
        {
            var suggestions = new List<string>
            {
                $"Score: {_result.Score:F1}%, Threshold: {_result.Query.MinimumScore}%",
                $"Missing facts: {_result.MissingFacts.Count}, Found facts: {_result.FoundFacts.Count}"
            };
            
            if (_result.MissingFacts.Count > 0)
            {
                suggestions.Add($"Agent failed to recall: {string.Join(", ", _result.MissingFacts.Take(2).Select(f => $"'{f.Content}'"))}");
            }

            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected query '{_result.Query.Question}' to pass (>= {_result.Query.MinimumScore}%), but scored {_result.Score:F1}%",
                expected: $"Score >= {_result.Query.MinimumScore}%",
                actual: $"Score = {_result.Score:F1}%",
                suggestions: suggestions,
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the query achieved at least the specified score.
    /// </summary>
    [StackTraceHidden]
    public MemoryQueryAssertions HaveScoreAtLeast(double minimumScore, string? because = null)
    {
        if (_result.Score < minimumScore)
        {
            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected query score to be at least {minimumScore}%, but was {_result.Score:F1}%",
                expected: $"Score >= {minimumScore}%",
                actual: $"Score = {_result.Score:F1}%",
                suggestions: new[] { $"Query: '{_result.Query.Question}'" },
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the query found all expected facts.
    /// </summary>
    [StackTraceHidden]
    public MemoryQueryAssertions HaveFoundAllExpectedFacts(string? because = null)
    {
        var missing = _result.MissingFacts;
        
        if (missing.Count > 0)
        {
            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected all {_result.Query.ExpectedFacts.Count} facts to be found, but {missing.Count} were missing",
                expected: $"All {_result.Query.ExpectedFacts.Count} facts found",
                actual: $"{_result.FoundFacts.Count}/{_result.Query.ExpectedFacts.Count} facts found",
                suggestions: new[] 
                {
                    $"Missing: {string.Join(", ", missing.Take(3).Select(f => $"'{f.Content}'"))}",
                    $"Agent response: '{_result.Response.Substring(0, Math.Min(100, _result.Response.Length))}...'"
                },
                because: because
            ));
        }
        
        return this;
    }

    /// <summary>
    /// Asserts that the query response contains specific text.
    /// </summary>
    [StackTraceHidden]
    public MemoryQueryAssertions HaveResponseContaining(string expectedText, string? because = null)
    {
        if (!_result.Response.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
        {
            AgentEvalScope.FailWith(new MemoryAssertionException(
                $"Expected response to contain '{expectedText}', but it did not",
                expected: $"Response containing '{expectedText}'",
                actual: $"Response: '{_result.Response.Substring(0, Math.Min(200, _result.Response.Length))}...'",
                suggestions: new[] 
                {
                    "Check if the agent recalled the information using different wording",
                    "Consider using LLM-based judgment instead of exact text matching"
                },
                because: because
            ));
        }
        
        return this;
    }
}