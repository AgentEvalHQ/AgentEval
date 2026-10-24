// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Published reference scores from the LongMemEval paper (ICLR 2025).
/// Full-history S mode without Chain-of-Note (Figure 3b), not the separate commercial-memory study.
/// </summary>
/// <seealso href="https://arxiv.org/html/2410.10813v2#S3.F3">LongMemEval paper, Figure 3.</seealso>
public static class LongMemEvalReferenceScores
{
    /// <summary>Overall accuracy scores by model (S mode, direct).</summary>
    public static readonly IReadOnlyDictionary<string, double> OverallAccuracy =
        new Dictionary<string, double>
        {
            ["GPT-4o"] = 60.6,
            ["Llama-3.1-70B"] = 33.4
        };
}
