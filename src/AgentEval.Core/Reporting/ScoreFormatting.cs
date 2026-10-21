// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core.Reporting;

/// <summary>Consistent report text for measured scores and missing readings.</summary>
public static class ScoreFormatting
{
    /// <summary>Formats a score on the 0–1 scale as a percentage, or explains that it was not measured.</summary>
    public static string Percentage(double? score) => score is { } value ? $"{value:P0}" : "n/a — not measured";
}
