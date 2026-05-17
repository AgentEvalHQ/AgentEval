// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.Text;

namespace AgentEval.TravelDemo;

/// <summary>
/// Fans <see cref="Console.Out"/> + <see cref="Console.Error"/> to BOTH the
/// terminal AND a log file. Use at the top of <c>Program.cs</c> to capture
/// every byte of a demo / eval run for later inspection (post-mortem,
/// regression diff, sharing in a bug report, etc.).
/// </summary>
/// <example>
/// <code>
/// // Capture everything from this run into ./logs/eval01-2026-05-14_15-22-08.log:
/// using var _ = ConsoleLogRecorder.StartLogging(); // auto-named, ./logs/ folder
///
/// // Or pin a custom path:
/// using var _ = ConsoleLogRecorder.StartLogging("./logs/eval01-debug.log");
///
/// // Disposing the scope restores Console.Out / Console.Error.
/// </code>
/// </example>
/// <remarks>
/// <para>
/// The file is written with auto-flush enabled so a crash or hard kill still
/// leaves a usable transcript on disk. ANSI colour codes from
/// <see cref="Console.ForegroundColor"/> are NOT emitted to the file (the
/// .NET console writer applies colour via Win32 APIs that don't reach a
/// <see cref="StreamWriter"/>), so the file ends up as clean plain text —
/// ideal for grep, diff, or pasting into a chat / issue.
/// </para>
/// <para>
/// Wired into Program.cs of both <c>AgentEval.TravelDemo</c> and
/// <c>AgentEval.TravelDemo.Evals</c> via the <c>--log [path]</c> CLI flag.
/// </para>
/// </remarks>
public static class ConsoleLogRecorder
{
    /// <summary>
    /// Starts capturing console output to a log file. Dispose the returned
    /// scope to stop capturing and restore the original
    /// <see cref="Console.Out"/> / <see cref="Console.Error"/>.
    /// </summary>
    /// <param name="logPath">
    /// Optional file path. When null, auto-names the file as
    /// <c>./logs/{processName}-{yyyy-MM-dd_HH-mm-ss}.log</c>.
    /// </param>
    public static IDisposable StartLogging(string? logPath = null)
    {
        logPath ??= DefaultPath();

        var fullPath = Path.GetFullPath(logPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var file = new StreamWriter(fullPath, append: false, Encoding.UTF8) { AutoFlush = true };

        var origOut = Console.Out;
        var origErr = Console.Error;

        Console.SetOut(new TeeWriter(origOut, file));
        Console.SetError(new TeeWriter(origErr, file));

        // Mirror the banner to the file too — the tee is already installed,
        // so this line lands in both places.
        Console.WriteLine($"📝 Logging to {fullPath}");

        return new LogScope(file, origOut, origErr);
    }

    private static string DefaultPath()
    {
        var procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var stamp    = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return Path.Combine("logs", $"{procName}-{stamp}.log");
    }

    /// <summary>
    /// <see cref="TextWriter"/> that writes every character to TWO underlying
    /// writers (typically: original console + file). Used as a drop-in
    /// replacement for <see cref="Console.Out"/> / <see cref="Console.Error"/>.
    /// </summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _file;

        public TeeWriter(TextWriter primary, TextWriter file)
        {
            _primary = primary;
            _file    = file;
        }

        public override Encoding Encoding => _primary.Encoding;

        public override void Write(char c)              { _primary.Write(c);    _file.Write(c); }
        public override void Write(string? s)           { _primary.Write(s);    _file.Write(s); }
        public override void Write(char[] buf, int i, int n) { _primary.Write(buf, i, n); _file.Write(buf, i, n); }
        public override void WriteLine()                { _primary.WriteLine(); _file.WriteLine(); }
        public override void WriteLine(string? s)       { _primary.WriteLine(s); _file.WriteLine(s); }
        public override void Flush()                    { _primary.Flush();     _file.Flush(); }
    }

    private sealed class LogScope : IDisposable
    {
        private readonly StreamWriter _file;
        private readonly TextWriter   _origOut;
        private readonly TextWriter   _origErr;
        private bool _disposed;

        public LogScope(StreamWriter file, TextWriter origOut, TextWriter origErr)
        {
            _file    = file;
            _origOut = origOut;
            _origErr = origErr;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Console.SetOut(_origOut); }   catch { /* best-effort */ }
            try { Console.SetError(_origErr); } catch { /* best-effort */ }
            try { _file.Flush(); _file.Dispose(); } catch { /* best-effort */ }
        }
    }
}
