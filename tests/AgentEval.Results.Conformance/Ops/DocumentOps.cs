// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// What one document or one run's contents must be: <c>document</c> (schema verdicts and the readings of §7.3: the
/// <c>document</c>, <c>reader-only</c> and <c>plan</c> vectors), <c>paths</c> ([RUN-3]) and <c>result-id</c> ([RES-4]).
/// </summary>
internal static class DocumentOps
{
    /// <summary>
    /// <c>document SCHEMA FILE</c>: <c>{"writer": "valid"|"invalid", "reader": …, "reads": {field: value as read}}</c>.
    /// SCHEMA is a schema name (<c>run</c>) or a subschema (<c>decision#/$defs/input</c>). A file named <c>*.ndjson</c>
    /// or <c>*.jsonl</c> is valid when every line is. Bytes a reader refuses (not I-JSON, beyond a limit) are invalid on
    /// both sides. The readings are those of a document the reader accepts, and of a single document.
    /// </summary>
    public static int Document(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 2, 2, "document SCHEMA FILE");
        var (schema, path) = (args[0], args[1]);
        if (!AefSchemas.Writer.Has(schema) || !AefSchemas.Reader.Has(schema))
        {
            throw new UsageException($"'{schema}' is not an AEF schema (known: {string.Join(", ", AefSchemas.Names)}).");
        }

        var bytes = DriverIO.Bytes(path);
        List<JsonObject>? documents;
        if (DriverIO.IsNdjson(path))
        {
            var file = AefNdjson.Read(bytes);
            documents = file.IsValid ? [.. file.Lines.Select(l => l.Value!)] : null;
        }
        else
        {
            try
            {
                // [ENC-17]: a seal or a batch seal may be up to 40 MiB, whatever the file is called here.
                var maxBytes = schema.Split('#')[0] is "seal" or "overlay-seal" ? AefLimits.MaxSealBytes : AefLimits.MaxBytesOf(path);
                documents = [AefJsonReader.ParseDocument(bytes, maxBytes)];
            }
            catch (AefReadException)
            {
                documents = null;
            }
        }

        var writer = documents is not null && documents.All(d => AefSchemas.Writer.IsValid(schema, d));
        var reader = documents is not null && documents.All(d => AefSchemas.Reader.IsValid(schema, d));
        var reads = new JsonObject();
        if (reader && documents is [var single])
        {
            foreach (var reading in AefReadings.Read(schema, single))
            {
                reads[reading.Field] = reading.Read?.DeepClone();
            }
        }

        return DriverIO.Print(stdout, new JsonObject
        {
            ["writer"] = writer ? "valid" : "invalid",
            ["reader"] = reader ? "valid" : "invalid",
            ["reads"] = reads,
        });
    }

    /// <summary>
    /// <c>paths FILE</c>: FILE holds a JSON list of the paths of one run folder; <c>{"problems": [[path, "path"], …]}</c>
    /// ([RUN-3], ordered as §3.9 orders them). It may instead hold the corpus form, a list of
    /// <c>{"name", "paths", …}</c>: then <c>[{"name", "problems"}, …]</c>.
    /// </summary>
    public static int Paths(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 1, 1, "paths FILE");
        static List<string>? Strings(JsonNode? node) =>
            node is JsonArray list && list.All(p => p is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                ? [.. list.Select(p => p!.GetValue<string>())]
                : null;
        static JsonArray Problems(List<string> paths) => DriverIO.Pairs(AefPaths.Check(paths).Select(p => (p.Path, p.Code)));

        var value = DriverIO.Value(args[0]);
        if (Strings(value) is { } paths)
        {
            return DriverIO.Print(stdout, new JsonObject { ["problems"] = Problems(paths) });
        }

        if (value is JsonArray items && items.All(i => i is JsonObject o && Strings(o["paths"]) is not null))
        {
            return DriverIO.Print(stdout, new JsonArray(items.Select(i => (JsonNode?)new JsonObject
            {
                ["name"] = i!["name"]?.DeepClone(),
                ["problems"] = Problems(Strings(i["paths"])!),
            }).ToArray()));
        }

        throw new UsageException($"{args[0]}: not a list of paths");
    }

    /// <summary>
    /// <c>result-id RUNID CASEID PATH [TRIAL]</c>: <c>{"resultId": "r_…"}</c> ([RES-4]). TRIAL is a JSON number with
    /// an integral value (3 and 3.0 are trial 3, [ENC-4]), at least 0.
    /// </summary>
    public static int ResultId(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 3, 4, "result-id RUNID CASEID PATH [TRIAL]");
        long? trial = null;
        if (args.Length == 4)
        {
            JsonNode? number;
            try
            {
                number = AefJsonReader.ParseValue(Encoding.UTF8.GetBytes(args[3]));
            }
            catch (AefReadException e)
            {
                throw new UsageException($"trial '{args[3]}' is not a JSON number: {e.Message}");
            }

            var value = number is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? (double)v : double.NaN;
            trial = value >= 0 && value <= 9_007_199_254_740_991 && Math.Floor(value) == value
                ? (long)value
                : throw new UsageException($"trial '{args[3]}' is not a trial number (an integer from 0 to 2^53 - 1).");
        }

        try
        {
            return DriverIO.Print(stdout, new JsonObject { ["resultId"] = AefResultId.Compute(args[0], args[1], args[2], trial) });
        }
        catch (ArgumentException e)
        {
            throw new UsageException(e.Message);
        }
    }
}
