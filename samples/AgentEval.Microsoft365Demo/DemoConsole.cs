// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365Demo;

/// <summary>The colour of a screen line (only used when the console is interactive; console.log never has colours).</summary>
internal enum Tint
{
    /// <summary>The terminal's default colour.</summary>
    None,

    /// <summary>A pass.</summary>
    Good,

    /// <summary>A failure or an error.</summary>
    Bad,

    /// <summary>A warning, a skip or something not measured.</summary>
    Warn,

    /// <summary>Background detail (trace lines).</summary>
    Dim,

    /// <summary>A heading.</summary>
    Accent,
}

/// <summary>
/// The demo's one output channel: the screen at the chosen <see cref="DemoVerbosity"/> and <c>console.log</c> in the run
/// folder, which always gets the full (diagnostic) view without colours. Every line is redacted on the way out
/// (<see cref="WorkIqRedaction.Redact"/>), so no token, auth code or <c>Authorization</c> header reaches either; the
/// device-code sign-in message is the one auth text the screen shows in full, and console.log keeps it with the user code
/// masked. Thread-safe: the provider traces from several threads, and each line is written whole.
/// </summary>
internal sealed partial class DemoConsole : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly bool _colors;
    private StreamWriter? _log;

    /// <summary>Creates the console.</summary>
    /// <param name="out">The screen's standard output.</param>
    /// <param name="err">The screen's error output (usage and runtime errors).</param>
    /// <param name="verbosity">What the screen shows.</param>
    /// <param name="colors">Whether <paramref name="out"/> is the interactive console and may be coloured.</param>
    public DemoConsole(TextWriter @out, TextWriter err, DemoVerbosity verbosity, bool colors)
    {
        _out = @out ?? throw new ArgumentNullException(nameof(@out));
        _err = err ?? throw new ArgumentNullException(nameof(err));
        Verbosity = verbosity;
        _colors = colors;
    }

    /// <summary>What the screen shows.</summary>
    public DemoVerbosity Verbosity { get; }

    /// <summary>The first error writing console.log, when any (the log is then closed and the run exits 3).</summary>
    public string? LogError { get; private set; }

    /// <summary>The console.log file, once opened.</summary>
    public FileInfo? LogFile { get; private set; }

    /// <summary>Whether the screen shows lines of <paramref name="level"/>.</summary>
    /// <param name="level">The level.</param>
    public bool Shows(DemoVerbosity level) => Verbosity >= level;

    /// <summary>Opens console.log (UTF-8, no BOM, flushed after every line so a crash still leaves it on disk).</summary>
    /// <param name="file">The file (created; must not exist).</param>
    /// <exception cref="IOException">The file could not be created.</exception>
    public void OpenLog(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var stream = new FileStream(file.FullName, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        lock (_gate)
        {
            _log = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true, NewLine = "\n" };
            LogFile = file;
        }
    }

    /// <summary>Closes console.log; later lines reach the screen only.</summary>
    public void CloseLog()
    {
        lock (_gate)
        {
            try
            {
                _log?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                LogError ??= $"console.log could not be closed: {ex.Message}";
            }

            _log = null;
        }
    }

    /// <summary>Writes <paramref name="text"/> (one or more lines) to the screen when it shows <paramref name="min"/>, and always to console.log.</summary>
    /// <param name="min">The least verbosity that shows it on the screen.</param>
    /// <param name="text">The text.</param>
    /// <param name="tint">Its colour on an interactive screen.</param>
    public void Line(DemoVerbosity min, string text, Tint tint = Tint.None)
    {
        var safe = Safe(text);
        lock (_gate)
        {
            if (Shows(min))
            {
                WriteScreen(_out, safe, tint);
            }

            WriteLog(safe);
        }
    }

    /// <summary>A blank line on the screen (at <paramref name="min"/>) and in console.log.</summary>
    /// <param name="min">The least verbosity that shows it.</param>
    public void Blank(DemoVerbosity min = DemoVerbosity.Normal) => Line(min, "");

    /// <summary>An error: always on the error output, always in console.log.</summary>
    /// <param name="text">The error text.</param>
    public void Error(string text)
    {
        var safe = Safe(text);
        lock (_gate)
        {
            WriteScreen(_err, safe, Tint.Bad);
            WriteLog(safe);
        }
    }

    /// <summary>
    /// A trace event: <paramref name="screenText"/> on the screen when not null (the caller picks the form for the
    /// verbosity), <paramref name="logText"/> (the full form) always in console.log.
    /// </summary>
    /// <param name="screenText">The screen form, or null for none.</param>
    /// <param name="logText">The console.log form.</param>
    /// <param name="tint">Its colour on an interactive screen.</param>
    public void Trace(string? screenText, string logText, Tint tint = Tint.Dim)
    {
        var log = Safe(logText);
        var screen = screenText is null ? null : Safe(screenText);
        lock (_gate)
        {
            if (screen is not null)
            {
                WriteScreen(_out, screen, tint);
            }

            WriteLog(log);
        }
    }

    /// <summary>Writes console.log only.</summary>
    /// <param name="text">The text.</param>
    public void LogOnly(string text)
    {
        var safe = Safe(text);
        lock (_gate)
        {
            WriteLog(safe);
        }
    }

    /// <summary>
    /// The device-code sign-in message: always on the screen in full (the tester must read the page and the code), in
    /// console.log with the user code masked.
    /// </summary>
    /// <param name="text">The message.</param>
    public void SignInPrompt(string text)
    {
        var screen = Safe(text);
        var log = MaskDeviceCode(screen);
        lock (_gate)
        {
            WriteScreen(_out, screen, Tint.Accent);
            WriteLog(log);
        }
    }

    /// <summary>Masks the user code of a device-code sign-in message (<c>enter the code ABC123 to …</c>).</summary>
    /// <param name="text">The message.</param>
    internal static string MaskDeviceCode(string text)
    {
        var masked = EnterTheCode().Replace(text, "$1" + WorkIqRedaction.RedactedMarker);
        if (masked.Contains("devicelogin", StringComparison.OrdinalIgnoreCase) || masked.Contains("device login", StringComparison.OrdinalIgnoreCase))
        {
            masked = UserCodeShape().Replace(masked, WorkIqRedaction.RedactedMarker);
        }

        return masked;
    }

    /// <inheritdoc/>
    public void Dispose() => CloseLog();

    private static string Safe(string? text) => WorkIqRedaction.Redact(text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private void WriteScreen(TextWriter writer, string text, Tint tint)
    {
        try
        {
            var color = _colors && writer == _out ? ColorOf(tint) : null;
            if (color is { } c)
            {
                Console.ForegroundColor = c;
                writer.WriteLine(text);
                writer.Flush();
                Console.ResetColor();
            }
            else
            {
                writer.WriteLine(text);
                writer.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The screen went away (a closed pipe); console.log still gets everything.
        }
    }

    private void WriteLog(string text)
    {
        if (_log is null)
        {
            return;
        }

        try
        {
            foreach (var line in text.Split('\n'))
            {
                _log.Write(line);
                _log.Write('\n');
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            LogError ??= $"console.log could not be written: {ex.Message}";
            try
            {
                _log.Dispose();
            }
            catch (Exception)
            {
                // Already failing; the error above is what the run reports.
            }

            _log = null;
        }
    }

    private static ConsoleColor? ColorOf(Tint tint) => tint switch
    {
        Tint.Good => ConsoleColor.Green,
        Tint.Bad => ConsoleColor.Red,
        Tint.Warn => ConsoleColor.Yellow,
        Tint.Dim => ConsoleColor.DarkGray,
        Tint.Accent => ConsoleColor.Cyan,
        _ => null,
    };

    [GeneratedRegex(@"(?i)(enter the code\s+)([^\s.,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EnterTheCode();

    [GeneratedRegex(@"\b(?=[A-Z0-9]*[0-9])(?=[A-Z0-9]*[A-Z])[A-Z0-9]{8,12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UserCodeShape();
}

/// <summary>
/// The <see cref="TextWriter"/> the readiness check writes the device-code sign-in message to
/// (<c>ReadinessCheckOptions.Console</c>): every complete line goes to <see cref="DemoConsole.SignInPrompt"/>, so the screen
/// shows it in full whatever the verbosity and console.log keeps it with the user code masked.
/// </summary>
internal sealed class SignInPromptWriter : TextWriter
{
    private readonly DemoConsole _console;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();

    /// <summary>Creates the writer.</summary>
    /// <param name="console">Where the lines go.</param>
    public SignInPromptWriter(DemoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <inheritdoc/>
    public override Encoding Encoding => Encoding.UTF8;

    /// <inheritdoc/>
    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                EmitPending();
            }
            else if (value != '\r')
            {
                _pending.Append(value);
            }
        }
    }

    /// <inheritdoc/>
    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (var c in value)
        {
            Write(c);
        }
    }

    /// <inheritdoc/>
    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        lock (_gate)
        {
            if (_pending.Length > 0)
            {
                EmitPending();
            }
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    private void EmitPending()
    {
        var line = _pending.ToString();
        _pending.Clear();
        _console.SignInPrompt(line);
    }
}
