// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace AgentEval.Snapshots;

/// <summary>
/// Configuration for snapshot comparison.
/// </summary>
public class SnapshotOptions
{
    /// <summary>Fields to ignore during comparison.</summary>
    public HashSet<string> IgnoreFields { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "duration", "elapsed", "startTime", "endTime", "id", "requestId", "created"
    };

    /// <summary>Patterns to scrub from string values.</summary>
    public List<(Regex Pattern, string Replacement)> ScrubPatterns { get; set; } = new()
    {
        // OpenAI/Azure response IDs
        (new Regex(@"chatcmpl-[a-zA-Z0-9]+", RegexOptions.Compiled), "chatcmpl-[SCRUBBED]"),
        (new Regex(@"resp_[a-zA-Z0-9]+", RegexOptions.Compiled), "resp_[SCRUBBED]"),

        // Timestamps in various formats
        (new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", RegexOptions.Compiled), "[TIMESTAMP]"),
        (new Regex(@"\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}", RegexOptions.Compiled), "[TIMESTAMP]"),

        // GUIDs (with word boundaries to avoid partial matches)
        (new Regex(@"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b", RegexOptions.Compiled), "[GUID]"),

        // Durations in various formats (with word boundary to avoid matching "100seconds" inside a word)
        (new Regex(@"\b\d+(\.\d+)?\s*(ms|s|seconds|milliseconds)\b", RegexOptions.Compiled), "[DURATION]"),
    };

    /// <summary>Enable semantic similarity comparison for text fields.</summary>
    public bool UseSemanticComparison { get; set; } = false;

    /// <summary>Similarity threshold for semantic comparison (0.0–1.0).</summary>
    public double SemanticThreshold { get; set; } = 0.85;

    /// <summary>Fields to apply semantic comparison to.</summary>
    public HashSet<string> SemanticFields { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "response", "output", "content", "message", "answer", "text"
    };

    /// <summary>
    /// Controls how extra properties in the actual JSON (not present in expected) are handled.
    /// <list type="bullet">
    /// <item><description><c>true</c> (default) — Extra properties are silently allowed.</description></item>
    /// <item><description><c>false</c> — Reserved for future use; currently behaves the same as <c>true</c>.</description></item>
    /// <item><description><c>null</c> — Extra properties are reported as differences.</description></item>
    /// </list>
    /// </summary>
    public bool? AllowExtraProperties { get; set; } = true;
}
