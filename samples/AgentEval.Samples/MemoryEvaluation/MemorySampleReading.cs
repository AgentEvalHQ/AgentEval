// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Samples;

internal static class MemorySampleReading
{
    public static string Percent(double? score) => score is { } value ? $"{value:F1}%" : "n/a";
}
