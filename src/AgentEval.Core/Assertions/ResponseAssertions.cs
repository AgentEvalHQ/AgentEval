// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertions for response text.
/// </summary>
public class ResponseAssertions
{
    private readonly string _response;
    private readonly string? _subjectName;
    
    public ResponseAssertions(string response, string? subjectName = null)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
        _subjectName = subjectName;
    }
    
    /// <summary>Assert response contains a substring (case-insensitive by default).</summary>
    /// <param name="substring">The substring to search for.</param>
    /// <param name="caseSensitive">Whether the comparison is case-sensitive.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions Contain(string substring, bool caseSensitive = false, string? because = null)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!_response.Contains(substring, comparison))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to contain the specified substring.",
                    responsePreview: Truncate(_response, 200),
                    expected: $"Contains \"{substring}\"",
                    actual: $"Substring not found",
                    suggestions: caseSensitive ? new[] { "Try case-insensitive search with caseSensitive: false" } : null,
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response contains all specified substrings.</summary>
    /// <param name="substrings">The substrings that must all be present.</param>
    [StackTraceHidden]
    public ResponseAssertions ContainAll(params string[] substrings)
    {
        return ContainAll(null, substrings);
    }
    
    /// <summary>Assert response contains all specified substrings.</summary>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    /// <param name="substrings">The substrings that must all be present.</param>
    [StackTraceHidden]
    public ResponseAssertions ContainAll(string? because, params string[] substrings)
    {
        var missing = substrings.Where(s => 
            !_response.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        
        if (missing.Count > 0)
        {
            var found = substrings.Except(missing).ToList();
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to contain all specified substrings.",
                    responsePreview: Truncate(_response, 200),
                    expected: $"Contains all: [{string.Join(", ", substrings.Select(s => $"\"{s}\""))}]",
                    actual: $"Missing: [{string.Join(", ", missing.Select(s => $"\"{s}\""))}]",
                    context: found.Count > 0 ? $"Found: [{string.Join(", ", found.Select(s => $"\"{s}\""))}]" : null,
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response contains any of the specified substrings.</summary>
    /// <param name="substrings">The substrings where at least one must be present.</param>
    [StackTraceHidden]
    public ResponseAssertions ContainAny(params string[] substrings)
    {
        return ContainAny(null, substrings);
    }
    
    /// <summary>Assert response contains any of the specified substrings.</summary>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    /// <param name="substrings">The substrings where at least one must be present.</param>
    [StackTraceHidden]
    public ResponseAssertions ContainAny(string? because, params string[] substrings)
    {
        if (!substrings.Any(s => _response.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to contain at least one of the specified substrings.",
                    responsePreview: Truncate(_response, 200),
                    expected: $"Contains any: [{string.Join(", ", substrings.Select(s => $"\"{s}\""))}]",
                    actual: "None found",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response does NOT contain a substring.</summary>
    /// <param name="substring">The substring that should not be present.</param>
    /// <param name="caseSensitive">Whether the comparison is case-sensitive.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions NotContain(string substring, bool caseSensitive = false, string? because = null)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (_response.Contains(substring, comparison))
        {
            // Find where it was found
            var index = _response.IndexOf(substring, comparison);
            var contextStart = Math.Max(0, index - 20);
            var contextEnd = Math.Min(_response.Length, index + substring.Length + 20);
            var foundContext = _response[contextStart..contextEnd];
            
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    $"Expected response NOT to contain \"{substring}\".",
                    responsePreview: Truncate(_response, 200),
                    expected: $"Does not contain \"{substring}\"",
                    actual: $"Found at position {index}",
                    context: $"...{foundContext}...",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response matches a regex pattern.</summary>
    /// <param name="pattern">The regex pattern to match.</param>
    /// <param name="options">Regex options (default: IgnoreCase).</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions MatchPattern(string pattern, RegexOptions options = RegexOptions.IgnoreCase, string? because = null)
    {
        if (!Regex.IsMatch(_response, pattern, options))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to match the specified pattern.",
                    responsePreview: Truncate(_response, 200),
                    expected: $"Matches pattern: /{pattern}/",
                    actual: "Pattern not matched",
                    suggestions: new[] { "Check regex pattern syntax", "Verify pattern flags (case sensitivity)" },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response has length within a range.</summary>
    /// <param name="min">Minimum length (inclusive).</param>
    /// <param name="max">Maximum length (inclusive).</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions HaveLengthBetween(int min, int max, string? because = null)
    {
        if (_response.Length < min || _response.Length > max)
        {
            var position = _response.Length < min ? "below" : "above";
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    $"Expected response length to be within the specified range.",
                    expected: $"Length between {min} and {max}",
                    actual: $"Length = {_response.Length} ({position} range)",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response has at least N characters.</summary>
    /// <param name="min">Minimum length (inclusive).</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions HaveLengthAtLeast(int min, string? because = null)
    {
        if (_response.Length < min)
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to have at least the specified length.",
                    expected: $"Length ≥ {min}",
                    actual: $"Length = {_response.Length}",
                    suggestions: new[] { "Response may be truncated", "Check if agent provided complete answer" },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response is not empty or whitespace.</summary>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions NotBeEmpty(string? because = null)
    {
        if (string.IsNullOrWhiteSpace(_response))
        {
            var actual = _response.Length == 0 ? "empty string" : "whitespace only";
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to not be empty.",
                    expected: "Non-empty response",
                    actual: actual,
                    suggestions: new[] { "Verify agent received and understood the prompt", "Check for API errors" },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response starts with a prefix.</summary>
    /// <param name="prefix">The expected prefix.</param>
    /// <param name="caseSensitive">Whether the comparison is case-sensitive.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions StartWith(string prefix, bool caseSensitive = false, string? because = null)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!_response.StartsWith(prefix, comparison))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to start with the specified prefix.",
                    expected: $"Starts with \"{prefix}\"",
                    actual: $"Starts with \"{Truncate(_response, prefix.Length + 20)}\"",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert response ends with a suffix.</summary>
    /// <param name="suffix">The expected suffix.</param>
    /// <param name="caseSensitive">Whether the comparison is case-sensitive.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ResponseAssertions EndWith(string suffix, bool caseSensitive = false, string? because = null)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!_response.EndsWith(suffix, comparison))
        {
            var actualEnd = _response.Length > suffix.Length + 20 
                ? "..." + _response[^(suffix.Length + 20)..] 
                : _response;
            
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected response to end with the specified suffix.",
                    expected: $"Ends with \"{suffix}\"",
                    actual: $"Ends with \"{actualEnd}\"",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Get the underlying response for custom assertions.</summary>
    public string Response => _response;
    
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Extension method to start response assertions.
/// </summary>
public static class ResponseAssertionExtensions
{
    /// <summary>Start fluent assertions on a response string.</summary>
    public static ResponseAssertions Should(this string response) => new(response);
    
    /// <summary>Start fluent assertions on a response string with subject name for error messages.</summary>
    public static ResponseAssertions Should(
        this string response, 
        [CallerArgumentExpression(nameof(response))] string? subjectName = null) => new(response, subjectName);
}
