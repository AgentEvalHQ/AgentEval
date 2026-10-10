// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Adapters.Otel;

/// <summary>How <see cref="AefOtelExporter"/> reads the AEF run.</summary>
public sealed record AefOtelExportOptions
{
    /// <summary>
    /// The trust policy the run is verified with ([SIG-4]), or null: a redaction only its identities may authorize
    /// withholds a reasoning blob ([OVL-10]); without one, a deleted blob makes the run invalid, and it is not exported.
    /// </summary>
    public TrustPolicy? Policy { get; init; }
}

/// <summary>What an export to OpenTelemetry wrote.</summary>
/// <param name="RunId">The AEF run's <c>runId</c> (not on any event: OpenTelemetry has no attribute for it).</param>
/// <param name="Outcome">The run's verification outcome (§4.5): <c>intact</c> or <c>unsealed</c>; an invalid run is refused.</param>
/// <param name="Lines">The OTLP/JSON <c>LogsData</c> lines, one per result line of the run, in its order, each without its LF.</param>
/// <param name="Events">The <c>gen_ai.evaluation.result</c> events in them: one per score, and one per line without scores.</param>
/// <param name="Notes">What the export did not carry, beyond the page's fixed list, in plain words.</param>
public sealed record AefOtelExport(
    string RunId, AefOutcome Outcome, IReadOnlyList<string> Lines, int Events, IReadOnlyList<string> Notes)
{
    /// <summary>The lines as a file: UTF-8, no byte-order mark, each line ending in LF (OpenTelemetry's file exporter: one object per line).</summary>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(string.Concat(Lines.Select(l => l + "\n")));
}

/// <summary>
/// An export refused, naming the rule (contracts/aef/1/interop/opentelemetry.md): a run that does not verify (OT-8), a
/// line the page's rules cannot name an event for (OT-1), or a reasoning blob or a time OTLP/JSON cannot carry (OT-9).
/// The whole export is refused, and nothing is written.
/// </summary>
public sealed class AefOtelExportException(string message) : Exception(message);

/// <summary>
/// AEF → OpenTelemetry (contracts/aef/1/interop/opentelemetry.md, "AEF → OpenTelemetry" and its rules settled 10-09):
/// an AEF run as OTLP/JSON <c>LogsData</c> lines of <c>gen_ai.evaluation.result</c> events, one line per result line.
/// <list type="bullet">
/// <item>One event per score of a line: <c>gen_ai.evaluation.name</c> is the score's metric and
/// <c>gen_ai.evaluation.score.value</c> its value. A line without scores gives one event without a value, named after
/// the metric of the summary entries at its path in the lanes it belongs to ([SUM-3]); a line for which they name no
/// metric or more than one (a run with no <c>summary.json</c>, such as a running one) is refused (OT-1).</item>
/// <item><c>gen_ai.evaluation.score.label</c> is the line's state name (RUN-14), and a line in state <c>error</c> has
/// <c>error.type</c> <c>_OTHER</c> beside it.</item>
/// <item><c>gen_ai.evaluation.explanation</c> is the line's <c>reason</c>, else its reasoning blob's text; a run with
/// <c>contentCapture: off</c> gets none at all (OT-3).</item>
/// <item><c>test.case.name</c> is the <c>caseId</c>; the log record's <c>traceId</c> and <c>spanId</c> are the
/// <c>traceLink</c>'s; its <c>timeUnixNano</c> the line's <c>endedAt</c>, else its <c>startedAt</c>, else none (OTLP
/// reads none as unknown).</item>
/// <item>The envelope (OT-2): one <c>LogsData</c> per result line, holding its events under the instrumentation scope
/// <c>agenteval</c>, with <c>service.name</c> (run.json <c>subject.telemetry.serviceName</c>) as the only resource
/// attribute, and no resource when run.json names no service.</item>
/// </list>
/// Only a run that verifies is exported (OT-8): <c>intact</c> or <c>unsealed</c>, with no problem but a withhold a
/// redaction the trust policy authorizes. The lines are the sealed ones (OT-7): overlays (an override's state, say) are
/// not applied. A reasoning blob sent as an explanation that is not UTF-8 or is over 4 MiB, and a time
/// <c>timeUnixNano</c> cannot hold, refuse the whole export (OT-9). The page fixes values, not bytes (R7N-3): a score's
/// value is written as the AEF line spells the number (<c>1.0</c> stays <c>1.0</c>), every string as AEF's writer writes
/// one (UTF-8, only <c>"</c>, <c>\</c> and C0 controls escaped), members and attributes in the checked example's order.
/// </summary>
public static class AefOtelExporter
{
    /// <summary>The OpenTelemetry event name ("The evaluation event").</summary>
    public const string EventName = "gen_ai.evaluation.result";

