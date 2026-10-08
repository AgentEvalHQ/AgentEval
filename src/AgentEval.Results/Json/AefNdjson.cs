// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Json;

/// <summary>
/// One line of an NDJSON file: its 1-based <paramref name="Number"/>, where its bytes start in the file and how many
/// there are (without the LF), and either the object it holds or the <paramref name="Problem"/> that kept it from being
/// read (an <see cref="AefEncodingException"/> for a line that is not an I-JSON object, an
/// <see cref="AefLimitException"/> for a line beyond 4 MiB or nested deeper than 64).
/// </summary>
public sealed record AefNdjsonLine(int Number, int Offset, int Length, JsonObject? Value, AefReadException? Problem);

/// <summary>
/// An NDJSON file as a reader takes it: either a <see cref="Problem"/> with the whole file (its framing breaks [ENC-5]
/// or [ENC-7], or it has more lines than [ENC-17] allows), and then no line is read, or its lines, each read on its own.
/// </summary>
public sealed class AefNdjsonFile
{
    internal AefNdjsonFile(AefReadException? problem, IReadOnlyList<AefNdjsonLine> lines)
    {
        Problem = problem;
        Lines = lines;
    }

    /// <summary>The problem with the whole file, or null when its lines were read.</summary>
    public AefReadException? Problem { get; }

    /// <summary>The lines, in file order (none when <see cref="Problem"/> is set; none for an empty file).</summary>
    public IReadOnlyList<AefNdjsonLine> Lines { get; }

    /// <summary>The file and every line were read.</summary>
    public bool IsValid => Problem is null && Lines.All(l => l.Problem is null);
}

/// <summary>
/// Reads NDJSON files (contracts/aef/1/spec/02-encoding.md, §2.2): JSON objects one per line, UTF-8 with no byte-order
/// mark, lines separated by LF alone, every line (the last included) ending in LF, no blank lines, no CR anywhere
/// ([ENC-5]); an empty file holds no lines. Lines split on LF alone: U+2028 and U+2029 inside a string are text
/// ([ENC-6]). A file whose last line does not end in LF is incomplete and never read as finished ([ENC-7]).
/// </summary>
public static class AefNdjson
{
    /// <summary>Reads a whole NDJSON file.</summary>
    public static AefNdjsonFile Read(ReadOnlySpan<byte> bytes)
    {
        if (FramingProblem(bytes) is { } framing)
        {
            return new AefNdjsonFile(new AefEncodingException(framing), []);
        }

        var count = bytes.Count((byte)'\n');
        if (count > AefLimits.MaxLines)
        {
            return new AefNdjsonFile(
                new AefLimitException($"{count} lines: an NDJSON file holds at most {AefLimits.MaxLines} ([ENC-17])."), []);
        }

        var lines = new List<AefNdjsonLine>(count);
        var start = 0;
        while (start < bytes.Length)
        {
            var length = bytes[start..].IndexOf((byte)'\n');
            var line = bytes.Slice(start, length);
            JsonObject? value = null;
            AefReadException? problem = null;
            try
            {
                value = AefJsonReader.ParseDocument(line);
            }
            catch (AefReadException e)
            {
                problem = e;
            }

            lines.Add(new AefNdjsonLine(lines.Count + 1, start, length, value, problem));
            start += length + 1;
        }

        return new AefNdjsonFile(null, lines);
    }

    /// <summary>
    /// Null when the bytes are framed as [ENC-5] and [ENC-7] require, else why not: a byte-order mark, a CR, a blank line,
    /// or a last line without its LF (an incomplete file).
    /// </summary>
    public static string? FramingProblem(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return "A byte-order mark ([ENC-5]).";
        }

        if (bytes.Contains((byte)'\r'))
        {
            return "A CR: lines are separated by LF alone ([ENC-5]).";
        }

        if (bytes[^1] != (byte)'\n')
        {
            return "The last line does not end in LF: the file is incomplete ([ENC-7]).";
        }

        if (bytes[0] == (byte)'\n' || bytes.IndexOf("\n\n"u8) >= 0)
        {
            return "A blank line ([ENC-5]).";
        }

        return null;
    }

    /// <summary>
    /// How many bytes of a file still being written form complete lines: everything up to and including its last LF
    /// ([ENC-7], [STRM-2]: a last line without LF is still being written, and is not read).
    /// </summary>
    public static int CompleteLength(ReadOnlySpan<byte> bytes) => bytes.LastIndexOf((byte)'\n') + 1;
}
