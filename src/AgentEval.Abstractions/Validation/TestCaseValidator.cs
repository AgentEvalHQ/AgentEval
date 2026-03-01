// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Validation;

/// <summary>
/// Validates <see cref="TestCase"/> instances before execution to catch misconfiguration early.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Validate"/> at the entry points of evaluation harnesses and runners
/// to fail fast with clear messages instead of producing confusing downstream errors.
/// </para>
/// <para>
/// Validation rules:
/// <list type="bullet">
///   <item><c>Name</c> must not be null, empty, or whitespace</item>
///   <item><c>Input</c> must not be null, empty, or whitespace</item>
///   <item><c>PassingScore</c> must be between 0 and 100 (inclusive)</item>
/// </list>
/// </para>
/// </remarks>
public static class TestCaseValidator
{
    /// <summary>
    /// Validates a <see cref="TestCase"/> and throws <see cref="ArgumentException"/> if invalid.
    /// </summary>
    /// <param name="testCase">The test case to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="testCase"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the test case has invalid field values.</exception>
    public static void Validate(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        if (string.IsNullOrWhiteSpace(testCase.Name))
        {
            throw new ArgumentException(
                "TestCase.Name must not be null, empty, or whitespace. " +
                "Provide a descriptive name to identify this test case in reports.",
                nameof(testCase));
        }

        if (string.IsNullOrWhiteSpace(testCase.Input))
        {
            throw new ArgumentException(
                $"TestCase.Input must not be null, empty, or whitespace (test case: '{testCase.Name}'). " +
                "Provide the prompt text that will be sent to the agent.",
                nameof(testCase));
        }

        if (testCase.PassingScore < 0 || testCase.PassingScore > 100)
        {
            throw new ArgumentException(
                $"TestCase.PassingScore must be between 0 and 100 (inclusive), but was {testCase.PassingScore} " +
                $"(test case: '{testCase.Name}'). " +
                "Score represents a percentage — use 0 for always-pass or 100 for perfect-only.",
                nameof(testCase));
        }
    }
}
