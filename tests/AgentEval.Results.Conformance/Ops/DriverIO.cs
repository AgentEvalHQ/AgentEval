// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// Reading an operation's inputs and writing its result: a file that cannot be read, or that is not the I-JSON its
/// operation needs, is an input error (<see cref="UsageException"/>, exit code 2); the result is one JSON value, written
/// as AEF writes JSON (UTF-8, integers in plain digits), on one line.
/// </summary>
internal static class DriverIO
{
    /// <summary>
    /// Whether an exception AgentEval.Results threw is its refusal of the input ([CONF-3]: exit 2), as its public
    /// contract documents refusals: an <see cref="ArgumentException"/> (an argument it refuses: a line, a header, a
    /// request, a time) or an <see cref="InvalidOperationException"/> (a state it refuses: an open run, a run already
    /// sealed, a rule the whole run breaks). Not an <see cref="ArgumentNullException"/> (a call the driver got wrong),
    /// and not an <see cref="AefWriteException"/>, which reports that the writer's own verifier found what it wrote wrong:
    /// those, and every other exception, are failures nobody expected, and the driver exits 1 (<see cref="Program.Main"/>).
    /// </summary>
    public static bool IsInputError(Exception e) =>
        e is (ArgumentException and not ArgumentNullException) or (InvalidOperationException and not AefWriteException and not ObjectDisposedException);

    /// <summary>Exactly <paramref name="min"/> to <paramref name="max"/> arguments, else the usage.</summary>
    public static void Arguments(string[] args, int min, int max, string usage)
    {
        if (args.Length < min || args.Length > max || args.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new UsageException($"usage: {usage}");
        }
    }

    /// <summary>A file's bytes.</summary>
    public static byte[] Bytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"{path}: {e.Message}");
        }
    }

    /// <summary>Runs an operation that reads <paramref name="path"/>; a file that cannot be read is an input error.</summary>
    public static T Guard<T>(Func<T> operation, string path)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"{path}: {e.Message}");
        }
    }

    /// <summary>A JSON document (an I-JSON object, [ENC-1]).</summary>
    public static JsonObject Document(string path) => Document(Bytes(path), path);

    /// <summary>A JSON document from bytes already read.</summary>
    public static JsonObject Document(byte[] bytes, string path)
    {
        try
        {
            return AefJsonReader.ParseDocument(bytes, AefLimits.MaxBytesOf(path));
        }
        catch (AefReadException e)
        {
            throw new UsageException($"{path}: not an I-JSON document: {e.Message}");
        }
    }

    /// <summary>Any I-JSON value (a list of paths).</summary>
    public static JsonNode? Value(string path)
    {
        try
        {
            return AefJsonReader.ParseValue(Bytes(path));
        }
        catch (AefReadException e)
        {
            throw new UsageException($"{path}: not an I-JSON text: {e.Message}");
        }
    }

    /// <summary>Writes the result and returns exit code 0.</summary>
    public static int Print(TextWriter stdout, JsonNode? result)
    {
        stdout.Write(Encoding.UTF8.GetString(AefJsonWriter.Compact(result)));
        stdout.Write('\n');
        return 0;
    }

    /// <summary>Problems as [path, code] pairs ([CONF-2]).</summary>
    public static JsonArray Pairs(IEnumerable<(string Path, string Code)> problems) =>
        new(problems.Select(p => (JsonNode?)new JsonArray(p.Path, p.Code)).ToArray());

    /// <summary>A file the command line contract treats as NDJSON (spec 09: "NDJSON: every line").</summary>
    public static bool IsNdjson(string path) =>
        path.EndsWith(".ndjson", StringComparison.Ordinal) || path.EndsWith(".jsonl", StringComparison.Ordinal);
}
