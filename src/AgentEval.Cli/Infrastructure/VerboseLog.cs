// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// The ambient hook every <c>IChatClient</c>-constructing call site in the CLI wraps its result through —
/// <see cref="Wrap"/> is a no-op when <c>--log-file</c> was not passed, so call sites never need to know
/// whether verbose logging is active. <see cref="Initialize"/> is called ONCE, in <c>Program.cs</c>, before
/// the parsed command is invoked (no DI container exists in this CLI to do this more centrally).
/// </summary>
internal static class VerboseLog
{
    /// <summary>
    /// The shared writer for this process, or <see langword="null"/> when <c>--log-file</c> was not passed.
    /// Always <see cref="TextWriter.Synchronized(TextWriter)"/>-wrapped by <see cref="Initialize"/> — multiple
    /// <see cref="VerboseLoggingChatClient"/> instances (a RedTeam scan's SUT/judge/attacker clients, all
    /// distinct instances) can be writing through this SAME reference concurrently, and the BCL's own
    /// synchronized wrapper is the correct, well-tested way to make that safe, rather than a hand-rolled
    /// <c>lock</c> on this publicly-readable object (which a caller could also accidentally lock on).
    /// </summary>
    public static TextWriter? Writer { get; private set; }

    /// <summary>
    /// Opens <paramref name="logFilePath"/> for writing (overwriting any prior content — each CLI invocation
    /// gets its own fresh log) and sets <see cref="Writer"/>. Returns the opened writer (or <see langword="null"/>
    /// if no path was given, OR if the path couldn't be opened) so the caller can dispose/flush it once the
    /// command finishes. Disposes any PREVIOUS <see cref="Writer"/> first — this method is meant to run once
    /// per process, but nothing enforces that (tests call it repeatedly), and silently orphaning an open file
    /// handle on a second call would leak it until GC finalizes the underlying <see cref="StreamWriter"/>.
    /// </summary>
    /// <remarks>
    /// <b>A bad path degrades to "no verbose logging," it never fails the command.</b> <c>--log-file</c> is a
    /// RECURSIVE option evaluated eagerly in <c>Program.cs</c> before any command dispatch — an unhandled
    /// exception here would crash EVERY command, including ones with nothing to do with an LLM (e.g.
    /// <c>agenteval doctor --log-file &lt;bad-path&gt;</c>), over what is purely an optional debugging aid. A
    /// bad path (missing parent directory, no write permission, an invalid path shape) instead prints one clear
    /// warning to stderr and proceeds with <see cref="Writer"/> left <see langword="null"/> — the invoked
    /// command runs exactly as it would have without <c>--log-file</c> at all.
    /// </remarks>
    public static TextWriter? Initialize(string? logFilePath)
    {
        Writer?.Dispose();
        Writer = null;

        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return null;
        }

        try
        {
            // AutoFlush: a CLI invocation that crashes or is killed mid-run should still leave a readable
            // partial log — the whole point of this feature is troubleshooting a failure, including the
            // failures that don't reach an orderly exit.
            var raw = new StreamWriter(logFilePath, append: false) { AutoFlush = true };
            var synchronized = TextWriter.Synchronized(raw);
            Writer = synchronized;
            return synchronized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"  Warning: could not open --log-file '{logFilePath}': {ex.Message}. Continuing without verbose logging.");
            return null;
        }
    }

    /// <summary>
    /// Wraps <paramref name="client"/> in a <see cref="VerboseLoggingChatClient"/> if verbose logging is
    /// active; returns it unchanged otherwise.
    /// </summary>
    /// <param name="client">The chat client to wrap.</param>
    /// <param name="label">
    /// A short role label (e.g. <c>"sut"</c>/<c>"judge"</c>/<c>"attacker"</c>) printed in every log entry from
    /// this client. Optional, but strongly recommended whenever a caller might construct MORE than one wrapped
    /// client sharing this same log file (e.g. RedTeam's SUT + judge + attacker) — without it, entries from
    /// different clients are indistinguishable beyond their (per-instance, independently-numbered) round-trip
    /// index and timestamp.
    /// </param>
    public static IChatClient Wrap(IChatClient client, string? label = null) =>
        Writer is null ? client : new VerboseLoggingChatClient(client, Writer, label);
}