    /// <summary>The instrumentation scope of the events (OT-2).</summary>
    public const string ScopeName = "agenteval";

    /// <summary><c>error.type</c>'s fallback value, beside the label <c>error</c>: AEF records no error class.</summary>
    public const string OtherErrorType = "_OTHER";

    /// <summary>Exports the run in <paramref name="runDirectory"/>, in memory.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    /// <exception cref="IOException">The folder or a file cannot be read.</exception>
    /// <exception cref="AefOtelExportException">The run does not verify, or the page's rules refuse a line.</exception>
    public static AefOtelExport Export(string runDirectory, AefOtelExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runDirectory);
        options ??= new AefOtelExportOptions();
        var folder = AefRunFolder.Open(runDirectory);
        var verification = AefRunVerifier.Verify(folder, new AefVerifyOptions { Policy = options.Policy });
        if (verification.Outcome == AefOutcome.Invalid)
        {
            // OT-8: only a run that verifies (intact or unsealed, with no problem but an authorized withhold).
            throw new AefOtelExportException(
                $"{runDirectory} is invalid (spec 04 §4.5): {string.Join(", ", verification.Problems.Where(p => p.Code != "withheld").Take(10).Select(p => $"{p.Path} {p.Code}"))}"
                + " — only a run that verifies is exported (OT-8).");
        }

        var documents = AefRunDocuments.Read(folder);
        var run = documents.Run!;
        var runId = Str(run["runId"])!;
        var contentOff = Str(run["contentCapture"]) == "off";   // [RUN-11]: none written reads as on
        var serviceName = Str(run["subject"]?["telemetry"]?["serviceName"]);
        var lanes = Lanes(documents.Summary);
        var notes = new List<string>();
        var lines = new List<string>();
        var events = 0;
        var withheld = 0;

        foreach (var (number, line) in documents.Results.Objects)
        {
            var where = $"results.ndjson:{number.ToString(CultureInfo.InvariantCulture)}";
            var state = Str(line["state"])!;
            var explanation = contentOff ? null : Explanation(folder, line, where, ref withheld);

            // One event per score; a line without scores, one event named by the summary (OT-1).
            var names = new List<(string Metric, JsonNode? Value)>();
            if (line["scores"] is JsonArray { Count: > 0 } scores)
            {
                foreach (var score in scores)
                {
                    names.Add((Str(score!["metric"])!, NumberAsWritten(score["value"]!)));
                }
            }
            else
            {
                names.Add((MetricOfALineWithoutScores(line, lanes, where), null));
            }

            var records = new JsonArray();
            foreach (var (metric, value) in names)
            {
                records.Add(Record(line, where, metric, value, state, explanation));
            }

            var resourceLogs = new JsonObject();
            if (serviceName is not null)
            {
                resourceLogs["resource"] = new JsonObject
                {
                    ["attributes"] = new JsonArray(Attribute("service.name", serviceName)),
                };
            }

            resourceLogs["scopeLogs"] = new JsonArray(new JsonObject
            {
                ["scope"] = new JsonObject { ["name"] = ScopeName },
                ["logRecords"] = records,
            });
            lines.Add(OtlpJson.Write(new JsonObject { ["resourceLogs"] = new JsonArray(resourceLogs) }));
            events += records.Count;
        }

        if (contentOff)
        {
            notes.Add("The run keeps no content (contentCapture: off): no event carries gen_ai.evaluation.explanation, not even a line's reason (OT-3, SEC-6).");
        }

        if (withheld > 0)
        {
            notes.Add($"{withheld} line(s) cite a reasoning blob a redaction withholds (OVL-10): their events carry no explanation.");
        }

        if (serviceName is null)
        {
            notes.Add("run.json names no subject.telemetry.serviceName: the lines carry no resource (OT-2).");
        }

