// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.Assertions;

/// <summary>
/// Shared, order-independent tool-call check logic for the workflow tool-call assertion builders
/// (ARC-05). <see cref="WorkflowToolCallAssertionBuilder"/> and <see cref="ExecutorToolCallAssertionBuilder"/>
/// reimplemented these checks identically, differing only in a context label ("in workflow" vs
/// "in executor 'X'") and which AddFailure they call. Each builder now delegates to these helpers, passing
/// its context label and a failure-emitter, and keeps only its thin fluent wrapper. The order-dependent
/// <c>BeforeTool</c>/<c>AfterTool</c> stay on the builders because they use different ordering sources
/// (the workflow-global sequence vs the per-executor <see cref="ToolCallRecord.Order"/> — the BUG-13 trap).
/// </summary>
internal static class WorkflowToolCallChecks
{
    public static void WithoutError(
        ToolCallRecord call, string toolName, string context, string? because, Action<string, string?> addFailure)
    {
        if (call.HasError)
        {
            addFailure(
                $"Expected '{toolName}' {context} to complete without error, " +
                $"but got: {call.Exception?.Message ?? "(unknown error)"}",
                because);
        }
    }

    public static void WithArgument(
        ToolCallRecord call, string toolName, string context, string paramName, object expectedValue,
        string? because, Action<string, string?> addFailure)
    {
        object? actualValue = null;
        var hasArgument = call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;

        if (!hasArgument)
        {
            var available = call.Arguments?.Keys.Any() == true
                ? string.Join(", ", call.Arguments.Keys)
                : "(none)";
            addFailure(
                $"Expected '{toolName}' {context} to have argument '{paramName}', " +
                $"but available arguments: [{available}]",
                because);
        }
        else if (!ToolArgumentComparison.Matches(actualValue, expectedValue))
        {
            // Type-aware comparison (BUG-21): numbers numerically, booleans by value, strings by unquoted content.
            addFailure(
                $"Expected '{toolName}' {context} argument '{paramName}' = \"{expectedValue}\" " +
                $"but was \"{ToolArgumentComparison.Canonical(actualValue)}\"",
                because);
        }
    }

    public static void WithArgumentContaining(
        ToolCallRecord call, string toolName, string context, string paramName, string substring,
        string? because, Action<string, string?> addFailure)
    {
        object? actualValue = null;
        var hasArgument = call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;

        if (!hasArgument)
        {
            addFailure(
                $"Expected '{toolName}' {context} to have argument '{paramName}' containing " +
                $"'{substring}', but argument was not found.",
                because);
        }
        else
        {
            var actualStr = actualValue is JsonElement je ? je.GetString() : actualValue?.ToString();
            if (actualStr == null || !actualStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                addFailure(
                    $"Expected '{toolName}' {context} argument '{paramName}' to contain " +
                    $"'{substring}' but was \"{Truncate(actualStr ?? "(null)", 100)}\"",
                    because);
            }
        }
    }

    public static void WithResultContaining(
        ToolCallRecord call, string toolName, string context, string substring,
        string? because, Action<string, string?> addFailure)
    {
        var resultStr = call.Result?.ToString();
        if (resultStr == null || !resultStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            addFailure(
                $"Expected '{toolName}' {context} result to contain '{substring}' " +
                $"but was \"{Truncate(resultStr ?? "(null)", 100)}\"",
                because);
        }
    }

    public static void WithDurationUnder(
        ToolCallRecord call, string toolName, string context, TimeSpan max,
        string? because, Action<string, string?> addFailure)
    {
        if (!call.HasTiming)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AgentEval] Skipping duration assertion for '{toolName}' {context} - timing not available.");
            return;
        }

        if (call.Duration > max)
        {
            addFailure(
                $"Expected '{toolName}' {context} duration under {max.TotalMilliseconds:F0}ms " +
                $"but was {call.Duration!.Value.TotalMilliseconds:F0}ms",
                because);
        }
    }

    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}
