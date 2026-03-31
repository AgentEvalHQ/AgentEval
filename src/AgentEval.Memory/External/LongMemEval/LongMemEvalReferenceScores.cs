// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Published reference scores from the LongMemEval paper (ICLR 2025).
/// S mode, direct reading method, online interactive evaluation.
/// </summary>
public static class LongMemEvalReferenceScores
{
    /// <summary>Overall accuracy scores by model (S mode, direct).</summary>
    public static readonly IReadOnlyDictionary<string, double> OverallAccuracy =
        new Dictionary<string, double>
        {
            ["GPT-4o"] = 57.7,
            ["GPT-4o-mini"] = 42.8,
            ["Claude 3.5 Sonnet"] = 53.0,
            ["Llama-3.1-70B"] = 39.8
        };
}
