// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Reduces a sequence of severity strings to the single worst-case value.</summary>
public static class SeverityRollup
{
    private static readonly string[] s_orderedSeverities = { "none", "low", "medium", "high", "critical" };

    /// <summary>Returns the highest severity present in <paramref name="severities"/>, or "none" for an empty sequence.</summary>
    public static string Max(IEnumerable<string> severities)
    {
        ArgumentNullException.ThrowIfNull(severities);
        int worst = 0;
        foreach (var s in severities)
        {
            var idx = Array.IndexOf(s_orderedSeverities, s ?? "none");
            if (idx < 0) idx = 0;
            if (idx > worst) worst = idx;
        }
        return s_orderedSeverities[worst];
    }
}
