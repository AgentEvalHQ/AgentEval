// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Assertions;

/// <summary>
/// Exception thrown when an agent violates a behavioral policy.
/// Provides structured information for debugging, audit trails, and compliance reporting.
/// </summary>
/// <remarks>
/// Behavioral policies are safety-critical assertions that enforce constraints on agent behavior.
/// Common use cases include:
/// <list type="bullet">
///   <item><description>Preventing calls to dangerous or forbidden tools</description></item>
///   <item><description>Detecting PII or sensitive data in tool arguments</description></item>
///   <item><description>Requiring confirmation before risky actions</description></item>
/// </list>
/// </remarks>
public class BehavioralPolicyViolationException : AgentEvalAssertionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BehavioralPolicyViolationException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public BehavioralPolicyViolationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BehavioralPolicyViolationException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public BehavioralPolicyViolationException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the name of the policy that was violated.
    /// </summary>
    /// <example>NeverCallTool(DeleteDatabase), NeverPassArgumentMatching(\d{3}-\d{2}-\d{4})</example>
    public required string PolicyName { get; init; }

    /// <summary>
    /// Gets the type of violation that occurred.
    /// </summary>
    /// <remarks>
    /// Common values:
    /// <list type="bullet">
    ///   <item><description>ForbiddenTool - A prohibited tool was called</description></item>
    ///   <item><description>SensitiveData - Sensitive data pattern was detected in arguments</description></item>
    ///   <item><description>MissingConfirmation - Required confirmation step was not performed</description></item>
    /// </list>
    /// </remarks>
    public required string ViolationType { get; init; }

    /// <summary>
    /// Gets a description of the action that violated the policy.
    /// </summary>
    /// <example>Called DeleteDatabase 2 time(s)</example>
    public required string ViolatingAction { get; init; }

    /// <summary>
    /// Gets the name of the forbidden tool, if applicable.
    /// </summary>
    public string? ForbiddenToolName { get; init; }

    /// <summary>
    /// Gets the regex pattern that matched sensitive data, if applicable.
    /// </summary>
    public string? MatchedPattern { get; init; }

    /// <summary>
    /// Gets the redacted value that matched the pattern.
    /// Sensitive data is automatically masked for security.
    /// </summary>
    /// <example>1***4 (for SSN 123-45-6789)</example>
    public string? RedactedValue { get; init; }

    /// <summary>
    /// Gets the name of the tool argument that contained the violation, if applicable.
    /// </summary>
    public string? ArgumentName { get; init; }

    /// <summary>
    /// Gets the name of the tool that contained the violating argument, if applicable.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Gets the "because" reason provided by the test author.
    /// </summary>
    public new string? Because { get; init; }

    /// <summary>
    /// Gets actionable suggestions for fixing the policy violation.
    /// </summary>
    public new IReadOnlyList<string>? Suggestions { get; init; }

    /// <summary>
    /// Creates a new BehavioralPolicyViolationException with a formatted message.
    /// </summary>
    public static BehavioralPolicyViolationException Create(
        string message,
        string policyName,
        string violationType,
        string violatingAction,
        string? forbiddenToolName = null,
        string? matchedPattern = null,
        string? redactedValue = null,
        string? argumentName = null,
        string? toolName = null,
        string? because = null,
        IReadOnlyList<string>? suggestions = null)
    {
        var fullMessage = BuildMessage(message, policyName, violationType, violatingAction, 
            forbiddenToolName, matchedPattern, redactedValue, argumentName, toolName, because, suggestions);

        return new BehavioralPolicyViolationException(fullMessage)
        {
            PolicyName = policyName,
            ViolationType = violationType,
            ViolatingAction = violatingAction,
            ForbiddenToolName = forbiddenToolName,
            MatchedPattern = matchedPattern,
            RedactedValue = redactedValue,
            ArgumentName = argumentName,
            ToolName = toolName,
            Because = because,
            Suggestions = suggestions
        };
    }

    private static string BuildMessage(
        string message,
        string policyName,
        string violationType,
        string violatingAction,
        string? forbiddenToolName,
        string? matchedPattern,
        string? redactedValue,
        string? argumentName,
        string? toolName,
        string? because,
        IReadOnlyList<string>? suggestions)
    {
        var lines = new List<string>
        {
            "❌ Behavioral Policy Violation",
            "",
            $"Policy: {policyName}",
            $"Violation Type: {violationType}",
            $"Action: {violatingAction}"
        };

        if (!string.IsNullOrEmpty(toolName))
            lines.Add($"Tool: {toolName}");

        if (!string.IsNullOrEmpty(argumentName))
            lines.Add($"Argument: {argumentName}");

        if (!string.IsNullOrEmpty(matchedPattern))
            lines.Add($"Matched Pattern: {matchedPattern}");

        if (!string.IsNullOrEmpty(redactedValue))
            lines.Add($"Matched Value: \"{redactedValue}\" (redacted)");

        lines.Add("");

        if (!string.IsNullOrEmpty(because))
        {
            lines.Add($"Because: {because}");
            lines.Add("");
        }

        if (suggestions is { Count: > 0 })
        {
            lines.Add("Suggestions:");
            foreach (var suggestion in suggestions)
            {
                lines.Add($"  → {suggestion}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Redacts sensitive data by masking middle characters.
    /// </summary>
    /// <param name="value">The value to redact.</param>
    /// <param name="matchStart">Start index of the match.</param>
    /// <param name="matchLength">Length of the match.</param>
    /// <returns>A redacted version of the matched value.</returns>
    public static string RedactSensitiveData(string value, int matchStart, int matchLength)
    {
        if (matchLength <= 4)
            return "****";

        var matched = value.Substring(matchStart, matchLength);
        return $"{matched[0]}***{matched[^1]}";
    }
}
