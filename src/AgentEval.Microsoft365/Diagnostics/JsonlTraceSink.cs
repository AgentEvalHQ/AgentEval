// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// Appends every trace event to a JSONL file: one camelCase JSON object per line, enums as strings, absent facts
/// omitted, each line bounded to 64 KiB (<see cref="WorkIqDiagnosticsJson.MaxLineLength"/>) with
/// <see cref="WorkIqDiagnosticsJson.TruncationMarker"/> where it was cut, flushed per write. The directory is
/// created when missing. Thread-safe.
/// </summary>
public sealed class JsonlTraceSink : IWorkIqTraceSink, IDisposable
{
    private readonly JsonlLineWriter _writer;

    /// <summary>Opens (or creates) <paramref name="file"/> for appending.</summary>
    /// <param name="file">The trace file.</param>
    /// <exception cref="InvalidOperationException">The file could not be opened; the message names the path.</exception>
    public JsonlTraceSink(FileInfo file)
    {
        _writer = new JsonlLineWriter(file);
    }

    /// <summary>The file written to.</summary>
    public FileInfo File => _writer.File;

    /// <inheritdoc/>
    public void Write(WorkIqTraceEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _writer.WriteLine(WorkIqDiagnosticsJson.SerializeLine(e));
    }

    /// <inheritdoc/>
    public void Dispose() => _writer.Dispose();
}
