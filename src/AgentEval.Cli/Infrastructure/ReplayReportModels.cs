// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Linq;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Per-round-trip replay outcome — see <see cref="LogFileReplayer"/> for how each is decided.</summary>
public enum ReplayVerdict
{
    /// <summary>Tool calls, finish reason, and response shape all matched the capture.</summary>
    Pass,

    /// <summary>Tool calls and finish reason matched, but response shape (length bucket / structural markers) diverged — worth a look, not a behavioral regression.</summary>
    Flag,

    /// <summary>Tool calls or finish reason diverged from the capture (or, under <c>--strict-text</c>, exact text diverged) — the replay target behaved differently.</summary>
    Fail,
}

/// <summary>One captured round-trip replayed against <c>--against</c> and diffed structurally against the capture.</summary>
/// <param name="Index">The captured entry's own <c>FixtureCaptureEntry.Index</c>.</param>
/// <param name="Verdict">The outcome — see <see cref="ReplayVerdict"/>.</param>
/// <param name="Summary">One-line human summary of the outcome.</param>
/// <param name="Details">Per-dimension diff lines (tool calls, finish reason, response shape, token usage, latency).</param>
/// <param name="CapturedElapsedMs">Latency the ORIGINAL capture recorded for this round-trip.</param>
/// <param name="ActualElapsedMs">Latency THIS replay observed against <c>--against</c>.</param>
public sealed record ReplayRow(
    int Index,
    ReplayVerdict Verdict,
    string Summary,
    IReadOnlyList<string> Details,
    long CapturedElapsedMs,
    long ActualElapsedMs);

/// <summary>The full replay result: every replayed row plus how many captured entries were skipped and why.</summary>
/// <param name="Rows">One row per replayed ("response"-kind) captured entry, in capture order.</param>
/// <param name="SkippedNonResponseCount">
/// Captured entries with <c>Kind</c> "error"/"abandoned" — skipped, same "no scripted-fixture equivalent"
/// discipline <see cref="LogFixtureGenerator"/> already applies; there is no captured baseline response to
/// structurally diff against for those.
/// </param>
public sealed record ReplayReport(IReadOnlyList<ReplayRow> Rows, int SkippedNonResponseCount)
{
    /// <summary>Count of rows at each verdict.</summary>
    public int PassCount => Rows.Count(r => r.Verdict == ReplayVerdict.Pass);

    /// <summary>Count of rows at each verdict.</summary>
    public int FlagCount => Rows.Count(r => r.Verdict == ReplayVerdict.Flag);

    /// <summary>Count of rows at each verdict.</summary>
    public int FailCount => Rows.Count(r => r.Verdict == ReplayVerdict.Fail);
}
