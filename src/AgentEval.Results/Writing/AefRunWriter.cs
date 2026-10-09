// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Writing;

/// <summary>
/// The writer refused to finish something because a verifier found problems in what it wrote: a writer bug, or a run
/// folder changed behind its back. The run is left as it was before the call.
/// </summary>
public sealed class AefWriteException : InvalidOperationException
{
    /// <summary>A failure with the verifier's problems.</summary>
    public AefWriteException(string message, IReadOnlyList<AefProblem> problems)
        : base(message + (problems.Count == 0 ? "" : ": " + string.Join(", ", problems.Select(p => $"{p.Path} {p.Code}"))))
    {
        Problems = problems;
    }

    /// <summary>The problems the verifier reported, in the order of §3.9.</summary>
    public IReadOnlyList<AefProblem> Problems { get; }
}

/// <summary>
/// Writes one AEF 1.0 run folder (contracts/aef/1/spec/03-run.md) as a producer: <see cref="Create"/> starts it
/// (<c>status: running</c>, with run.json, an empty metrics.json and an empty results.ndjson), the producer adds result
/// lines, blobs, evidence, gate decisions, telemetry and its metrics, and <see cref="Close"/> ends it: it writes
/// summary.json (computed by <see cref="AefSummaryWriter"/>) and the closed run.json, and runs the run verifier
/// (<see cref="AefRunVerifier"/>), which must report the run <c>unsealed</c> with no problem. <see cref="AefSealer"/>
/// then seals it.
/// </summary>
/// <remarks>
/// <para>
/// Every document and line is checked against the writer schema before it is written ([VER-2]) and written as
/// <see cref="AefJsonWriter"/> writes JSON: UTF-8 without a byte-order mark, LF only, integers in plain digits, within
/// the limits of [ENC-17]. What one line can break (§3.9's line rules, [RUN-11] and [SEC-6] under
/// <c>contentCapture: off</c>) is refused when the line is added; what needs the whole run (a parent's decisive
/// children, cited evidence, declared metrics, resolving trace links, no <c>pending</c> line) is refused by
/// <see cref="Close"/> before it writes anything, so the run stays open and can be completed.
/// </para>
/// <para>
/// While the run is running its files may be rewritten ([RUN-4]): <see cref="SetMetrics"/> rewrites metrics.json and
/// <see cref="UpdateResult"/> rewrites results.ndjson. After <see cref="Close"/> nothing changes: every call throws.
/// A writer is not safe for use by several threads at once, and assumes no one else writes into its folder.
/// </para>
/// </remarks>
public sealed class AefRunWriter
{
    /// <summary>The AEF version the writer writes ([VER-1], [VER-2]).</summary>
    public const string SchemaVersion = "1.0";

    // summary.json, written at close: the room a producer's files leave in the run's file count ([ENC-17] counts neither
    // seal.json, attestation.dsse.json nor overlays/).
    private const int ReservedFiles = 1;

    private const long MaxNdjsonBytes = AefLimits.MaxNdjsonBytes;

    private const string RunPath = "run.json";
    private const string MetricsPath = "metrics.json";
    private const string SummaryPath = "summary.json";
    private const string ResultsPath = "results.ndjson";
    private const string EvidencePath = "evidence.ndjson";
    private const string GatesPath = "gates.ndjson";
    private const string TracesPath = "traces.otlp.jsonl";
    private const string LogsPath = "logs.otlp.jsonl";

    private readonly AefRunHeader _header;
    private readonly bool _contentOff;
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _results = [];
    private readonly Dictionary<string, int> _resultIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AefResultHandle> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _evidence = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _gates = [];
    private readonly Dictionary<string, long> _blobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _traces = new(StringComparer.Ordinal);
    private readonly HashSet<(string Trace, string Span)> _spans = [];
    private Dictionary<string, AefMetricKind> _metrics = new(StringComparer.Ordinal);
    private AefSummary? _summary;

    private AefRunWriter(string directory, AefRunHeader header)
    {
        Directory = directory;
        _header = header;
        _contentOff = header.ContentCapture == AefContentCapture.Off;
    }

    /// <summary>The run folder.</summary>
    public string Directory { get; }

    /// <summary>The run's <c>runId</c>.</summary>
    public string RunId => _header.RunId;

    /// <summary><see cref="AefRunStatus.Running"/> until <see cref="Close"/> succeeds, then the status it closed with.</summary>
    public AefRunStatus Status { get; private set; } = AefRunStatus.Running;

