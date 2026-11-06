// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// Writes every trace event as one plain line (no colours) to a <see cref="TextWriter"/>, <see cref="Console.Error"/>
/// by default — the <c>--m365-verbose</c> view:
/// <c>[m365 HH:mm:ss.fff Stage/Level code] message | ids=… http=… rpc=… state=… ms=… data=…</c>. The time is the
/// event's own stamp (UTC); a segment is printed only when its fact is known. Thread-safe.
/// </summary>
public sealed class ConsoleTraceSink : IWorkIqTraceSink
{
    /// <summary>Upper bound of the inline <c>data=</c> segment.</summary>
    public const int MaxDataLength = 2048;

    private readonly TextWriter _writer;
    private readonly object _gate = new();

    /// <summary>Creates the sink.</summary>
    /// <param name="writer">The destination; null means <see cref="Console.Error"/>.</param>
    public ConsoleTraceSink(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Error;
    }

    /// <summary>Formats one event as the single console line (without a line terminator).</summary>
    /// <param name="e">The event.</param>
    public static string Format(WorkIqTraceEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var line = new StringBuilder(256);
        line.Append("[m365 ")
            .Append(e.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(e.Stage)
            .Append('/')
            .Append(e.Level)
            .Append(' ')
            .Append(e.Code)
            .Append("] ")
            .Append(OneLine(e.Message));

        var ids = new List<string>(7);
        AppendId(ids, "case", e.CaseId);
        AppendId(ids, "turn", e.TurnId);
        AppendId(ids, "rpc", e.RpcId);
        AppendId(ids, "msg", e.MessageId);
        AppendId(ids, "task", e.TaskId);
        AppendId(ids, "ctx", e.ContextId);
        AppendId(ids, "req", e.RequestId);

        var segments = new List<string>(5);
        if (ids.Count > 0)
        {
            segments.Add("ids=" + string.Join(",", ids));
        }

        if (e.HttpStatus is { } http)
        {
            segments.Add("http=" + http.ToString(CultureInfo.InvariantCulture));
        }

        if (e.RpcErrorCode is { } rpc)
        {
            segments.Add("rpc=" + rpc.ToString(CultureInfo.InvariantCulture));
        }

        if (e.A2AState is { } state)
        {
            segments.Add("state=" + OneLine(state));
        }

        if (e.ElapsedMs is { } ms)
        {
            segments.Add("ms=" + ms.ToString("0.#", CultureInfo.InvariantCulture));
        }

        if (e.Data is { } data)
        {
            var compact = JsonSerializer.Serialize(data, WorkIqDiagnosticsJson.Options);
            if (compact.Length > MaxDataLength)
            {
                compact = compact[..MaxDataLength] + WorkIqDiagnosticsJson.TruncationMarker;
            }

            segments.Add("data=" + OneLine(compact));
        }

        if (segments.Count > 0)
        {
            line.Append(" | ").Append(string.Join(" ", segments));
        }

        return line.ToString();
    }

    /// <inheritdoc/>
    public void Write(WorkIqTraceEvent e)
    {
        var line = Format(e);
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private static void AppendId(List<string> ids, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ids.Add(name + ":" + OneLine(value));
        }
    }

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
