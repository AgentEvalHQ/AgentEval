// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Otel;

/// <summary>
/// How <see cref="AefOtelImporter"/> writes the AEF run: the run header the events do not give (OT-4), taken from the
/// person converting. <see cref="AefConversionOptions.TimeProvider"/> is the conversion time: the seal's
/// <c>sealedAt</c>, and the run's times when no event has one. <see cref="AefConversionOptions.ContentCapture"/> is the
/// run's <c>contentCapture</c>, the converter's choice (OT-4): <c>on</c> unless asked otherwise; with <c>off</c>, logs
/// whose records carry content ([SEC-6]) are refused (OT-8).
/// </summary>
public sealed record AefOtelImportOptions : AefConversionOptions
{
    /// <summary>run.json <c>runId</c>: an AEF id.</summary>
    public required string RunId { get; init; }

    /// <summary>run.json <c>imported.from</c>: the source tool and version.</summary>
    public required string From { get; init; }

    /// <summary>run.json <c>subject.ref</c>: a typed reference (<c>agent:support/support-triage</c>).</summary>
    public required string SubjectRef { get; init; }

    /// <summary>run.json <c>subject.kind</c>.</summary>
    public AefSubjectKind SubjectKind { get; init; } = AefSubjectKind.Agent;

    /// <summary>run.json <c>execution.targetMode</c>: how the evaluated operations drove their target ([RUN-7]).</summary>
    public required AefTargetMode TargetMode { get; init; }
}

/// <summary>An import refused, naming the rule (contracts/aef/1/interop/opentelemetry.md, "Refused"); nothing is written.</summary>
public sealed class AefOtelImportException(string message) : Exception(message);

/// <summary>
/// OpenTelemetry → AEF (contracts/aef/1/interop/opentelemetry.md, "OpenTelemetry → AEF" and its rules settled 10-09):
/// an OTLP/JSON logs file (one <c>LogsData</c> per line, OpenTelemetry's file exporter) as an imported AEF run
/// ([RUN-15]), sealed <c>ingest</c>.
/// <list type="bullet">
/// <item>Each <c>gen_ai.evaluation.result</c> event (a log record whose <c>eventName</c> is that) is a result line: its
/// <c>gen_ai.evaluation.name</c> is the line's <c>path</c>, its evaluator's <c>id</c> and its score's <c>metric</c>;
/// <c>test.case.name</c> (else <c>gen_ai.response.id</c>) its <c>caseId</c>; <c>timeUnixNano</c> its <c>endedAt</c>;
/// <c>traceId</c> and <c>spanId</c> its <c>traceLink</c>.</item>
/// <item>The state: a label that is an AEF state name is the state; another label is <c>scored</c>, kept in
/// <c>scores[].label</c>; a value without a label is <c>scored</c>; <c>error.type</c> is <c>error</c>, with the type as
/// the <c>reason</c> unless the event has an explanation (OT-5). <c>gen_ai.evaluation.explanation</c> is the
/// <c>reason</c>, cut to 4096 characters.</item>
/// <item>The run (OT-4): its id, <c>imported.from</c>, the subject's ref and kind and the target mode are the person's;
/// <c>status: completed</c>; <c>startedAt</c> and <c>endedAt</c> the earliest and latest event time (the conversion time
/// when no event has one); <c>contentCapture</c> the converter's choice (<c>on</c> unless asked otherwise);
/// <c>subject.telemetry.serviceName</c> the resources' <c>service.name</c>; <c>otel.schemaUrls</c> the source's schema
/// URLs; each supplied field listed in <c>imported.asserted</c>. A metric is declared only when a line scores it
/// (<c>kind: score</c>, <c>direction: none</c>, <c>scale: unbounded</c>); <c>summary.json</c> has no lanes; every line
/// of the source is a line of <c>logs.otlp.jsonl</c>. Traces are not read. The run is sealed at the conversion time and
/// verified (OT-8).</item>
/// <item>Each line (OT-10): <c>evaluator.id</c> is the event's name; an explanation longer than 4096 characters is cut
/// to its first 4096.</item>
/// </list>
/// Refused, naming the rule, with nothing written: resources naming more than one <c>service.name</c> (OT-4); logs
/// whose records carry content when asked for <c>contentCapture: off</c>, and a run that does not verify (OT-8); an
/// event without <c>gen_ai.evaluation.name</c>, a label longer than 64 characters, an event with neither
/// <c>test.case.name</c> nor <c>gen_ai.response.id</c>, a second event of one case with one name, a label that is not a
/// state name without a value, a typed-absence label with a value or without an explanation (the label <c>error</c>
/// beside an <c>error.type</c> excepted, OT-5), the label <c>pending</c>, a label other than <c>error</c> beside
/// <c>error.type</c>, an event with no label, no value and no <c>error.type</c> (OT-6); and a conversion time before the
/// last event ([SEAL-1]: a run is sealed after it closes).
/// </summary>
public static class AefOtelImporter
{
    /// <summary>The fields of run.json the converter supplies (OT-4): the person's, its own status and times, and its <c>contentCapture</c>.</summary>
    public static readonly IReadOnlyList<string> Asserted =
        ["runId", "status", "subject.ref", "subject.kind", "execution.targetMode", "startedAt", "endedAt", "contentCapture"];