        return new AefOtelExport(runId, verification.Outcome, lines, events, notes);
    }

    /// <summary>
    /// Exports the run into <paramref name="outputFile"/> (one <c>LogsData</c> per line, UTF-8, LF): written only when the
    /// whole export succeeded, and never over an existing file.
    /// </summary>
    /// <exception cref="IOException">The output file exists, or a file cannot be read or written.</exception>
    /// <exception cref="AefOtelExportException">The run does not verify, or the page's rules refuse a line.</exception>
    public static AefOtelExport ExportToFile(string runDirectory, string outputFile, AefOtelExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputFile);
        if (File.Exists(outputFile) || Directory.Exists(outputFile))
        {
            throw new IOException($"{outputFile} exists: the export writes a new file, never over one.");
        }

        var export = Export(runDirectory, options);
        var full = Path.GetFullPath(outputFile);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, export.ToBytes());
            File.Move(temporary, full, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }

        return export;
    }

    // ------------------------------------------------------------------ one event

    // A log record: [timeUnixNano], eventName, [traceId, [spanId]], attributes (name, [value], label, [error.type],
    // [explanation], test.case.name), in the order of the page's worked example.
    private static JsonObject Record(JsonObject line, string where, string metric, JsonNode? value, string state, string? explanation)
    {
        var record = new JsonObject();
        if ((Str(line["endedAt"]) ?? Str(line["startedAt"])) is { } time)
        {
            record["timeUnixNano"] = UnixNano(time, where);
        }

        record["eventName"] = EventName;
        if (line["traceLink"] is JsonObject traceLink)
        {
            record["traceId"] = Str(traceLink["traceId"]);
            if (Str(traceLink["spanId"]) is { } spanId)
            {
                record["spanId"] = spanId;
            }
        }

        var attributes = new JsonArray(Attribute("gen_ai.evaluation.name", metric));
        if (value is not null)
        {
            attributes.Add(new JsonObject { ["key"] = "gen_ai.evaluation.score.value", ["value"] = new JsonObject { ["doubleValue"] = value } });
        }

        attributes.Add(Attribute("gen_ai.evaluation.score.label", state));
        if (state == "error")
        {
            attributes.Add(Attribute("error.type", OtherErrorType));
        }

        if (explanation is not null)
        {
            attributes.Add(Attribute("gen_ai.evaluation.explanation", explanation));
        }

        attributes.Add(Attribute("test.case.name", Str(line["caseId"])!));
        record["attributes"] = attributes;
        return record;
    }

    private static JsonObject Attribute(string key, string value) =>
        new() { ["key"] = key, ["value"] = new JsonObject { ["stringValue"] = value } };

    // OTLP/JSON writes a fixed64 as a decimal string: nanoseconds since the Unix epoch. OT-9: a time timeUnixNano cannot
    // hold refuses the export: before 1970-01-01T00:00:00.000000001Z (0 means unknown) or after
    // 2554-07-21T23:34:33.709551615Z (an unsigned 64-bit count of nanoseconds).
    private static string UnixNano(string time, string where)
    {
        var parsed = AefTime.Parse(time);
        var nanos = ((Int128)parsed.Seconds * 1_000_000_000) + parsed.Nanoseconds;
        if (nanos <= 0 || nanos > ulong.MaxValue)
        {
            throw new AefOtelExportException(
                $"{where}: {time} is a time timeUnixNano cannot hold (1970-01-01T00:00:00.000000001Z to 2554-07-21T23:34:33.709551615Z; 0 means unknown): the export is refused (OT-9).");
        }

        return nanos.ToString(CultureInfo.InvariantCulture);
    }

    // The value of a score, as the line spells the number: a fresh node of the same text.
    private static JsonNode NumberAsWritten(JsonNode value) => JsonNode.Parse(value.ToJsonString())!;

    // reason, else the reasoning blob's text; a withheld blob ([OVL-10]: the run verified, so a missing blob is a
    // withheld one) gives none.
    private static string? Explanation(AefRunFolder folder, JsonObject line, string where, ref int withheld)
    {
        if (Str(line["reason"]) is { } reason)
        {
            return reason;
        }

        if (Str(line["reasoning"]?["blob"]) is not { } blob)
        {
            return null;
        }

        var path = AefRunFolder.BlobPath(blob["sha256:".Length..]);
        if (!folder.Has(path))
        {
            withheld++;
            return null;
        }

        if (folder.Size(path) > AefLimits.MaxJsonBytes)
        {
            throw new AefOtelExportException($"{where}: its reasoning blob, sent as the explanation, holds {folder.Size(path)} bytes, over 4 MiB: the export is refused (OT-9).");
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(folder.Read(path, AefLimits.MaxJsonBytes));
        }
        catch (DecoderFallbackException)
        {
            throw new AefOtelExportException($"{where}: its reasoning blob, sent as the explanation, is not UTF-8 text (gen_ai.evaluation.explanation is a string): the export is refused (OT-9).");
        }
    }

    // ------------------------------------------------------------------ OT-1

    // The summary's lanes: name → the entries' (path, metric).
    private static List<(string Lane, List<(string Path, string Metric)> Entries)> Lanes(JsonObject? summary) =>
        [.. Objs(summary?["lanes"]).Select(lane => (
            Str(lane["lane"])!,
            Objs(lane["metrics"]).Select(e => (Str(e["path"])!, Str(e["metric"])!)).ToList()))];

    // [SUM-3]: a line belongs to the lane its lane names; when the summary has a single lane, a line without lane belongs
    // to it. The event is named after the metric of the summary entries at the line's path there: exactly one (OT-1).
    private static string MetricOfALineWithoutScores(JsonObject line, List<(string Lane, List<(string Path, string Metric)> Entries)> lanes, string where)
    {
        var path = Str(line["path"])!;
        var lane = Str(line["lane"]) ?? (lanes.Count == 1 ? lanes[0].Lane : null);
        var metrics = lanes.Where(l => l.Lane == lane)
            .SelectMany(l => l.Entries).Where(e => e.Path == path).Select(e => e.Metric).Distinct(StringComparer.Ordinal).ToList();
        return metrics.Count switch
        {
            1 => metrics[0],
            0 => throw new AefOtelExportException(
                $"{where} has no scores, and no summary entry at its path {path} {(lane is null ? "in a lane it belongs to" : $"in its lane {lane}")} names a metric"
                + " to name its event after (OT-1: gen_ai.evaluation.name is required; a run with no summary.json, such as a running one, has none)."),
            _ => throw new AefOtelExportException(
                $"{where} has no scores, and the summary entries at its path {path} in its lane {lane} name {metrics.Count} metrics ({string.Join(", ", metrics)}):"
                + " the event takes one name (OT-1)."),
        };
    }

    // A string value, or null (the run verified: a member the schema types is of its type).
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    // The object items of an array, or none.
    private static IEnumerable<JsonObject> Objs(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

    // ------------------------------------------------------------------ OTLP/JSON text

    /// <summary>
    /// One JSON value on one line: strings as AEF's writer writes them (<see cref="AefJsonWriter"/>: UTF-8, only <c>"</c>,
    /// <c>\</c> and C0 controls escaped), numbers as their nodes spell them (a score's value as its line wrote it).
    /// </summary>
    internal static class OtlpJson
    {
        public static string Write(JsonNode value)
        {
            var output = new ArrayBufferWriter<byte>();
            Write(output, value);
            return Encoding.UTF8.GetString(output.WrittenSpan);
        }

        private static void Write(ArrayBufferWriter<byte> output, JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    output.Write("{"u8);
                    var first = true;
                    foreach (var (name, value) in obj)
                    {
                        if (!first)
                        {
                            output.Write(","u8);
                        }

                        first = false;
                        output.Write(AefJsonWriter.Compact(JsonValue.Create(name)));
                        output.Write(":"u8);
                        Write(output, value);
                    }

                    output.Write("}"u8);
                    break;
                case JsonArray array:
                    output.Write("["u8);
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (i > 0)
                        {
                            output.Write(","u8);
                        }

                        Write(output, array[i]);
                    }

                    output.Write("]"u8);
                    break;
                case JsonValue number when number.GetValueKind() == JsonValueKind.Number:
                    output.Write(Encoding.UTF8.GetBytes(number.ToJsonString()));
                    break;
                default:
                    output.Write(AefJsonWriter.Compact(node));
                    break;
            }
        }
    }
}