    /// <summary>
    /// Starts a run in <paramref name="directory"/>, which must not exist or be empty (a run folder holds only its own
    /// files, [RUN-2]): writes run.json (<c>status: running</c>), an empty metrics.json and an empty results.ndjson, so
    /// the folder is a valid open run from the start.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The folder is not empty; or the header is not valid against the writer run schema ([VER-2]), or breaks a rule of
    /// §3.9 about run.json (<c>calibration</c>, <c>execution-policy</c>).
    /// </exception>
    /// <exception cref="IOException">The folder cannot be written.</exception>
    public static AefRunWriter Create(string directory, AefRunHeader header)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(header);
        if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new ArgumentException($"{directory} is not empty: a run folder holds only the run's files ([RUN-2]).", nameof(directory));
        }

        var run = header.ToJson(AefRunStatus.Running, null, null);
        Valid("run", run, "run.json");
        CheckHeader(header);

        var writer = new AefRunWriter(directory, header);
        System.IO.Directory.CreateDirectory(directory);
        writer.WriteFile(RunPath, AefJsonWriter.Document(run));
        writer.WriteFile(MetricsPath, AefJsonWriter.Document(MetricsJson([], null)));
        writer.WriteFile(ResultsPath, []);
        return writer;
    }

    /// <summary>
    /// The <c>resultId</c> a line of this run with <paramref name="caseId"/>, <paramref name="path"/> and
    /// <paramref name="trial"/> has ([RES-4]): for a parent's <c>aggregation.decisive</c> before its children are added.
    /// </summary>
    /// <exception cref="ArgumentException">A control character in the case or the path.</exception>
    public string ResultIdOf(string caseId, string path, int? trial = null) => AefResultId.Compute(RunId, caseId, path, trial);

    /// <summary>
    /// Adds a result line: a root, or, with <paramref name="parent"/>, a child of a line already added ([RES-5]). Its
    /// <c>resultId</c> is computed ([RES-4]) and is unique: no two lines with one case, path and trial.
    /// </summary>
    /// <returns>A handle to the line, for its children.</returns>
    /// <exception cref="ArgumentException">
    /// The line is not valid against the writer result schema; it breaks a rule of §3.9 about one line
    /// (<c>aggregation</c> counts, <c>annotator</c>, <c>trials</c>, <c>attack</c>, <c>result-times</c>,
    /// <c>interval</c>, a metric scored twice); it has the id of a line already added; <paramref name="parent"/> is no
    /// line of this run; a child has no <c>component</c> ([RES-5]); a line under a trial's line does not carry its
    /// <c>trial</c> ([RES-8]); its reasoning is no blob of this run; or the run
    /// keeps no content and the line has reasoning or a prompt hash ([RUN-11]).
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public AefResultHandle AddResult(AefResult result, AefResultHandle? parent = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureRunning();
        if (parent is not null && (!ReferenceEquals(parent.Writer, this) || !_resultIndex.ContainsKey(parent.ResultId)))
        {
            throw new ArgumentException($"The parent {parent.ResultId} is no line of this run: a parentResultId names a line of the run (§3.9 parent).", nameof(parent));
        }

        if (parent?.Trial is { } trial && result.Trial != trial)
        {
            throw new ArgumentException($"A line under trial {trial} carries that trial: a trial's tree is the trial's ([RES-8], §3.9 trials).", nameof(result));
        }

        var resultId = ResultIdOf(result.CaseId, result.Path, result.Trial);
        if (_resultIndex.ContainsKey(resultId))
        {
            throw new ArgumentException(
                $"A line with case '{result.CaseId}', path '{result.Path}' and trial {result.Trial?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"} was already added: its resultId {resultId} is taken ([RES-4], §3.9 result-id).",
                nameof(result));
        }

        var (json, line) = ResultLine(result, resultId, parent?.ResultId);
        Count(ResultsPath, line);
        AppendLine(ResultsPath, line);

        _resultIndex[resultId] = _results.Count;
        _results.Add(json);
        var handle = new AefResultHandle(this, resultId, result.CaseId, result.Path, result.Trial, parent?.ResultId);
        _handles[resultId] = handle;
        if (parent is not null)
        {
            if (!_children.TryGetValue(parent.ResultId, out var children))
            {
                _children[parent.ResultId] = children = [];
            }

            children.Add(resultId);
        }

        return handle;
    }

    /// <summary>
    /// Replaces a line already added (while the run is running its files may be rewritten, [RUN-4]): a
    /// <c>pending</c> line given its final state, a composite given its aggregation once its children are known. The
    /// line keeps its case, path, trial and parent, so its id; results.ndjson is rewritten.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="handle"/> is no line of this run; the case, path or trial differ from the line's; or the line
    /// breaks what <see cref="AddResult"/> refuses.
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void UpdateResult(AefResultHandle handle, AefResult result)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(result);
        EnsureRunning();
        if (!ReferenceEquals(handle.Writer, this) || !_resultIndex.TryGetValue(handle.ResultId, out var index))
        {
            throw new ArgumentException($"{handle.ResultId} is no line of this run.", nameof(handle));
        }

        if (result.CaseId != handle.CaseId || result.Path != handle.Path || result.Trial != handle.Trial)
        {
            throw new ArgumentException("An updated line keeps its case, path and trial: they make its resultId ([RES-4]).", nameof(result));
        }

        var (json, _) = ResultLine(result, handle.ResultId, handle.ParentResultId);
        var lines = _results.Select((line, i) => AefJsonWriter.Line(i == index ? json : line)).ToList();
        var size = lines.Sum(l => (long)l.Length);
        if (size > MaxNdjsonBytes)
        {
            throw new InvalidOperationException($"{ResultsPath} would hold {size} bytes, above the {MaxNdjsonBytes} [ENC-17] allows an NDJSON file.");
        }

        _results[index] = json;
        WriteFile(ResultsPath, [.. lines.SelectMany(l => l)]);
        _sizes[ResultsPath] = size;
    }

    /// <summary>
    /// Stores a blob ([EVD-3]): the bytes at <c>blobs/sha256/&lt;ab&gt;/&lt;hex&gt;</c>, named by their SHA-256. The same
    /// bytes put twice are one blob.
    /// </summary>
    /// <exception cref="ArgumentException">More than 1 GiB ([ENC-17]).</exception>
    /// <exception cref="InvalidOperationException">The run is closed, or holds as many files as [ENC-17] allows.</exception>
    public AefBlob PutBlob(ReadOnlySpan<byte> bytes)
    {
        EnsureRunning();
        if (bytes.Length > AefLimits.MaxBlobBytes)
        {
            throw new ArgumentException($"A blob is at most {AefLimits.MaxBlobBytes} bytes ([ENC-17]).", nameof(bytes));
        }

        var blob = new AefBlob(AefWire.Sha256(bytes), bytes.Length);
        if (!_blobs.ContainsKey(blob.Sha256))
        {
            ReserveFile(blob.Path);
            WriteFile(blob.Path, bytes);
            _blobs[blob.Sha256] = blob.Size;
        }

        return blob;
    }

    /// <summary>Stores a blob read from <paramref name="content"/> to its end (<see cref="PutBlob(ReadOnlySpan{byte})"/>), hashed as it is copied.</summary>
    /// <exception cref="ArgumentException">More than 1 GiB ([ENC-17]).</exception>
    /// <exception cref="InvalidOperationException">The run is closed, or holds as many files as [ENC-17] allows.</exception>
    /// <exception cref="IOException">The stream or the folder cannot be read or written.</exception>
    public AefBlob PutBlob(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureRunning();
        var temporary = Path.Combine(Path.GetTempPath(), $"aef-blob-{Guid.NewGuid():N}");
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += read;
                    if (size > AefLimits.MaxBlobBytes)
                    {
                        throw new ArgumentException($"A blob is at most {AefLimits.MaxBlobBytes} bytes ([ENC-17]).", nameof(content));
                    }

                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
            }

            var blob = new AefBlob(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
            if (!_blobs.ContainsKey(blob.Sha256))
            {
                ReserveFile(blob.Path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Full(blob.Path))!);
                File.Move(temporary, Full(blob.Path));
                _paths.Add(blob.Path);
                _blobs[blob.Sha256] = blob.Size;
            }

            return blob;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Adds an evidence record (evidence.ndjson, [EVD-1], [EVD-2]).</summary>
    /// <exception cref="ArgumentException">
    /// Not valid against the writer evidence schema; an <c>evidenceId</c> already added (§3.9 <c>evidence-id</c>); a
    /// blob link to a blob not in this run; or a content kind in a run that keeps no content ([RUN-11]).
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void AddEvidence(AefEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureRunning();
        if (_evidence.ContainsKey(evidence.EvidenceId))
        {
            throw new ArgumentException($"The evidence id {evidence.EvidenceId} was already added: an evidence id is unique in the run ([EVD-1], §3.9 evidence-id).", nameof(evidence));
        }

        var kind = AefWire.Name(evidence.Kind);
        if (_contentOff && CrossFileRules.ContentEvidenceKinds.Contains(kind))
        {
            throw new ArgumentException($"Evidence of kind {kind} holds content: a run with contentCapture off writes none ([RUN-11]).", nameof(evidence));
        }

        if (evidence.Link.Blob is { } blob)
        {
            RequireBlob(blob, "the evidence link");
        }

        var json = evidence.ToJson();
        Valid("evidence", json, $"evidence {evidence.EvidenceId}");
        var line = AefJsonWriter.Line(json);
        Count(EvidencePath, line);
        AppendLine(EvidencePath, line);
        _evidence[evidence.EvidenceId] = json;
    }

    /// <summary>Adds a gate decision (gates.ndjson, [GATE-1], [GATE-2]). The results it names are checked by <see cref="Close"/>.</summary>
    /// <exception cref="ArgumentException">Not valid against the writer gate-decision schema (<c>ship</c> on an incomparable comparison among them).</exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void AddGate(AefGateDecision gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        EnsureRunning();
        var json = gate.ToJson();
        Valid("gate-decision", json, $"gate decision {gate.DecisionId}");
        var line = AefJsonWriter.Line(json);
        Count(GatesPath, line);
        AppendLine(GatesPath, line);
        _gates.Add(json);
    }

    /// <summary>Writes metrics.json ([SUM-1]), replacing the metrics set before (the run is still running, [RUN-4]).</summary>
    /// <exception cref="ArgumentException">Not valid against the writer metrics schema, a metric declared twice, or a scale whose min exceeds its max (§3.9 <c>metric</c>).</exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void SetMetrics(IEnumerable<AefMetric> metrics, JsonObject? ext = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        EnsureRunning();
        var list = metrics.ToList();
        var kinds = new Dictionary<string, AefMetricKind>(StringComparer.Ordinal);
        foreach (var metric in list)
        {
            if (!kinds.TryAdd(metric.Id, metric.Kind))
            {
                throw new ArgumentException($"The metric {metric.Id} is declared twice: a metric's id is unique ([SUM-1], §3.9 metric).", nameof(metrics));
            }

            if (metric.Scale.Min > metric.Scale.Max)
            {
                throw new ArgumentException($"The metric {metric.Id} has a scale whose min exceeds its max ([SUM-1], §3.9 metric).", nameof(metrics));
            }
        }

        var json = MetricsJson(list, ext);
        Valid("metrics", json, "metrics.json");
        WriteFile(MetricsPath, AefJsonWriter.Document(json));
        _metrics = kinds;
    }

    /// <summary>
    /// Says what summary.json summarises: its lanes, the entries of each, the run's cost and usage. It is written by
    /// <see cref="Close"/>, which computes each entry's figures. Without it, summary.json has no lane.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A lane named twice; two entries with one lane, metric and path, or two usage entries with one role and model
    /// ([SUM-9], §3.9 <c>summary-duplicate</c>); or a value the writer summary schema refuses.
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void SetSummary(AefSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        EnsureRunning();
        AefSummaryWriter.Check(summary);
        _summary = summary;
    }

    /// <summary>
    /// Adds an OpenTelemetry <c>TracesData</c> object as a line of traces.otlp.jsonl ([RUN-14]): OTLP/JSON 1.x
    /// (<c>resourceSpans[].scopeSpans[].spans[]</c>), lower-case hex ids.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Spans under the pre-1.0 <c>instrumentationLibrarySpans</c>, which readers of OTLP/JSON 1.x do not see; a span
    /// id or trace id that is not lower-case hex of its length ([RUN-14]); or, in a run that keeps no content, a span,
    /// span event, resource or scope carrying an attribute [SEC-6] names.
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void AddTraces(JsonObject tracesData)
    {
        ArgumentNullException.ThrowIfNull(tracesData);
        EnsureRunning();
        if (AefNode.Objects(tracesData["resourceSpans"]).Any(r => r.ContainsKey("instrumentationLibrarySpans")))
        {
            throw new ArgumentException("Spans under instrumentationLibrarySpans (OTLP before 1.0): a reader sees only scopeSpans ([RUN-14]).", nameof(tracesData));
        }

        if (_contentOff && CrossFileRules.Otlp.ResourcesAndScopes(tracesData, "resourceSpans", "scopeSpans").Any(CrossFileRules.Otlp.CarriesContent))
        {
            throw new ArgumentException("A resource or scope carries content: a run with contentCapture off writes none of the attributes [SEC-6] names.", nameof(tracesData));
        }

        var spans = CrossFileRules.Otlp.Spans(tracesData).ToList();
        var ids = new List<(string, string)>();
        foreach (var span in spans)
        {
            var (traceId, spanId) = (AefNode.String(span["traceId"]), AefNode.String(span["spanId"]));
            if (!IsLowerHex(traceId, 32) || !IsLowerHex(spanId, 16))
            {
                throw new ArgumentException("A span's traceId is 32 and its spanId 16 lower-case hex characters (a writer writes lower case, [RUN-14]).", nameof(tracesData));
            }

            if (_contentOff && (CrossFileRules.Otlp.CarriesContent(span) || AefNode.Objects(span["events"]).Any(CrossFileRules.Otlp.CarriesContent)))
            {
                throw new ArgumentException("A span carries content: a run with contentCapture off writes none of the attributes [SEC-6] names.", nameof(tracesData));
            }

            ids.Add((traceId!, spanId!));
        }

        var line = AefJsonWriter.Line(tracesData);
        Count(TracesPath, line);
        AppendLine(TracesPath, line);
        foreach (var (traceId, spanId) in ids)
        {
            _traces.Add(traceId);
            _spans.Add((traceId, spanId));
        }
    }

    /// <summary>
    /// Adds an OpenTelemetry <c>LogsData</c> object as a line of logs.otlp.jsonl ([RUN-14]): OTLP/JSON 1.x
    /// (<c>resourceLogs[].scopeLogs[].logRecords[]</c>), such as <c>gen_ai.evaluation.result</c> events.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Records under the pre-1.0 <c>instrumentationLibraryLogs</c>; or, in a run that keeps no content, a log record
    /// with a <c>body</c>, or a log record, resource or scope with an attribute [SEC-6] names.
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed ([RUN-4]).</exception>
    public void AddLogs(JsonObject logsData)
    {
        ArgumentNullException.ThrowIfNull(logsData);
        EnsureRunning();
        if (AefNode.Objects(logsData["resourceLogs"]).Any(r => r.ContainsKey("instrumentationLibraryLogs")))
        {
            throw new ArgumentException("Log records under instrumentationLibraryLogs (OTLP before 1.0): a reader sees only scopeLogs ([RUN-14]).", nameof(logsData));
        }

        if (_contentOff && (CrossFileRules.Otlp.LogRecords(logsData).Any(r => r.ContainsKey("body") || CrossFileRules.Otlp.CarriesContent(r))
                            || CrossFileRules.Otlp.ResourcesAndScopes(logsData, "resourceLogs", "scopeLogs").Any(CrossFileRules.Otlp.CarriesContent)))
        {
            throw new ArgumentException("A log record carries content: a run with contentCapture off writes no body and none of the attributes [SEC-6] names (on records, resources or scopes).", nameof(logsData));
        }

        var line = AefJsonWriter.Line(logsData);
        Count(LogsPath, line);
        AppendLine(LogsPath, line);
    }

    /// <summary>
    /// Writes one of the producer's own files, <c>ext/&lt;path&gt;</c> ([RUN-2]), replacing it if it was written before
    /// (the run is still running, [RUN-4]).
    /// </summary>
    /// <param name="path">The path under <c>ext/</c>, <c>/</c> between segments.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <exception cref="ArgumentException">
    /// <c>ext/</c> and the path break [RUN-3]: a segment that is empty, holds a character other than ASCII letters,
    /// digits, <c>.</c>, <c>_</c> and <c>-</c>, starts or ends with <c>.</c>, or is a name Windows reserves; more than
    /// 255 bytes; or a path or folder that differs from another of the run only in letter case.
    /// </exception>
    /// <exception cref="InvalidOperationException">The run is closed, or holds as many files as [ENC-17] allows.</exception>
    public void PutExtFile(string path, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsureRunning();
        var full = "ext/" + path;
        if (!_paths.Contains(full))
        {
            if (!AefPaths.IsValid(full) || AefPaths.Check([.. _paths, full]).Count > 0)
            {
                throw new ArgumentException($"{full} is not a path a run folder may hold ([RUN-3]).", nameof(path));
            }

            ReserveFile(full);
        }

        WriteFile(full, bytes);
    }

    /// <summary>
    /// Closes the run ([RUN-4], [RUN-5]): checks the rules that need the whole run, writes summary.json (each entry's
    /// figures computed by <see cref="AefSummaryCalculator"/>, [SUM-3]–[SUM-8]) and run.json with
    /// <paramref name="status"/> and <paramref name="endedAt"/>, and verifies the folder. From then on nothing changes.
    /// </summary>
    /// <param name="status"><see cref="AefRunStatus.Completed"/> or <see cref="AefRunStatus.Aborted"/>.</param>
    /// <param name="endedAt">When the run ended: not before its start.</param>
    /// <param name="abortReason">Why it was aborted: required for an aborted run, and only for one.</param>
    /// <returns>The run verifier's report: <c>unsealed</c>, no problem.</returns>
    /// <exception cref="ArgumentException">A status that is not a closed one, an end before the start, or an abort reason missing or out of place ([RUN-5]).</exception>
    /// <exception cref="InvalidOperationException">
    /// The run is already closed, or a rule that needs the whole run is broken: a <c>pending</c> line ([RES-3]: the
    /// producer updates it to <c>skipped</c> or <c>error</c>); a node with children and no aggregation ([RES-5]); a
    /// <c>total</c> that is not the number of children, or a <c>decisive</c> id that is not a child ([RES-6]); a case
    /// run in trials with no rollup line, a rollup whose <c>n</c> and <c>passed</c> are not what its trial lines
    /// give, or a rollup at a child path that is not a child of its case's rollup at the parent path ([RES-8]); a cited
    /// evidence id that was not added; a
    /// metric no declaration names; a gate decision naming a result that is no line; a trace link that names no span
    /// of traces.otlp.jsonl; a summary entry whose aggregate is the producer's but has no value. Nothing was written:
    /// the run is still running.
    /// </exception>
    /// <exception cref="AefWriteException">The verifier found a problem; the run is left running, as before the call.</exception>
    public AefRunVerification Close(AefRunStatus status, DateTimeOffset endedAt, string? abortReason = null)
    {
        EnsureRunning();
        if (status is not (AefRunStatus.Completed or AefRunStatus.Aborted))
        {
            throw new ArgumentException("A run closes as completed or aborted ([RUN-5]).", nameof(status));
        }

        if ((status == AefRunStatus.Aborted) != (abortReason is not null))
        {
            throw new ArgumentException("An aborted run has an abortReason, and only an aborted run has one ([RUN-5]).", nameof(abortReason));
        }

        if (endedAt < _header.StartedAt)
        {
            throw new ArgumentException("endedAt is not earlier than startedAt ([RUN-5]).", nameof(endedAt));
        }

        CheckHeader(_header);   // again: the header's lists and objects are the caller's, and may have changed since Create
        CheckWholeRun();
        var summary = AefSummaryWriter.Build(RunId, _results, _metrics, _summary ?? new AefSummary { Lanes = [] });
        var closed = _header.ToJson(status, AefWire.Time(endedAt), abortReason);
        Valid("run", closed, "run.json");
        var (summaryBytes, closedBytes) = (AefJsonWriter.Document(summary), AefJsonWriter.Document(closed));

        WriteFile(SummaryPath, summaryBytes);
        WriteFile(RunPath, closedBytes);
        var verification = AefRunVerifier.Verify(Directory);
        if (verification.Outcome != AefOutcome.Unsealed || verification.Problems.Count > 0)
        {
            // Back to the open run this was before the call.
            WriteFile(RunPath, AefJsonWriter.Document(_header.ToJson(AefRunStatus.Running, null, null)));
            File.Delete(Full(SummaryPath));
            _paths.Remove(SummaryPath);
            throw new AefWriteException($"The closed run does not verify ({AefRunVerification.Name(verification.Outcome)})", verification.Problems);
        }

        Status = status;
        return verification;
    }

    // ---------------------------------------------------------------- checks

    // The rules of §3.9 about run.json that the schema cannot check: calibration and execution-policy.
    private static void CheckHeader(AefRunHeader header)
    {
        foreach (var judge in header.Judges ?? [])
        {
            if (judge.Calibration is { } calibration
                && (calibration.DangerousErrors > calibration.N || calibration.MeasuredAt > header.StartedAt))
            {
                throw new ArgumentException(
                    $"Judge {judge.Model}: a calibration has no more dangerous errors than cases, and was measured before the run started ([RUN-9], §3.9 calibration).",
                    nameof(header));
            }
        }

        if (header.Suite?.ExecutionPolicy is { } policy && policy.RequirePasses > policy.TrialsPerCase)
        {
            throw new ArgumentException("requirePasses is not above trialsPerCase (§3.9 execution-policy).", nameof(header));
        }
    }

    // A line checked and serialised: the rules of §3.9 one line can break, [RES-5]'s component, [RUN-11], the writer
    // schema, and [ENC-17]'s size and depth.
    private (JsonObject Json, byte[] Line) ResultLine(AefResult result, string resultId, string? parentResultId)
    {
        var where = $"result {result.CaseId}/{result.Path}";
        if (parentResultId is not null && result.Component is null)
        {
            throw new ArgumentException($"{where}: a child of a composite has a component ([RES-5]).", nameof(result));
        }

        if (result.Reasoning is { } reasoning)
        {
            if (_contentOff)
            {
                throw new ArgumentException($"{where}: a run with contentCapture off keeps no reasoning ([RUN-11]).", nameof(result));
            }

            RequireBlob(reasoning, $"{where}: the reasoning");
        }

        if (_contentOff && result.Annotator?.PromptHash is not null)
        {
            throw new ArgumentException($"{where}: a run with contentCapture off keeps no digest of a prompt ([RUN-11]).", nameof(result));
        }

        if (result.Aggregation is { } aggregation
            && (aggregation.Measured > aggregation.Total || (aggregation.Unmeasured?.Total ?? 0) != aggregation.Total - aggregation.Measured))
        {
            throw new ArgumentException(
                $"{where}: measured is at most total, and the unmeasured counts add up to total − measured ([RES-6], §3.9 aggregation).", nameof(result));
        }

        if (result.Annotator?.Panel is { } panel && panel.Agree > panel.Of)
        {
            throw new ArgumentException($"{where}: a panel's agree does not exceed its of ([RES-10], §3.9 annotator).", nameof(result));
        }

        if (result.Trials is { } trials && trials.Passed > trials.N)
        {
            throw new ArgumentException($"{where}: a rollup's passed does not exceed its n ([RES-8], §3.9 trials).", nameof(result));
        }

        if (result.Attack?.Success == true && result.State == AefState.Passed)
        {
            throw new ArgumentException($"{where}: an attack that succeeded is not a pass (§3.9 attack).", nameof(result));
        }

        if (result.EndedAt < result.StartedAt)
        {
            throw new ArgumentException($"{where}: endedAt is not before startedAt ([RES-10], §3.9 result-times).", nameof(result));
        }

        if (result.Usage is { } usage && usage.Select(u => (u.Role, u.Model)).Distinct().Count() != usage.Count)
        {
            throw new ArgumentException($"{where}: usage names each role and model once ([RES-10], §3.9 result-times).", nameof(result));
        }

        if (result.Uncertainty?.Ci is { } ci && ci.Low > ci.High)
        {
            throw new ArgumentException($"{where}: an interval's low does not exceed its high (§3.9 interval).", nameof(result));
        }

        if (result.Scores is { } scores && scores.Select(s => s.Metric).Distinct(StringComparer.Ordinal).Count() != scores.Count)
        {
            throw new ArgumentException($"{where}: a line scores each metric once (§3.9 metric).", nameof(result));
        }

        var json = result.ToJson(resultId, parentResultId);
        Valid("result", json, where);
        return (json, AefJsonWriter.Line(json));
    }

    // The rules that need the whole run, checked before Close writes anything.
    private void CheckWholeRun()
    {
        var problems = new List<string>();
        var metrics = _metrics;
        var trialCases = new Dictionary<(string Case, string Path), (int Count, int Passed)>();
        var trialStates = new Dictionary<(string Case, string Path), HashSet<string>>();
        var rollups = new Dictionary<(string Case, string Path), (double N, double Passed, bool Agree)>();
        var rollupLines = new Dictionary<(string Case, string Path), (string Id, string? Parent)>();
        foreach (var line in _results)
        {
            var id = AefNode.String(line["resultId"])!;
            var where = $"result {AefNode.String(line["caseId"])}/{AefNode.String(line["path"])} ({id})";
            if (AefNode.String(line["state"]) == "pending")
            {
                problems.Add($"{where} is pending: a closed run has no pending line ([RES-3])");
            }

            var children = _children.GetValueOrDefault(id) ?? [];
            if (children.Count > 0 && !line.ContainsKey("aggregation"))
            {
                problems.Add($"{where} has children and no aggregation: a composite node carries one ([RES-5])");
            }

            if (AefNode.Strings(AefNode.At(line, "aggregation", "decisive")).Any(d => !children.Contains(d, StringComparer.Ordinal)))
            {
                problems.Add($"{where}: a decisive id is not a child of the node ([RES-6], §3.9 aggregation)");
            }

            if (AefNode.Number(AefNode.At(line, "aggregation", "total")) is { } total && total != children.Count)
            {
                problems.Add($"{where}: its aggregation's total is {total}, and it has {children.Count} children ([RES-6], §3.9 aggregation)");
            }

            if (AefNode.Strings(line["evidence"]).FirstOrDefault(e => !_evidence.ContainsKey(e)) is { } missing)
            {
                problems.Add($"{where} cites {missing}, which no evidence record has (§3.9 evidence)");
            }

            if (AefNode.Objects(line["scores"]).Select(s => AefNode.String(s["metric"])!).FirstOrDefault(m => !metrics.ContainsKey(m)) is { } undeclared)
            {
                problems.Add($"{where} scores {undeclared}, which metrics.json does not declare ([SUM-1], §3.9 metric)");
            }

            if (line["traceLink"] is JsonObject link && !Resolves(AefNode.String(link["traceId"])!, AefNode.String(link["spanId"])))
            {
                problems.Add($"{where}: its traceLink names no span of traces.otlp.jsonl (§3.9 trace-link)");
            }

            var key = (AefNode.String(line["caseId"])!, AefNode.String(line["path"])!);
            if (line.ContainsKey("trial"))
            {
                var (count, passed) = trialCases.GetValueOrDefault(key);
                trialCases[key] = (count + 1, passed + (AefNode.String(line["state"]) == "passed" ? 1 : 0));
                if (!trialStates.TryGetValue(key, out var states))
                {
                    trialStates[key] = states = new HashSet<string>(StringComparer.Ordinal);
                }

                states.Add(AefNode.String(line["state"]) ?? "");
            }
            else if (line["trials"] is JsonObject trials)
            {
                rollups[key] = (AefNode.Number(trials["n"]) ?? 0, AefNode.Number(trials["passed"]) ?? 0, AefNode.IsTrue(trials["agree"]));
                rollupLines.TryAdd(key, (id, AefNode.String(line["parentResultId"])));
            }
        }

        // [RES-8]: the rollups of a composite case run in trials form the case's own tree: the rollup at a child path has
        // its case's rollup at the parent path (the path without its last '/' segment) as its parent, when there is one.
        foreach (var ((caseId, path), (_, parent)) in rollupLines)
        {
            if (path.LastIndexOf('/') is var slash and >= 0 && rollupLines.TryGetValue((caseId, path[..slash]), out var above)
                && !string.Equals(parent, above.Id, StringComparison.Ordinal))
            {
                problems.Add($"case {caseId}: its rollup at {path} is not a child of its rollup at {path[..slash]} ([RES-8], §3.9 trials)");
            }
        }

        foreach (var ((caseId, path), (count, passed)) in trialCases)
        {
            if (!rollups.TryGetValue((caseId, path), out var rollup))
            {
                problems.Add($"case {caseId} ran in trials at {path} and has no rollup line ([RES-8])");
            }
            else if (rollup.N != count || rollup.Passed != passed)
            {
                problems.Add($"case {caseId} at {path}: its rollup says {rollup.N} trials, {rollup.Passed} passed; its trial lines give {count}, {passed} passed ([RES-8], §3.9 trials)");
            }
            else if (rollup.Agree != (trialStates[(caseId, path)].Count == 1))
            {
                problems.Add($"case {caseId} at {path}: its rollup says agree {(rollup.Agree ? "true" : "false")}; its trial lines are in {trialStates[(caseId, path)].Count} states (agree is true exactly when they are all in one, [RES-8], §3.9 trials)");
            }
        }

        foreach (var (id, record) in _evidence)
        {
            if (AefNode.String(AefNode.At(record, "link", "traceId")) is { } traceId && !Resolves(traceId, AefNode.String(AefNode.At(record, "link", "spanId"))))
            {
                problems.Add($"evidence {id}: its span names no span of traces.otlp.jsonl (§3.9 trace-link)");
            }
        }

        foreach (var gate in _gates)
        {
            var named = AefNode.Strings(AefNode.At(gate, "inputs", "results")).Concat(AefNode.Strings(gate["decisive"]));
            if (named.FirstOrDefault(r => !_resultIndex.ContainsKey(r)) is { } unknown)
            {
                problems.Add($"gate decision {AefNode.String(gate["decisionId"])} names {unknown}, which is no line of the run (§3.9 gate)");
            }
        }

        foreach (var lane in _summary?.Lanes ?? [])
        {
            foreach (var entry in lane.Entries.Where(e => !metrics.ContainsKey(e.Metric)))
            {
                problems.Add($"summary lane {lane.Lane}: metric {entry.Metric} is not declared in metrics.json ([SUM-1], §3.9 metric)");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The run cannot close: " + string.Join("; ", problems) + ".");
        }
    }

    private static JsonObject MetricsJson(IReadOnlyList<AefMetric> metrics, JsonObject? ext)
    {
        var json = new JsonObject { ["schemaVersion"] = SchemaVersion, ["metrics"] = AefWire.Array(metrics, m => m.ToJson()) };
        json.Put("ext", AefWire.Ext(ext));
        return json;
    }

    // A span link or traceLink resolves when traces.otlp.jsonl is absent (it points outside the run), or names a span
    // (or, with no span id, a trace) of it.
    private bool Resolves(string traceId, string? spanId) =>
        !_lines.ContainsKey(TracesPath) || (spanId is null ? _traces.Contains(traceId) : _spans.Contains((traceId, spanId)));

    private void RequireBlob(AefBlob blob, string what)
    {
        if (!_blobs.TryGetValue(blob.Sha256, out var size) || size != blob.Size)
        {
            throw new ArgumentException($"{what} names blob {blob.Sha256} ({blob.Size} bytes), which is not a blob of this run ([EVD-3], §3.9 blob).");
        }
    }

    private static void Valid(string schema, JsonObject json, string what)
    {
        if (AefSchemas.Writer.Validate(schema, json) is { } failure)
        {
            throw new ArgumentException($"{what} is not valid against the writer schema '{schema}' ([VER-2]): {failure}.");
        }
    }

    private static bool IsLowerHex(string? text, int length) =>
        text is not null && text.Length == length && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private void EnsureRunning()
    {
        if (Status != AefRunStatus.Running)
        {
            throw new InvalidOperationException($"The run {RunId} is closed: a closed run never changes ([RUN-4]); what comes later is an overlay (§4.2).");
        }
    }

    // ---------------------------------------------------------------- files

    // One more line of an NDJSON file, within [ENC-17]'s line count and file size.
    private void Count(string path, byte[] line)
    {
        var lines = _lines.GetValueOrDefault(path);
        if (lines >= AefLimits.MaxLines)
        {
            throw new InvalidOperationException($"{path} holds {AefLimits.MaxLines} lines, the most [ENC-17] allows.");
        }

        var size = _sizes.GetValueOrDefault(path) + line.Length;
        if (size > MaxNdjsonBytes)
        {
            throw new InvalidOperationException($"{path} would hold {size} bytes, above the {MaxNdjsonBytes} [ENC-17] allows an NDJSON file.");
        }

        if (lines == 0 && path != ResultsPath && !_paths.Contains(path))
        {
            ReserveFile(path);
        }

        _lines[path] = lines + 1;
        _sizes[path] = size;
    }

    // A new file, within [ENC-17]'s file count (room is left for summary.json; the seal and overlays are not counted).
    private void ReserveFile(string path)
    {
        if (_paths.Count + 1 > AefLimits.MaxFiles - ReservedFiles)
        {
            throw new InvalidOperationException($"The run holds {_paths.Count} files: with summary.json it would exceed the {AefLimits.MaxFiles} [ENC-17] allows.");
        }
    }

    private void WriteFile(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Full(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
        }

        _paths.Add(path);
    }

    private void AppendLine(string path, byte[] line)
    {
        using (var stream = new FileStream(Full(path), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(line);
        }

        _paths.Add(path);
    }

    private string Full(string path) => Path.Combine(Directory, path.Replace('/', Path.DirectorySeparatorChar));
}
