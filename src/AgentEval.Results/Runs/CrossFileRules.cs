// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Runs;

/// <summary>
/// The rules across files of a run (contracts/aef/1/spec/03-run.md, §3.9), from <c>result-id</c> down the table:
/// what a schema, which checks one document, cannot. They are checked only when every file and line of the run was
/// read and is valid against its reader schema (<see cref="AefRunDocuments.AllRead"/>): they would otherwise be checked
/// against data that was not read. [SEC-6] (spec 08) is checked with them, as <c>content-capture</c>.
/// </summary>
public static class CrossFileRules
{
    /// <summary>
    /// [RUN-11]: the evidence kinds that hold content, which a run with <c>contentCapture</c> <c>off</c> never writes.
    /// </summary>
    public static IReadOnlySet<string> ContentEvidenceKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "judge_reasoning", "tool_call", "document", "input", "expected", "output", "transcript" };

    /// <summary>
    /// [SEC-6]: the OpenTelemetry GenAI attributes that carry content (prompts, completions, tool arguments and results,
    /// a judge's reasoning), the deprecated <c>gen_ai.prompt</c> and <c>gen_ai.completion</c> included.
    /// </summary>
    public static IReadOnlySet<string> ContentAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions", "gen_ai.tool.call.arguments",
        "gen_ai.tool.call.result", "gen_ai.evaluation.explanation", "gen_ai.prompt", "gen_ai.completion",
    };

    // [RUN-2]'s files at the top of the folder (blobs, ext/ and overlays/ are matched by their own rules).
    private static readonly HashSet<string> ListedFiles = new(StringComparer.Ordinal)
    {
        "run.json", "results.ndjson", "metrics.json", "summary.json", "evidence.ndjson", "gates.ndjson",
        "traces.otlp.jsonl", "logs.otlp.jsonl", "seal.json", "attestation.dsse.json",
    };

    /// <summary>
    /// Whether [RUN-2] lists <paramref name="path"/>: one of its files, a blob at the path [EVD-3] gives, or a file under
    /// <c>ext/</c> or <c>overlays/</c> (whose files §4.2 checks).
    /// </summary>
    public static bool ListedByRun2(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ListedFiles.Contains(path) || AefRunFolder.IsBlobPath(path, out _)
               || path.StartsWith("ext/", StringComparison.Ordinal) || path.StartsWith("overlays/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The problems of §3.9 from <c>result-id</c> down, unordered.
    /// </summary>
    /// <param name="folder">The run folder.</param>
    /// <param name="documents">Its documents, all read (<see cref="AefRunDocuments.AllRead"/>).</param>
    /// <param name="sealedPaths">
    /// The paths the run's seal lists (the subjects of a <c>seal.json</c> valid against the reader schema), for
    /// <c>unexpected-file</c>; empty when the run has no such seal.
    /// </param>
    /// <param name="withheld">
    /// The blobs (SHA-256, lower-case hex) that authorized redactions withhold ([OVL-10]): a line that references one
    /// that is gone has no <c>blob</c> problem. §3.9 exempts them only in a sealed run, so for a run without
    /// <c>seal.json</c> the caller passes none (<see cref="Integrity.AefRunVerifier"/> does).
    /// </param>
    /// <exception cref="ArgumentException">The documents were not all read.</exception>
    /// <exception cref="IOException">A blob cannot be read.</exception>
    public static IReadOnlyCollection<AefProblem> Check(
        AefRunFolder folder, AefRunDocuments documents, IReadOnlySet<string> sealedPaths, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(sealedPaths);
        ArgumentNullException.ThrowIfNull(withheld);
        if (!documents.AllRead)
        {
            throw new ArgumentException("§3.9's rules from result-id down are checked only on a run whose files all read.", nameof(documents));
        }

        var check = new Checker(folder, documents, withheld);
        check.Metrics();
        check.Evidence();
        check.Results();
        check.Blobs();
        check.UnexpectedFiles(sealedPaths);
        check.Summary();
        check.Gates();
        check.Run();
        check.Telemetry();
        return check.Problems;
    }

    private sealed class Checker
    {
        private readonly AefRunFolder _folder;
        private readonly AefRunDocuments _documents;
        private readonly IReadOnlySet<string> _withheld;
        private readonly JsonObject _run;
        private readonly string _runId;
        private readonly bool _contentOff;
        private readonly Dictionary<string, JsonObject> _metrics = new(StringComparer.Ordinal);
        private readonly HashSet<string> _resultIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evidenceIds = new(StringComparer.Ordinal);
        private readonly (HashSet<string> Traces, HashSet<(string Trace, string Span)> Spans)? _spans;

        public Checker(AefRunFolder folder, AefRunDocuments documents, IReadOnlySet<string> withheld)
        {
            _folder = folder;
            _documents = documents;
            _withheld = withheld;
            _run = documents.Run!;
            _runId = documents.RunId!;

            // [RUN-11]: an absent contentCapture, or one this version does not know, reads as "on" (§7.3).
            _contentOff = AefNode.String(_run["contentCapture"]) == "off";
            foreach (var metric in AefNode.Objects(documents.Metrics!["metrics"]))
            {
                // A metric declared twice is a metric problem; the first declaration is the one rules read.
                _metrics.TryAdd(AefNode.String(metric["id"]) ?? "", metric);
            }

            foreach (var (_, line) in documents.Results.Objects)
            {
                _resultIds.Add(AefNode.String(line["resultId"]) ?? "");
            }

            foreach (var (_, record) in documents.Evidence.Objects)
            {
                _evidenceIds.Add(AefNode.String(record["evidenceId"]) ?? "");
            }

            if (documents.Traces.Present)
            {
                var traces = new HashSet<string>(StringComparer.Ordinal);
                var spans = new HashSet<(string, string)>();
                foreach (var (_, data) in documents.Traces.Objects)
                {
                    foreach (var span in Otlp.Spans(data))
                    {
                        // Ids compare without regard to letter case (§3.9 trace-link).
                        var trace = (AefNode.String(span["traceId"]) ?? "").ToLowerInvariant();
                        traces.Add(trace);
                        spans.Add((trace, (AefNode.String(span["spanId"]) ?? "").ToLowerInvariant()));
                    }
                }

                _spans = (traces, spans);
            }
        }

        public HashSet<AefProblem> Problems { get; } = [];

        // metrics.json: a metric declared twice, or a scale whose min exceeds its max.
        public void Metrics()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var metric in AefNode.Objects(_documents.Metrics!["metrics"]))
            {
                var scale = metric["scale"];
                if (!seen.Add(AefNode.String(metric["id"]) ?? "")
                    || (AefNode.Number(AefNode.Get(scale, "min")) is { } min && AefNode.Number(AefNode.Get(scale, "max")) is { } max && min > max))
                {
                    Add("metrics.json", "metric");
                }
            }
        }

        // evidence.ndjson: evidence-id, evidence-digest ([EVD-2]), blob, trace-link, content-capture.
        public void Evidence()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (number, record) in _documents.Evidence.Objects)
            {
                var where = $"evidence.ndjson:{number}";
                if (!seen.Add(AefNode.String(record["evidenceId"]) ?? ""))
                {
                    Add(where, "evidence-id");
                }

                var link = record["link"];
                if (AefNode.String(AefNode.Get(link, "blob")) is { } blob)
                {
                    // [EVD-2]: a blob link's digest is the blob's SHA-256, "sha256:" and its file name.
                    if (!string.Equals(AefNode.String(record["digest"]), blob, StringComparison.Ordinal))
                    {
                        Add(where, "evidence-digest");
                    }

                    if (BlobGone(blob))
                    {
                        Add(where, "blob");
                    }
                }

                if (AefNode.String(AefNode.Get(link, "traceId")) is { } traceId && !SpanResolves(traceId, AefNode.String(AefNode.Get(link, "spanId"))))
                {
                    Add(where, "trace-link");
                }

                // [RUN-11]: an unknown evidence kind reads as "other" (§7.3), which holds no content.
                if (_contentOff && AefNode.String(record["kind"]) is { } kind && ContentEvidenceKinds.Contains(kind))
                {
                    Add(where, "content-capture");
                }
            }
        }

        // results.ndjson: every rule whose path is a result line.
        public void Results()
        {
            // The children of each node: the ids of the lines whose parentResultId names it.
            var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            // [RES-8], per case and path: the trial lines, and the rollup lines (those carrying trials) by line number.
            var trialLines = new Dictionary<(string, string), List<JsonObject>>();
            var rollups = new Dictionary<(string, string), List<int>>();
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);   // the first line of each id
            foreach (var (number, line) in _documents.Results.Objects)
            {
                byId.TryAdd(AefNode.String(line["resultId"]) ?? "", line);
                if (AefNode.String(line["parentResultId"]) is { } parent)
                {
                    if (!children.TryGetValue(parent, out var set))
                    {
                        children[parent] = set = new HashSet<string>(StringComparer.Ordinal);
                    }

                    set.Add(AefNode.String(line["resultId"]) ?? "");
                }

                var key = CaseAndPath(line);
                if (line.ContainsKey("trial"))
                {
                    GetOrAdd(trialLines, key).Add(line);
                }

                if (line.ContainsKey("trials"))
                {
                    GetOrAdd(rollups, key).Add(number);
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (number, line) in _documents.Results.Objects)
            {
                var where = $"results.ndjson:{number}";
                var resultId = AefNode.String(line["resultId"]) ?? "";

                // [RES-4]: the id is the hash of the line, and no earlier line has it.
                var first = seen.Add(resultId);
                if (!first || !string.Equals(resultId, ExpectedResultId(line), StringComparison.Ordinal))
                {
                    Add(where, "result-id");
                }

                if (AefNode.String(line["parentResultId"]) is { } parent)
                {
                    if (!_resultIds.Contains(parent))
                    {
                        Add(where, "parent");
                    }

                    // [RES-5]: each child has component.
                    if (!line.ContainsKey("component"))
                    {
                        Add(where, "component");
                    }

                    // [RES-8]: every line under a trial's line carries the same trial (numbers compare as binary64).
                    if (byId.TryGetValue(parent, out var parentLine) && parentLine.ContainsKey("trial")
                        && (AefNode.Number(line["trial"]) is not { } trial || trial != AefNode.Number(parentLine["trial"])))
                    {
                        Add(where, "trials");
                    }
                }

                // [RES-5], [RES-6]: a node with children has aggregation, and its counts hold.
                if (line["aggregation"] is JsonObject aggregation
                    ? !AggregationHolds(aggregation, children.GetValueOrDefault(resultId))
                    : children.ContainsKey(resultId))
                {
                    Add(where, "aggregation");
                }

                if (AefNode.Number(AefNode.At(line, "annotator", "panel", "agree")) is { } agree
                    && AefNode.Number(AefNode.At(line, "annotator", "panel", "of")) is { } of && agree > of)
                {
                    Add(where, "annotator");
                }

                if (line["trials"] is JsonObject trials && !RollupHolds(trials, number, rollups[CaseAndPath(line)], trialLines.GetValueOrDefault(CaseAndPath(line))))
                {
                    Add(where, "trials");
                }

                // [RES-8]: in a closed run every trial line's case and path has a rollup (a running run's case may not yet).
                if (line.ContainsKey("trial") && _documents.IsClosed && !rollups.ContainsKey(CaseAndPath(line)))
                {
                    Add(where, "trials");
                }

                var state = AefNode.String(line["state"]);
                if (state == "pending" && _documents.IsClosed)
                {
                    Add(where, "pending");
                }

                if (AefNode.Strings(line["evidence"]).Any(id => !_evidenceIds.Contains(id)))
                {
                    Add(where, "evidence");
                }

                if (line["reasoning"] is JsonObject reasoning && AefNode.String(reasoning["blob"]) is { } blob)
                {
                    var path = BlobPathOf(blob);
                    if (BlobGone(blob))
                    {
                        Add(where, "blob");
                    }
                    else if (_folder.Has(path) && AefNode.Number(reasoning["bytes"]) is { } bytes && bytes != _folder.Size(path))
                    {
                        Add(where, "reasoning-size");
                    }
                }

                // [RUN-11]: no reasoning and no digest of a prompt in a run that keeps no content.
                if (_contentOff && (line.ContainsKey("reasoning") || AefNode.Has(line["annotator"], "promptHash")))
                {
                    Add(where, "content-capture");
                }

                // §3.9 metric: a score of a metric metrics.json does not declare, or one metric scored twice.
                var scored = AefNode.Objects(line["scores"]).Select(s => AefNode.String(s["metric"]) ?? "").ToList();
                if (scored.Any(m => !_metrics.ContainsKey(m)) || scored.Distinct(StringComparer.Ordinal).Count() != scored.Count)
                {
                    Add(where, "metric");
                }

                if ((AefNode.Time(line["endedAt"]) is { } ended && AefNode.Time(line["startedAt"]) is { } started && ended < started)
                    || RoleAndModelTwice(line["usage"]))
                {
                    Add(where, "result-times");
                }

                if (Inverted(AefNode.At(line, "uncertainty", "ci")))
                {
                    Add(where, "interval");
                }

                if (AefNode.IsTrue(AefNode.At(line, "attack", "success")) && state == "passed")
                {
                    Add(where, "attack");
                }

                if (line["traceLink"] is JsonObject traceLink && AefNode.String(traceLink["traceId"]) is { } traceId
                    && !SpanResolves(traceId, AefNode.String(traceLink["spanId"])))
                {
                    Add(where, "trace-link");
                }
            }
        }

        // [EVD-3]: a blob's bytes hash to its name.
        public void Blobs()
        {
            foreach (var path in _folder.Files)
            {
                if (AefRunFolder.IsBlobPath(path, out var name) && !string.Equals(_folder.Sha256(path), name, StringComparison.Ordinal))
                {
                    Add(path, "blob-digest");
                }
            }
        }

        // A file [RUN-2] does not list, which the seal lists (a producer seals only [RUN-2]'s files). One the seal does
        // not list is §4.1's not-sealed; one under overlays/ is §4.2's.
        public void UnexpectedFiles(IReadOnlySet<string> sealedPaths)
        {
            foreach (var path in _folder.Files)
            {
                if (!ListedByRun2(path) && sealedPaths.Contains(path))
                {
                    Add(path, "unexpected-file");
                }
            }
        }

        // summary.json: summary-run-id, metric, summary, interval, summary-duplicate.
        public void Summary()
        {
            if (_documents.Summary is not { } summary)
            {
                return;   // a running run has none; a closed run without one was reported as schema
            }

            if (!string.Equals(AefNode.String(summary["runId"]), _runId, StringComparison.Ordinal))
            {
                Add("summary.json", "summary-run-id");
            }

            var lanes = AefNode.Objects(summary["lanes"]).ToList();
            var laneNames = lanes.Select(l => AefNode.String(l["lane"]) ?? "").ToList();

            // [SUM-9]: no lane name twice in lanes. Membership ([SUM-3]) still goes by the names: a line without lane
            // belongs to the lane when every lane object has that one name.
            if (laneNames.Distinct(StringComparer.Ordinal).Count() != laneNames.Count)
            {
                Add("summary.json", "summary-duplicate");
            }
            var results = _documents.Results.Objects.Select(o => o.Value).ToList();
            var keys = new HashSet<(string, string, string)>();
            foreach (var lane in lanes)
            {
                var laneName = AefNode.String(lane["lane"]) ?? "";
                foreach (var entry in AefNode.Objects(lane["metrics"]))
                {
                    var metricId = AefNode.String(entry["metric"]) ?? "";
                    var path = AefNode.String(entry["path"]) ?? "";
                    if (!keys.Add((laneName, metricId, path)))
                    {
                        Add("summary.json", "summary-duplicate");
                    }

                    if (Inverted(entry["ci"]))
                    {
                        Add("summary.json", "interval");
                    }

                    if (!_metrics.TryGetValue(metricId, out var metric))
                    {
                        Add("summary.json", "metric");   // and its figures cannot be recomputed without a kind
                        continue;
                    }

                    var figures = AefSummaryCalculator.Compute(
                        results, laneNames, laneName, metricId, AefNode.String(metric["kind"]) ?? "", path,
                        AefNode.String(AefNode.At(entry, "aggregate", "method")));
                    if (!EntryMatches(entry, figures))
                    {
                        Add("summary.json", "summary");
                    }
                }
            }

            // [SUM-9]: no two usage entries with one role and model; an absent model is a value of its own, and roles
            // compare as written.
            var usage = new HashSet<(string?, bool, string?)>();
            foreach (var entry in AefNode.Objects(summary["usage"]))
            {
                if (!usage.Add((AefNode.String(entry["role"]), entry.ContainsKey("model"), AefNode.String(entry["model"]))))
                {
                    Add("summary.json", "summary-duplicate");
                }
            }
        }

        // gates.ndjson: a result a decision names that is no line of the run, or ship on an incomparable comparison.
        public void Gates()
        {
            foreach (var (number, gate) in _documents.Gates.Objects)
            {
                var named = AefNode.Strings(AefNode.At(gate, "inputs", "results")).Concat(AefNode.Strings(gate["decisive"]));

                // [GATE-2]; a comparability this version does not know reads as incomparable (§7.3).
                var comparability = AefNode.String(gate["comparability"]);
                var incomparable = comparability is not ("comparable" or "not_applicable");
                if (named.Any(r => !_resultIds.Contains(r)) || (AefNode.String(gate["outcome"]) == "ship" && incomparable))
                {
                    Add($"gates.ndjson:{number}", "gate");
                }
            }
        }

        // run.json: run-times ([RUN-5]), calibration ([RUN-9]), execution-policy.
        public void Run()
        {
            var startedAt = AefNode.Time(_run["startedAt"]);
            if (AefNode.Time(_run["endedAt"]) is { } ended && startedAt is { } started && ended < started)
            {
                Add("run.json", "run-times");
            }

            foreach (var judge in AefNode.Objects(_run["judges"]))
            {
                if (judge["calibration"] is not JsonObject calibration)
                {
                    continue;
                }

                var impossible = AefNode.Number(calibration["dangerousErrors"]) is { } dangerous
                                 && AefNode.Number(calibration["n"]) is { } n && dangerous > n;
                var afterTheRun = AefNode.Time(calibration["measuredAt"]) is { } measured && startedAt is { } start && measured > start;
                if (impossible || afterTheRun)
                {
                    Add("run.json", "calibration");
                }
            }

            var policy = AefNode.At(_run, "suite", "executionPolicy");
            if (AefNode.Number(AefNode.Get(policy, "requirePasses")) is { } requirePasses
                && AefNode.Number(AefNode.Get(policy, "trialsPerCase")) is { } trials && requirePasses > trials)
            {
                Add("run.json", "execution-policy");
            }
        }

        // [SEC-6]: in a run with contentCapture off, no span, span event or log record carries content.
        public void Telemetry()
        {
            if (!_contentOff)
            {
                return;
            }

            foreach (var (number, data) in _documents.Traces.Objects)
            {
                if (Otlp.ResourcesAndScopes(data, "resourceSpans", "scopeSpans").Any(Otlp.CarriesContent)
                    || Otlp.Spans(data).Any(span => Otlp.CarriesContent(span) || AefNode.Objects(span["events"]).Any(Otlp.CarriesContent)))
                {
                    Add($"traces.otlp.jsonl:{number}", "content-capture");
                }
            }

            foreach (var (number, data) in _documents.Logs.Objects)
            {
                if (Otlp.ResourcesAndScopes(data, "resourceLogs", "scopeLogs").Any(Otlp.CarriesContent)
                    || Otlp.LogRecords(data).Any(record => record.ContainsKey("body") || Otlp.CarriesContent(record)))
                {
                    Add($"logs.otlp.jsonl:{number}", "content-capture");
                }
            }
        }

        private void Add(string path, string code) => Problems.Add(new AefProblem(path, code));

        // [RES-4] for this line, or null when its parts cannot make one.
        private string? ExpectedResultId(JsonObject line)
        {
            if (AefNode.String(line["caseId"]) is not { } caseId || AefNode.String(line["path"]) is not { } path)
            {
                return null;
            }

            long? trial = AefNode.Number(line["trial"]) is { } t ? (long)t : null;
            try
            {
                return AefResultId.Compute(_runId, caseId, path, trial);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // [RES-6]: total is the number of the node's children (the lines whose parentResultId is its resultId, a child
        // counted once by its id: a line repeating a child's id is that child again, and a result-id problem);
        // measured ≤ total; the unmeasured counts (an absent unmeasured or count is 0) add up to total − measured; every
        // decisive id is a child of this node.
        private static bool AggregationHolds(JsonObject aggregation, HashSet<string>? children)
        {
            var measured = AefNode.Number(aggregation["measured"]) ?? 0;
            var total = AefNode.Number(aggregation["total"]) ?? 0;
            var unmeasured = 0.0;
            if (aggregation["unmeasured"] is JsonObject counts)
            {
                foreach (var (_, count) in counts)
                {
                    unmeasured += AefNode.Number(count) ?? 0;
                }
            }

            return total == (children?.Count ?? 0) && measured <= total && unmeasured == total - measured
                   && AefNode.Strings(aggregation["decisive"]).All(id => children?.Contains(id) == true);
        }

        // [RES-8]: a rollup's passed is at most its n; it is the first rollup of its case and path (a second one is
        // the problem); and when the case has trial lines at that path, n is their number and passed the number of them
        // in state passed.
        private static bool RollupHolds(JsonObject trials, int number, List<int> rollupsHere, List<JsonObject>? trialsHere)
        {
            var n = AefNode.Number(trials["n"]);
            var passed = AefNode.Number(trials["passed"]);
            if (passed > n || rollupsHere[0] != number)
            {
                return false;
            }

            return trialsHere is not { Count: > 0 }
                   || (n == trialsHere.Count && passed == trialsHere.Count(t => AefNode.String(t["state"]) == "passed"));
        }

        // A line's case and path, as written.
        private static (string, string) CaseAndPath(JsonObject line) =>
            (AefNode.String(line["caseId"]) ?? "", AefNode.String(line["path"]) ?? "");

        private static List<T> GetOrAdd<T>(Dictionary<(string, string), List<T>> map, (string, string) key)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            return list;
        }

        // A blob reference ("sha256:<hex>") whose blob is not in the run and that no authorized redaction withholds.
        private bool BlobGone(string reference)
        {
            var name = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference[7..] : reference;
            return !_folder.Has(AefRunFolder.BlobPath(name)) && !_withheld.Contains(name);
        }

        private static string BlobPathOf(string reference) =>
            AefRunFolder.BlobPath(reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference[7..] : reference);

        // §3.9 trace-link: with traces.otlp.jsonl present, a span link names one of its spans; a traceLink without
        // spanId names a trace, which resolves when any span has that traceId. Without the file, nothing is verified.
        private bool SpanResolves(string traceId, string? spanId)
        {
            if (_spans is not { } known)
            {
                return true;
            }

            var (traces, spans) = known;
            var trace = traceId.ToLowerInvariant();
            return spanId is null ? traces.Contains(trace) : spans.Contains((trace, spanId.ToLowerInvariant()));
        }

        // A usage list that names one role and model twice ([RES-10]): compared as written, an absent model a value
        // of its own.
        private static bool RoleAndModelTwice(JsonNode? usage)
        {
            var parties = new HashSet<(string?, bool, string?)>();
            return AefNode.Objects(usage).Any(u => !parties.Add((AefNode.String(u["role"]), u.ContainsKey("model"), AefNode.String(u["model"]))));
        }

        // An interval whose low exceeds its high.
        private static bool Inverted(JsonNode? interval) =>
            AefNode.Number(AefNode.Get(interval, "low")) is { } low && AefNode.Number(AefNode.Get(interval, "high")) is { } high && low > high;

        // §3.6: N, n and notMeasured equal; sum (when written) and value within 1e-9 × max(1, |recomputed|); value
        // compared only where AEF defines it ([SUM-8]), and always null when n is 0.
        private static bool EntryMatches(JsonObject entry, AefSummaryFigures figures)
        {
            if (AefNode.Number(entry["N"]) != figures.N || AefNode.Number(entry["n"]) != figures.Measured
                || AefNode.Number(entry["notMeasured"]) != figures.NotMeasured)
            {
                return false;
            }

            if (entry.ContainsKey("sum") && !(AefNode.Number(entry["sum"]) is { } sum && AefSummaryCalculator.Matches(sum, figures.Sum)))
            {
                return false;
            }

            if (!figures.ValueDefined)
            {
                // [SUM-8]: a method of the producer's is never recomputed, but its value is a number when n is not 0.
                return AefNode.Number(entry["value"]) is not null;
            }

            var written = AefNode.Number(entry["value"]);
            return figures.Value is { } value
                ? written is { } w && AefSummaryCalculator.Matches(w, value)
                : written is null;
        }
    }

    /// <summary>Reading OTLP/JSON objects ([RUN-14]): spans of a <c>TracesData</c>, log records of a <c>LogsData</c>, attributes.</summary>
    internal static class Otlp
    {
        /// <summary>The spans of a TracesData object: <c>resourceSpans[].scopeSpans[].spans[]</c>.</summary>
        public static IEnumerable<JsonObject> Spans(JsonObject data) =>
            AefNode.Objects(data["resourceSpans"]).SelectMany(r => AefNode.Objects(r["scopeSpans"])).SelectMany(s => AefNode.Objects(s["spans"]));

        /// <summary>The log records of a LogsData object: <c>resourceLogs[].scopeLogs[].logRecords[]</c>.</summary>
        public static IEnumerable<JsonObject> LogRecords(JsonObject data) =>
            AefNode.Objects(data["resourceLogs"]).SelectMany(r => AefNode.Objects(r["scopeLogs"])).SelectMany(s => AefNode.Objects(s["logRecords"]));

        /// <summary>
        /// The resources and scopes of a TracesData or LogsData object, whose <c>attributes</c> [SEC-6] reads too:
        /// <c>resourceSpans[].resource</c> and <c>resourceSpans[].scopeSpans[].scope</c> (or the same for logs).
        /// </summary>
        public static IEnumerable<JsonObject> ResourcesAndScopes(JsonObject data, string resources, string scopes)
        {
            foreach (var resource in AefNode.Objects(data[resources]))
            {
                if (resource["resource"] is JsonObject r)
                {
                    yield return r;
                }

                foreach (var scope in AefNode.Objects(resource[scopes]))
                {
                    if (scope["scope"] is JsonObject s)
                    {
                        yield return s;
                    }
                }
            }
        }

        /// <summary>Whether a span, span event, log record, resource or scope has an attribute (<c>{"key", "value"}</c>) [SEC-6] names.</summary>
        public static bool CarriesContent(JsonObject holder) =>
            AefNode.Objects(holder["attributes"]).Any(a => AefNode.String(a["key"]) is { } key && ContentAttributes.Contains(key));
    }
}