    private static readonly HashSet<string> MeasuredStates = new(StringComparer.Ordinal) { "passed", "failed", "warn", "inconclusive", "scored" };
    private static readonly HashSet<string> AbsentStates = new(StringComparer.Ordinal) { "not_measured", "not_applicable", "skipped", "error", "pending" };

    // [SEC-6]: the attributes that carry content, which a run with contentCapture off holds on no record, resource or scope.
    private static readonly HashSet<string> ContentAttributes = new(StringComparer.Ordinal)
    {
        "gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions", "gen_ai.tool.call.arguments",
        "gen_ai.tool.call.result", "gen_ai.evaluation.explanation", "gen_ai.prompt", "gen_ai.completion",
    };

    /// <summary>Imports the OTLP/JSON logs file <paramref name="logsFile"/> into the AEF run folder <paramref name="outputDirectory"/>.</summary>
    /// <param name="logsFile">The OTLP/JSON logs: one <c>LogsData</c> object per line.</param>
    /// <param name="outputDirectory">The AEF run folder to write: it must not exist, or be empty.</param>
    /// <param name="options">The run header the events do not give, and how to seal.</param>
    /// <exception cref="AefOtelImportException">The page refuses the events (the message names the rule).</exception>
    /// <exception cref="InvalidDataException">The file is not OTLP/JSON logs, or a value cannot be written as AEF.</exception>
    /// <exception cref="ArgumentException">The output folder is not empty.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static AefConversion Import(string logsFile, string outputDirectory, AefOtelImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(logsFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);
        if (!AefConverter.IsId(options.RunId))
        {
            throw new InvalidDataException($"The run id '{options.RunId}' is not an AEF id (1-128 letters, digits, '.', '_', ':' or '-').");
        }

        // Read everything, and refuse what the page refuses, before anything is written.
        var source = Read(logsFile);
        var events = source.Events;
        var services = events.Select(e => e.Service).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (services.Count > 1)
        {
            throw new AefOtelImportException($"The events' resources name {services.Count} service.name values ({string.Join(", ", services)}): a run has one subject (OT-4).");
        }

        var seen = new Dictionary<(string Case, string Name), string>();
        foreach (var e in events)
        {
            if (seen.TryGetValue((e.CaseId, e.Name), out var first))
            {
                throw new AefOtelImportException(
                    $"{e.Where}: a second event of case '{e.CaseId}' named '{e.Name}' (the first is {first}): the path comes from the name, so the two lines would have one result id (OT-6).");
            }

            seen[(e.CaseId, e.Name)] = e.Where;
        }

        var clock = options.TimeProvider ?? TimeProvider.System;
        var now = AefTime.FromDateTimeOffset(clock.GetUtcNow());
        var times = events.Select(e => e.Time).OfType<AefTime>().ToList();
        var startedAt = times.Count > 0 ? times.Min() : now;
        var endedAt = times.Count > 0 ? times.Max() : now;
        if (options.Seal && now < endedAt)
        {
            throw new AefOtelImportException(
                $"The conversion time {now} is before the last event ({endedAt}): the run is sealed at the conversion time, and only a closed run is sealed ([SEAL-1]).");
        }

        // OT-8: asked for contentCapture off, logs whose records carry content would make a run that does not verify
        // ([SEC-6], content-capture).
        if (options.ContentCapture == AefContentCapture.Off && source.Lines.Select(Content).Select((what, i) => (What: what, Line: i + 1)).FirstOrDefault(c => c.What is not null) is { What: { } content } carrying)
        {
            throw new AefOtelImportException(
                $"line {carrying.Line}: {content}, which a run with contentCapture off does not hold ([SEC-6], content-capture): the run would not verify (OT-8).");
        }

