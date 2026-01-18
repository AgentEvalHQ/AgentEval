// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.ComponentModel;

namespace AgentEval.NuGetConsumer.Tools;

/// <summary>
/// Calculator tool for math operations.
/// A simple tool used for testing agentic workflows.
/// </summary>
public static class CalculatorTool
{
    [Description("Performs basic arithmetic operations")]
    public static string Calculate(
        [Description("First operand")] double a,
        [Description("Operation: add, subtract, multiply, divide")] string operation,
        [Description("Second operand")] double b)
    {
        var result = operation.ToLowerInvariant() switch
        {
            "add" or "+" or "plus" => a + b,
            "subtract" or "-" or "minus" => a - b,
            "multiply" or "*" or "x" or "times" => a * b,
            "divide" or "/" => b != 0 ? a / b : double.NaN,
            _ => throw new ArgumentException($"Unknown operation: {operation}")
        };
        
        return $"{a} {operation} {b} = {result}";
    }
}
