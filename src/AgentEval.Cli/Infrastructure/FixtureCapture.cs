// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// The ambient hook every <c>IChatClient</c>-constructing call site in the CLI wraps its result through for
/// <c>--capture-fixture</c> — sibling to <see cref="VerboseLog"/>, same shape, same lifecycle
/// (<see cref="Initialize"/> called once in <c>Program.cs</c>). Deliberately a SEPARATE ambient hook from
/// <see cref="VerboseLog"/>, not a mode on it — a user troubleshooting a one-off failure wants the
/// human-readable log and nothing else; a user building a regression fixture wants the structured capture and
/// may not care about the prose log at all. See <see cref="CliChatClientDiagnostics.Wrap"/> for the single
/// call-site hook that composes both when a caller wants either/both to apply.
/// </summary>
internal static class FixtureCapture
{
    /// <summary>
    /// The shared writer for this process, or <see langword="null"/> when <c>--capture-fixture</c> was not
    /// passed. Always <see cref="TextWriter.Synchronized(TextWriter)"/>-wrapped — see <see cref="VerboseLog.Writer"/>'s
    /// matching remark for why.
    /// </summary>
    public static TextWriter? Writer { get; private set; }

    /// <summary>
    /// Opens <paramref name="fixturePath"/> for writing (overwriting any prior content) and sets
    /// <see cref="Writer"/>. Same "a bad path degrades to no capture, it never fails the command" contract as
    /// <see cref="VerboseLog.Initialize"/> — see its remarks.
    /// </summary>
    public static TextWriter? Initialize(string? fixturePath)
    {
        Writer?.Dispose();
        Writer = null;

        if (string.IsNullOrWhiteSpace(fixturePath))
        {
            return null;
        }

        try
        {
            var raw = new StreamWriter(fixturePath, append: false) { AutoFlush = true };
            var synchronized = TextWriter.Synchronized(raw);
            Writer = synchronized;
            return synchronized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"  Warning: could not open --capture-fixture '{fixturePath}': {ex.Message}. Continuing without fixture capture.");
            return null;
        }
    }

    /// <summary>
    /// Wraps <paramref name="client"/> in a <see cref="FixtureCapturingChatClient"/> if fixture capture is
    /// active; returns it unchanged otherwise. See <see cref="VerboseLog.Wrap"/>'s matching remark for the
    /// <paramref name="label"/> convention.
    /// </summary>
    public static IChatClient Wrap(IChatClient client, string? label = null) =>
        Writer is null ? client : new FixtureCapturingChatClient(client, Writer, label);
}