        var notes = new List<string>
        {
            "The run id, the subject's ref and kind and the target mode are the converter's (imported.asserted); its status is completed and its times are the earliest and latest event time (OT-4).",
            "Each metric is declared kind score, direction none, scale unbounded: the event gives no kind, direction or range.",
            "Traces are not read: neither gen_ai.agent.id nor the parent spans are imported (OT-4).",
        };
        if (source.Skipped > 0)
        {
            notes.Add($"{source.Skipped} log record(s) are no gen_ai.evaluation.result event: they are in logs.otlp.jsonl only.");
        }

        if (times.Count == 0)
        {
            notes.Add("No event has a time: the run's startedAt and endedAt are the conversion time.");
        }

        var header = new AefRunHeader
        {
            RunId = options.RunId,
            Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = new AefSubject
            {
                Ref = options.SubjectRef,
                Kind = options.SubjectKind,
                Telemetry = services.Count == 1 ? new AefSubjectTelemetry { ServiceName = services[0] } : null,
            },
            StartedAt = startedAt,
            Otel = source.SchemaUrls.Count > 0 ? new AefOtel { SchemaUrls = source.SchemaUrls } : null,
            ContentCapture = options.ContentCapture,   // OT-4: the converter's choice, on unless asked otherwise
            Execution = new AefExecution { TargetMode = options.TargetMode },
            Imported = new AefImported { From = options.From, Asserted = Asserted },
        };

        var existed = Directory.Exists(outputDirectory);
        AefRunWriter writer;
        try
        {
            writer = AefRunWriter.Create(outputDirectory, header);
        }
        catch (ArgumentException e) when (e.ParamName != "directory")
        {
            throw new InvalidDataException($"The run's header cannot be written as AEF: {e.Message}", e);
        }

