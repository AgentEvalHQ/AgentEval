// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The append-only, flush-per-line, thread-safe file writer behind <see cref="JsonlTraceSink"/> and
/// <see cref="JsonlIssueSink"/>: creates the directory, opens the file for shared reading (a tester can tail it
/// while the run goes on) and writes one LF-terminated UTF-8 line per call.
/// </summary>
internal sealed class JsonlLineWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <summary>Opens (or creates) <paramref name="file"/> for appending; the directory is created when missing.</summary>
    /// <param name="file">The file.</param>
    /// <exception cref="InvalidOperationException">The file could not be opened for appending (the message names the path).</exception>
    public JsonlLineWriter(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        File = file;
        try
        {
            var directory = Path.GetDirectoryName(file.FullName);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(file.FullName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, WorkIqDiagnosticsJson.Utf8NoBom) { NewLine = "\n", AutoFlush = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException($"Diagnostics file could not be opened for appending: {file.FullName} ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }

    /// <summary>The file written to.</summary>
    public FileInfo File { get; }

    /// <summary>Appends one line and flushes it to the OS.</summary>
    /// <param name="line">The line (no line terminator; one is appended).</param>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    public void WriteLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Write(line);
            _writer.Write('\n');
            _writer.Flush();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }
}
