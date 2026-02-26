// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Utility for converting between different scoring scales.
/// 
/// AgentEval uses 0-100 scale internally.
/// Microsoft.Extensions.AI.Evaluation uses 1-5 scale.
/// Cosine similarity uses 0.0-1.0 scale.
/// </summary>
public static class ScoreNormalizer
{
    /// <summary>
    /// Convert a 1-5 Microsoft evaluation score to 0-100 AgentEval scale.
    /// </summary>
    /// <param name="score">Score in range 1-5.</param>
    /// <returns>Score in range 0-100.</returns>
    public static double FromOneToFive(double score)
    {
        // 1 → 0, 2 → 25, 3 → 50, 4 → 75, 5 → 100
        var clamped = Math.Clamp(score, 1.0, 5.0);
        return (clamped - 1.0) * 25.0;
    }
    
    /// <summary>
    /// Convert a 0-100 AgentEval score to 1-5 Microsoft evaluation scale.
    /// </summary>
    /// <param name="score">Score in range 0-100.</param>
    /// <returns>Score in range 1-5.</returns>
    public static double ToOneToFive(double score)
    {
        // 0 → 1, 25 → 2, 50 → 3, 75 → 4, 100 → 5
        var clamped = Math.Clamp(score, 0.0, 100.0);
        return (clamped / 25.0) + 1.0;
    }
    
    /// <summary>
    /// Convert a 0.0-1.0 similarity score to 0-100 AgentEval scale.
    /// </summary>
    /// <param name="similarity">Similarity in range 0.0-1.0.</param>
    /// <returns>Score in range 0-100.</returns>
    public static double FromSimilarity(double similarity)
    {
        var clamped = Math.Clamp(similarity, 0.0, 1.0);
        return clamped * 100.0;
    }
    
    /// <summary>
    /// Convert a 0.0-1.0 similarity score (float) to 0-100 AgentEval scale.
    /// </summary>
    /// <param name="similarity">Similarity in range 0.0-1.0.</param>
    /// <returns>Score in range 0-100.</returns>
    public static double FromSimilarity(float similarity)
        => FromSimilarity((double)similarity);
    
    /// <summary>
    /// Convert a 0-100 AgentEval score to 0.0-1.0 similarity scale.
    /// </summary>
    /// <param name="score">Score in range 0-100.</param>
    /// <returns>Similarity in range 0.0-1.0.</returns>
    public static double ToSimilarity(double score)
    {
        var clamped = Math.Clamp(score, 0.0, 100.0);
        return clamped / 100.0;
    }
    
    /// <summary>
    /// Get a human-readable interpretation of a 0-100 score.
    /// </summary>
    /// <param name="score">Score in range 0-100.</param>
    /// <returns>Interpretation string.</returns>
    public static string Interpret(double score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 60 => "Satisfactory",
        >= 40 => "Needs Improvement",
        >= 20 => "Poor",
        _ => "Very Poor"
    };
    
    /// <summary>
    /// Determines if a score passes based on a threshold.
    /// </summary>
    /// <param name="score">Score to evaluate.</param>
    /// <param name="threshold">Passing threshold (default: 70).</param>
    /// <returns>True if score meets or exceeds threshold.</returns>
    public static bool Passes(double score, double threshold = 70.0)
        => score >= threshold;
}