        try
        {
            writer.SetMetrics(events.Where(e => e.Value is not null).Select(e => e.Name).Distinct(StringComparer.Ordinal).Select(name => new AefMetric
            {
                Id = name,
                Kind = AefMetricKind.Score,
                Direction = AefMetricDirection.None,
                Scale = AefScale.Unbounded,
            }));
            foreach (var e in events)
            {
                writer.AddResult(new AefResult
                {
                    CaseId = e.CaseId,
                    Path = e.Name,
                    Evaluator = new AefEvaluator(e.Name),
                    State = AefNames.TryParse<AefState>(e.State, out var state) ? state.Value : throw new InvalidOperationException(e.State),
                    Reason = e.Reason,
                    Scores = e.Value is { } value ? [new AefScore { Metric = e.Name, Value = value, Label = e.ScoreLabel }] : null,
                    EndedAt = e.Time,
                    TraceLink = e.TraceId is null ? null : new AefTraceLink(e.TraceId, e.SpanId),
                });
            }

            foreach (var line in source.Lines)
            {
                writer.AddLogs(line);
            }

            writer.SetSummary(new AefSummary { Lanes = [] });   // OT-4: the events carry no summary
            return AefConverter.Finish(writer, options, options.From, Asserted, notes, AefRunStatus.Completed, endedAt, null);
        }
        catch (Exception e)
        {
            AefConverter.Discard(outputDirectory, existed);
            if (e is ArgumentException)
            {
                throw new InvalidDataException($"The events cannot be written as AEF: {e.Message}", e);
            }

            if (e is AefWriteException)
            {
                // OT-8: the run written from the events is verified, and refused when it does not verify.
                throw new AefOtelImportException($"The run written from the events does not verify, so nothing is written (OT-8): {e.Message}");
            }

            throw;
        }
    }

    // [SEC-6]: the first content a LogsData line carries (a log record's body, or an attribute SEC-6 names on a record, a
    // resource or a scope), or null.
    private static string? Content(JsonObject logsData)
    {
        string? In(JsonNode? attributes, string where) =>
            Objects(attributes).Select(a => Text(a["key"])).FirstOrDefault(k => k is not null && ContentAttributes.Contains(k)) is { } key
                ? $"{where} carries {key}"
                : null;

        foreach (var resourceLogs in Objects(logsData["resourceLogs"]))
        {
            if (In(resourceLogs["resource"]?["attributes"], "a resource") is { } resource)
            {
                return resource;
            }

            foreach (var scopeLogs in Objects(resourceLogs["scopeLogs"]))
            {
                if (In(scopeLogs["scope"]?["attributes"], "a scope") is { } scope)
                {
                    return scope;
                }

                foreach (var record in Objects(scopeLogs["logRecords"]))
                {
                    if (record.ContainsKey("body"))
                    {
                        return "a log record has a body";
                    }

                    if (In(record["attributes"], "a log record") is { } attribute)
                    {
                        return attribute;
                    }
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ reading the source

    private sealed record Source(List<JsonObject> Lines, List<Event> Events, List<string> SchemaUrls, int Skipped);

    // One evaluation event, read and placed: the line it becomes.
    private sealed record Event(
        string Where, string Name, string CaseId, string State, string? Reason, double? Value, string? ScoreLabel,
        AefTime? Time, string? TraceId, string? SpanId, string? Service);

    private static Source Read(string logsFile)
    {
        var bytes = File.ReadAllBytes(logsFile);
        var lines = new List<JsonObject>();
        var events = new List<Event>();
        var schemaUrls = new List<string>();
        var skipped = 0;
        var number = 0;
        foreach (var raw in Split(bytes))
        {
            number++;
            if (raw.Length == 0)
            {
                continue;
            }

            JsonObject logsData;
            try
            {
                logsData = AefJsonReader.ParseDocument(raw);
            }
            catch (AefReadException e)
            {
                throw new InvalidDataException($"{logsFile} line {number}: not an OTLP/JSON LogsData object: {e.Message}", e);
            }

            lines.Add(logsData);
            foreach (var resourceLogs in Objects(logsData["resourceLogs"]))
            {
                var service = Attributes(resourceLogs["resource"]?["attributes"], $"line {number}: a resource").GetValueOrDefault("service.name") is { } s
                    ? StringValue(s, $"line {number}: service.name")
                    : null;
                var resourceSchema = Text(resourceLogs["schemaUrl"]);
                foreach (var scopeLogs in Objects(resourceLogs["scopeLogs"]))
                {
                    var scopeSchema = Text(scopeLogs["schemaUrl"]);
                    var record = 0;
                    var any = false;
                    foreach (var logRecord in Objects(scopeLogs["logRecords"]))
                    {
                        record++;
                        if (Text(logRecord["eventName"]) != AefOtelExporter.EventName)
                        {
                            skipped++;
                            continue;
                        }

                        any = true;
                        events.Add(EventOf(logRecord, $"line {number}, log record {record}", service));
                    }

                    // The source's schema URLs (resource and scope), of the events' resources and scopes.
                    foreach (var url in new[] { resourceSchema, scopeSchema })
                    {
                        if (any && !string.IsNullOrEmpty(url) && !schemaUrls.Contains(url))
                        {
                            schemaUrls.Add(url);
                        }
                    }
                }
            }
        }

        return new Source(lines, events, schemaUrls, skipped);
    }

    // The lines of the file: LF-separated; a last line without LF is a line too.
    private static IEnumerable<byte[]> Split(byte[] bytes)
    {
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                yield return bytes[start..i];
                start = i + 1;
            }
        }

        if (start < bytes.Length)
        {
            yield return bytes[start..];
        }
    }

    private static Event EventOf(JsonObject logRecord, string where, string? service)
    {
        var attributes = Attributes(logRecord["attributes"], where);
        string? Str(string key) => attributes.GetValueOrDefault(key) is { } v ? StringValue(v, $"{where}: {key}") : null;

        var name = Str("gen_ai.evaluation.name")
                   ?? throw new AefOtelImportException($"{where}: no gen_ai.evaluation.name (Required): the line's path and metric come from it.");
        if (!AefConverter.IsResultText(name, 256))
        {
            throw new InvalidDataException($"{where}: the name '{name}' cannot be a path, an evaluator id and a metric (1-256 characters, no control character).");
        }

        var label = Str("gen_ai.evaluation.score.label");
        var explanation = Str("gen_ai.evaluation.explanation");
        var errorType = Str("error.type");
        double? value = null;
        if (attributes.GetValueOrDefault("gen_ai.evaluation.score.value") is { } v)
        {
            value = v["doubleValue"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number
                ? double.Parse(d.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
                : throw new InvalidDataException($"{where}: gen_ai.evaluation.score.value is not a finite doubleValue (the convention's type; AEF numbers are finite, [ENC-3]).");
        }

        var caseId = Str("test.case.name") ?? Str("gen_ai.response.id")
                     ?? throw new AefOtelImportException($"{where}: neither test.case.name nor gen_ai.response.id: a result needs a caseId (OT-6).");
        if (!AefConverter.IsResultText(caseId, 256))
        {
            throw new InvalidDataException($"{where}: the case '{caseId}' cannot be a caseId (1-256 characters, no control character, [RES-4]).");
        }

        // The state (the table's rows, OT-5, OT-6).
        string state;
        string? scoreLabel = null;
        string? reason = explanation;
        if (label is not null && (MeasuredStates.Contains(label) || AbsentStates.Contains(label)))
        {
            if (label == "pending")
            {
                throw new AefOtelImportException($"{where}: the label pending: a converted run is closed, and a closed run has no pending line ([RES-3], OT-6).");
            }

            if (errorType is not null && label != "error")
            {
                throw new AefOtelImportException($"{where}: the label {label} beside error.type {errorType}: error.type makes the state error (OT-6).");
            }

            if (AbsentStates.Contains(label))
            {
                if (value is not null)
                {
                    throw new AefOtelImportException($"{where}: the typed-absence label {label} with a score value: a typed absence has no scores ([RES-2], OT-6).");
                }

                // A typed absence has a reason: the explanation, or (label error) the error.type (OT-5).
                reason ??= errorType;
                if (reason is null)
                {
                    throw new AefOtelImportException($"{where}: the typed-absence label {label} without an explanation: a typed absence has a reason ([RES-2], OT-6).");
                }
            }

            state = label;
        }
        else if (label is not null)
        {
            if (errorType is not null)
            {
                throw new AefOtelImportException($"{where}: the label {label} beside error.type {errorType}: error.type makes the state error (OT-6).");
            }

            if (value is null)
            {
                throw new AefOtelImportException($"{where}: the label {label}, which is no state name, without a score value: scores[].value is required (OT-6).");
            }

            if (label.EnumerateRunes().Count() > 64)
            {
                throw new InvalidDataException($"{where}: the label '{label}' is longer than the 64 characters scores[].label holds.");
            }

            state = "scored";
            scoreLabel = label;
        }
        else if (errorType is not null)
        {
            if (value is not null)
            {
                throw new AefOtelImportException($"{where}: error.type with a score value: error.type makes the state error, a typed absence, which has no scores ([RES-2], OT-6).");
            }

            state = "error";
            reason ??= errorType;   // OT-5: with an explanation, the explanation is kept and the type is lost
        }
        else if (value is not null)
        {
            state = "scored";
        }
        else
        {
            throw new AefOtelImportException($"{where}: no label, no value and no error.type: the event says nothing a result line can hold (OT-6).");
        }

        // traceLink: the trace, and the span when the record names one in that trace.
        var traceId = TraceId(logRecord["traceId"], 32, where);
        var spanId = traceId is null ? null : TraceId(logRecord["spanId"], 16, where);
        return new Event(
            where, name, caseId, state, reason is null ? null : Cut(reason, 4096), value, scoreLabel,
            TimeOf(logRecord["timeUnixNano"], where), traceId, spanId, service);
    }

    // The first max code points of the text, as written ("exact up to that length"); the text itself when shorter.
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;   // at most max UTF-16 units: at most max code points
        }

        var cut = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(max))
        {
            cut.Append(rune.ToString());
        }

        return cut.ToString();
    }

    // timeUnixNano: a decimal string (OTLP/JSON's fixed64), or a JSON integer; 0 or none is unknown.
    private static AefTime? TimeOf(JsonNode? node, string where)
    {
        var text = node switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
            _ => throw new InvalidDataException($"{where}: timeUnixNano is not a number of nanoseconds."),
        };
        if (text is null)
        {
            return null;
        }

        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var nanos))
        {
            throw new InvalidDataException($"{where}: timeUnixNano '{text}' is not an unsigned 64-bit count of nanoseconds.");
        }

        return nanos == 0 ? null : new AefTime((long)(nanos / 1_000_000_000), (int)(nanos % 1_000_000_000));
    }

    // A trace or span id: hex of its length (OTLP/JSON), written in lower case ([RUN-14]); empty or none is no id.
    private static string? TraceId(JsonNode? node, int length, string where)
    {
        if (Text(node) is not { Length: > 0 } id)
        {
            return null;
        }

        return id.Length == length && id.All(char.IsAsciiHexDigit)
            ? id.ToLowerInvariant()
            : throw new InvalidDataException($"{where}: '{id}' is not an id of {length} hex characters.");
    }

    // A record's or resource's attributes, by key (OTLP: keys are unique).
    private static Dictionary<string, JsonObject> Attributes(JsonNode? attributes, string where)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var attribute in Objects(attributes))
        {
            var key = Text(attribute["key"]) ?? throw new InvalidDataException($"{where}: an attribute without a key.");
            if (!map.TryAdd(key, attribute["value"] as JsonObject ?? new JsonObject()))
            {
                throw new InvalidDataException($"{where}: the attribute {key} twice (OTLP: attribute keys are unique).");
            }
        }

        return map;
    }

    private static string StringValue(JsonObject value, string where) =>
        Text(value["stringValue"]) ?? throw new InvalidDataException($"{where} is not a stringValue.");

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
}
