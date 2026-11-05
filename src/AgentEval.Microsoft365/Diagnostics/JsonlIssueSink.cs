// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// Appends every issue to a JSONL file (the issue register, <c>m365-issues.jsonl</c>) with the same conventions
/// as <see cref="JsonlTraceSink"/>: one camelCase JSON object per line, enums as strings, absent facts omitted,
/// 64 KiB line bound with a truncation marker, flushed per write, directory created when missing. Thread-safe.
/// </summary>
public sealed class JsonlIssueSink : IWorkIqIssueSink, IDisposable
{
    private readonly JsonlLineWriter _writer;

    /// <summary>Opens (or creates) <paramref name="file"/> for appending.</summary>
    /// <param name="file">The issue file.</param>
    /// <exception cref="InvalidOperationException">The file could not be opened; the message names the path.</exception>
    public JsonlIssueSink(FileInfo file)
    {
        _writer = new JsonlLineWriter(file);
    }

    /// <summary>The file written to.</summary>
    public FileInfo File => _writer.File;

    /// <inheritdoc/>
    public void Write(WorkIqIssue i)
    {
        ArgumentNullException.ThrowIfNull(i);
        _writer.WriteLine(WorkIqDiagnosticsJson.SerializeLine(i));
    }

    /// <inheritdoc/>
    public void Dispose() => _writer.Dispose();
}
