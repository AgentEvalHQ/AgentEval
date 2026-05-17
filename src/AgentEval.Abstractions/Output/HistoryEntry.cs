// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Represents a single line in a subject's <c>history.jsonl</c> file.</summary>
public sealed record HistoryEntry(
    string RunId,
    DateTimeOffset Timestamp,
    string Verdict,
    double Score,
    string? Notes)
{
    /// <summary>Creates a <see cref="HistoryEntry"/> from a completed run manifest and summary.</summary>
    public static HistoryEntry From(RunManifest manifest, RunSummary summary) =>
        new(
            manifest.Run.RunId,
            manifest.Run.Timestamp,
            summary.Verdict,
            summary.Stats.Total > 0
                ? (double)summary.Stats.Passed / summary.Stats.Total
                : 0.0,
            null);
}
