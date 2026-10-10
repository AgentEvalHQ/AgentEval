// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Adapters.Inspect;

/// <summary>How <see cref="AefInspectExporter"/> reads the AEF run.</summary>
public sealed record AefInspectExportOptions
{
    /// <summary>
    /// The trust policy the run is verified with ([SIG-4]), or null: a redaction only its identities may authorize
    /// withholds a blob ([OVL-10]); without one, a deleted blob makes the run invalid, and it is not exported.
    /// </summary>
    public TrustPolicy? Policy { get; init; }

    /// <summary>
    /// Leave the run's overlay events out (<c>--ignore-overlays</c>): without it a run with overlay events is refused
    /// (IN-4); with it, the sealed lines are converted as if the run had none.
    /// </summary>
    public bool IgnoreOverlays { get; init; }
}

/// <summary>What an export to Inspect wrote.</summary>
/// <param name="RunId">The AEF run's <c>runId</c> (the log's <c>eval.eval_id</c> and <c>eval.run_id</c>).</param>
/// <param name="Outcome">The run's verification outcome (§4.5): <c>intact</c> or <c>unsealed</c>; an invalid run is refused.</param>
/// <param name="Text">The <c>EvalLog</c> in <c>.json</c> form, as Inspect writes it: indented by two spaces, ending in LF.</param>
/// <param name="Samples">The log's samples: one per case and trial.</param>
/// <param name="Notes">What the export did not carry, beyond the page's fixed list, in plain words.</param>
public sealed record AefInspectExport(string RunId, AefOutcome Outcome, string Text, int Samples, IReadOnlyList<string> Notes)
{
    /// <summary>The log as a file: UTF-8, no byte-order mark.</summary>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Text);
}

/// <summary>
/// An export refused, naming the rule (contracts/aef/1/interop/inspect.md, "AEF → Inspect", "Refused"): a run that does
/// not verify (IN-11), the eval header Inspect requires (IN-1), the shape of <c>results</c> (IN-3), overlay events (IN-4),
/// a sample's content or times (IN-5), a reasoning or case-content blob that is not UTF-8 (IN-12). The whole export is
/// refused, and nothing is written.
/// </summary>
public sealed class AefInspectExportException(string message) : Exception(message);

/// <summary>
/// AEF → Inspect (contracts/aef/1/interop/inspect.md, "AEF → Inspect" and its rules settled 10-09): an AEF run as one
/// Inspect <c>EvalLog</c> (log format version 2) in <c>.json</c> form.
/// <list type="bullet">
/// <item>The eval header (IN-1): <c>eval_id</c> and <c>run_id</c> are the <c>runId</c>; <c>created</c> the start;
/// <c>task</c> the suite's ref without <c>suite:</c> and <c>task_version</c> its version; <c>dataset</c> the number of
/// cases and their ids; <c>model</c> the subject's ref with its name decoded as [ENC-13] encodes it (a model subject's
/// name alone, any other subject's <c>kind:name</c>, R7I-8); <c>model_roles</c> the run's judge under the role <c>judge</c>; <c>config</c> the trials per case as <c>epochs</c> and
/// the aggregation as <c>epochs_reducer</c>; <c>packages</c> the producer; <c>metadata.aef</c> what the table sends to
/// <c>eval.metadata</c>.</item>
/// <item>One sample per case and trial (<c>epoch</c> = <c>trial</c> + 1), each line of it a score under its path: the
/// score's value (its label when it has one), or a map of metric to value for several scores; a line without scores
/// (a typed absence, IN-2) is <c>NaN</c> with the state in <c>reason</c>; the line's <c>reason</c>, else its reasoning
/// blob's text, is the explanation, and a run that keeps no content gives none (IN-13); its id, state, evaluator and
/// the facts the table names go to
/// <c>metadata.aef</c>.</item>
/// <item>A rollup line is a reduction's sample, under the reducer its aggregation names.</item>
/// <item>A sample's usage is the sum of its lines' entries, per role and per model (IN-5); its times and
/// <c>total_time</c> are its root line's; its <c>input</c> and <c>target</c> the text of the <c>input</c> and
/// <c>expected</c> evidence its lines cite, empty when the run kept none (a record no line cites is neither carried nor
/// refused; a blob a redaction withholds is left out).</item>
/// <item><c>results</c> (IN-3): one <c>EvalScore</c> per summary entry; <c>stats</c> the run's times and the summary's
/// usage.</item>
/// </list>
/// Only a run that verifies is exported (IN-11), and its sealed lines: overlays are not applied, and a run with overlay
/// events is refused unless <see cref="AefInspectExportOptions.IgnoreOverlays"/> (IN-4). The page fixes values, not
/// bytes (R7I-1); the text is what Python's <c>json.dumps(log, indent=2, ensure_ascii=False)</c> writes, with the bare
/// token <c>NaN</c> for an unscored value, as Inspect writes it, members in the checked examples' order.
/// </summary>
public static class AefInspectExporter
{
    /// <summary>The log format version written (<c>version</c>).</summary>
    public const int LogVersion = 2;

    /// <summary>The role of <c>eval.model_roles</c> the run's judge is under (IN-1: the name AEF's <c>usage</c> gives it).</summary>
    public const string JudgeRole = "judge";

    private const string OverlayEvents = "overlays/events.ndjson";

    // What the table sends to Score.metadata, beside the line's resultId, state and evaluator (in the line's order).
    private static readonly HashSet<string> LineFacts = new(StringComparer.Ordinal)
    {
        "parentResultId", "component", "aggregation", "verdictRule", "severity", "annotator",
    };

    /// <summary>Exports the run in <paramref name="runDirectory"/>, in memory.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    /// <exception cref="IOException">The folder or a file cannot be read.</exception>
    /// <exception cref="AefInspectExportException">The run does not verify, or the page's rules refuse it.</exception>
    public static AefInspectExport Export(string runDirectory, AefInspectExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runDirectory);
        options ??= new AefInspectExportOptions();
        var folder = AefRunFolder.Open(runDirectory);
        var verification = AefRunVerifier.Verify(folder, new AefVerifyOptions { Policy = options.Policy });
        if (verification.Outcome == AefOutcome.Invalid)
        {
            // IN-11: only a run that verifies (intact or unsealed, with no problem but an authorized withhold).
            throw new AefInspectExportException(
                $"{runDirectory} is invalid (spec 04 §4.5): {string.Join(", ", verification.Problems.Where(p => p.Code != "withheld").Take(10).Select(p => $"{p.Path} {p.Code}"))}"
                + " — only a run that verifies is exported (IN-11).");
        }

        var notes = new List<string>();
        if (folder.Has(OverlayEvents) && folder.Size(OverlayEvents) > 0)
        {
            if (!options.IgnoreOverlays)
            {
                throw new AefInspectExportException(
                    $"{runDirectory} has overlay events ({OverlayEvents}): the table sends override and adjudicate to Score.history and the other kinds to log_updates, but not the shape of either entry, so the run is refused (IN-4); --ignore-overlays converts the rest.");
            }

            notes.Add("The run's overlay events are left out (--ignore-overlays, IN-4): the log holds the sealed lines.");
        }

        var documents = AefRunDocuments.Read(folder);
        var run = documents.Run!;
        var runId = Str(run["runId"])!;
        var header = Header(run, runDirectory);
        var entries = Entries(documents, runDirectory);
        var evidence = Evidence(documents);
        var contentOff = Str(run["contentCapture"]) == "off";   // [RUN-11]: none written reads as on

        // The lines: the samples (one per case and trial) and the reductions (the rollup lines, per path and reducer).
        var cases = new List<string>();
        var samples = new List<Sample>();
        var sampleOf = new Dictionary<(string Case, int Epoch), Sample>();
        var reductions = new List<Reduction>();
        var reductionOf = new Dictionary<(string Path, string Reducer), Reduction>();
        foreach (var (number, line) in documents.Results.Objects)
        {
            var where = $"results.ndjson:{number.ToString(CultureInfo.InvariantCulture)}";
            var caseId = Str(line["caseId"])!;
            if (!cases.Contains(caseId, StringComparer.Ordinal))
            {
                cases.Add(caseId);
            }

            if (line["trials"] is JsonObject trials)
            {
                var path = Str(line["path"])!;
                var reducer = Reducer(Str(trials["aggregation"])!, Long(trials["k"]), Long(trials["n"]) ?? 1, where);
                if (!reductionOf.TryGetValue((path, reducer), out var reduction))
                {
                    reductionOf[(path, reducer)] = reduction = new Reduction(path, reducer);
                    reductions.Add(reduction);
                }

                reduction.Lines.Add((where, line));
                continue;
            }

            var epoch = (int)(Long(line["trial"]) ?? 0) + 1;
            if (!sampleOf.TryGetValue((caseId, epoch), out var sample))
            {
                sampleOf[(caseId, epoch)] = sample = new Sample(caseId, epoch);
                samples.Add(sample);
            }

            sample.Lines.Add((where, line));
        }

        var eval = Eval(run, header, cases);
        var log = new JsonObject
        {
            ["version"] = LogVersion,
            ["status"] = Str(run["status"]) switch { "completed" => "success", "aborted" => "error", _ => "started" },
            ["eval"] = eval,
        };
        if (documents.Summary is not null)
        {
            log["results"] = Results(entries, samples.Count);
        }

        log["stats"] = Stats(run, documents.Summary);
        if (Str(run["status"]) == "aborted")
        {
            // EvalError: message, traceback and traceback_ansi; AEF records no traceback.
            log["error"] = new JsonObject { ["message"] = Str(run["abortReason"]), ["traceback"] = "", ["traceback_ansi"] = "" };
        }

        var context = new Context(folder, evidence, contentOff);
        log["samples"] = new JsonArray([.. samples.Select(s => (JsonNode?)SampleJson(context, s))]);
        if (reductions.Count > 0)
        {
            log["reductions"] = new JsonArray([.. reductions.Select(r => (JsonNode?)new JsonObject
            {
                ["scorer"] = r.Path,
                ["reducer"] = r.Reducer,
                ["samples"] = new JsonArray([.. r.Lines.Select(l =>
                {
                    var score = Score(context, l.Line, l.Where);
                    score["sample_id"] = Str(l.Line["caseId"]);
                    return (JsonNode?)score;
                })]),
            })]);
        }

        if (contentOff)
        {
            notes.Add("The run keeps no content (contentCapture: off): no score carries an explanation, not even a line's reason (IN-13), and every sample's input and target are empty.");
        }

        return new AefInspectExport(runId, verification.Outcome, InspectJson.Indented(log) + "\n", samples.Count, notes);
    }

    /// <summary>
    /// Exports the run into <paramref name="outputFile"/> (the <c>.json</c> log, UTF-8): written only when the whole export
    /// succeeded, and never over an existing file.
    /// </summary>
    /// <exception cref="IOException">The output file exists, or a file cannot be read or written.</exception>
    /// <exception cref="AefInspectExportException">The run does not verify, or the page's rules refuse it.</exception>
    public static AefInspectExport ExportToFile(string runDirectory, string outputFile, AefInspectExportOptions? options = null)
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

    // ------------------------------------------------------------------ the eval header (IN-1)

    private sealed record HeaderFacts(string Task, string Version, string Model, string? Judge);

    private static HeaderFacts Header(JsonObject run, string runDirectory)
    {
        if (run["suite"] is not JsonObject suite)
        {
            throw new AefInspectExportException($"{runDirectory}: run.json has no suite, and eval.task is required (IN-1).");
        }

        var suiteRef = Str(suite["ref"])!;
        if (!suiteRef.StartsWith("suite:", StringComparison.Ordinal))
        {
            throw new AefInspectExportException($"{runDirectory}: the suite's ref {suiteRef} is not suite:<task>, and eval.task is the suite's ref without suite: (IN-1).");
        }

        var judges = Objs(run["judges"]).ToList();
        if (judges.Count > 1)
        {
            throw new AefInspectExportException($"{runDirectory}: run.json lists {judges.Count} judges, and a role of eval.model_roles holds one model (IN-1).");
        }

        var model = ModelName(Str(run["subject"]?["ref"])!, Str(run["subject"]?["kind"]) == "model");
        return new HeaderFacts(suiteRef["suite:".Length..], Str(suite["version"])!, model, judges.Count == 1 ? Str(judges[0]["model"]) : null);
    }

    /// <summary>
    /// <c>eval.model</c> (IN-1, R7I-8): the subject's ref with its name decoded as [ENC-13] encodes it (each <c>%XX</c>
    /// the byte XX, read as UTF-8; <c>-</c> the empty name; a name that does not decode is kept as written): a model
    /// subject's name alone, any other subject's <c>kind:name</c>.
    /// </summary>
    internal static string ModelName(string subjectRef, bool modelSubject)
    {
        var colon = subjectRef.IndexOf(':', StringComparison.Ordinal);
        var (kind, name) = colon < 0 ? ("", subjectRef) : (subjectRef[..colon], subjectRef[(colon + 1)..]);
        var decoded = Decode(name);
        return modelSubject || colon < 0 ? decoded : $"{kind}:{decoded}";
    }

    private static string Decode(string name)
    {
        if (name == "-")
        {
            return "";
        }

        var bytes = new List<byte>(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] != '%')
            {
                bytes.Add((byte)name[i]);   // a ref's name is printable ASCII
                continue;
            }

            if (i + 2 >= name.Length || !char.IsAsciiHexDigit(name[i + 1]) || !char.IsAsciiHexDigit(name[i + 2]))
            {
                return name;
            }

            bytes.Add(byte.Parse(name.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            i += 2;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return name;
        }
    }

    private static JsonObject Eval(JsonObject run, HeaderFacts header, List<string> cases)
    {
        var eval = new JsonObject
        {
            ["eval_id"] = Str(run["runId"]),
            ["run_id"] = Str(run["runId"]),
            ["created"] = Str(run["startedAt"]),
            ["task"] = header.Task,
            ["task_version"] = header.Version,
            ["dataset"] = new JsonObject
            {
                ["samples"] = cases.Count,
                ["sample_ids"] = new JsonArray([.. cases.Select(c => (JsonNode?)c)]),
            },
            ["model"] = header.Model,
        };
        if (header.Judge is { } judge)
        {
            eval["model_roles"] = new JsonObject { [JudgeRole] = new JsonObject { ["model"] = judge } };
        }

        var config = new JsonObject();
        if (run["suite"]?["executionPolicy"] is JsonObject policy)
        {
            config["epochs"] = Copy(policy["trialsPerCase"]);
            if (Str(policy["aggregation"]) is { } aggregation)
            {
                config["epochs_reducer"] = new JsonArray(Reducer(aggregation, Long(policy["k"]), Long(policy["trialsPerCase"]) ?? 1, "run.json"));
            }
        }

        eval["config"] = config;
        eval["packages"] = new JsonObject { [Str(run["producer"]?["name"])!] = Str(run["producer"]?["version"]) };

        // What the table sends to eval.metadata, under aef, in the table's order.
        var aef = new JsonObject();
        if (Str(run["suite"]?["digest"]) is { } digest)
        {
            aef["suite"] = new JsonObject { ["digest"] = digest };
        }

        var judges = Objs(run["judges"]).ToList();
        if (judges.Any(j => j.ContainsKey("rubricDigest") || j.ContainsKey("calibration")))
        {
            aef["judges"] = new JsonArray([.. judges.Select(j =>
            {
                var entry = new JsonObject { ["model"] = Str(j["model"]) };
                foreach (var name in new[] { "rubricDigest", "calibration" })
                {
                    if (j[name] is { } value)
                    {
                        entry[name] = Copy(value);
                    }
                }

                return (JsonNode?)entry;
            })]);
        }

        foreach (var name in new[] { "execution", "contentCapture", "deployment", "imported" })
        {
            if (run[name] is { } value)
            {
                aef[name] = Copy(value);
            }
        }

        if (aef.Count > 0)
        {
            eval["metadata"] = new JsonObject { ["aef"] = aef };
        }

        return eval;
    }

    // The table: MajorityVote → majority, Mean → mean, Median → median, Max → max, PassAtK → pass_at_<k>,
    // AnyPass → at_least_1, AllPass → at_least_<n>.
    private static string Reducer(string aggregation, long? k, long n, string where) => aggregation switch
    {
        "MajorityVote" => "majority",
        "Mean" => "mean",
        "Median" => "median",
        "Max" => "max",
        "PassAtK" => k is { } value
            ? $"pass_at_{value.ToString(CultureInfo.InvariantCulture)}"
            : throw new AefInspectExportException($"{where}: the aggregation PassAtK without k, and the table writes pass_at_<k>."),
        "AnyPass" => "at_least_1",
        "AllPass" => $"at_least_{n.ToString(CultureInfo.InvariantCulture)}",
        _ => throw new AefInspectExportException($"{where}: the aggregation {aggregation} has no epochs reducer in the table."),
    };

    // ------------------------------------------------------------------ results (IN-3) and stats

    private static List<(string Lane, JsonObject Entry)> Entries(AefRunDocuments documents, string runDirectory)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var metric in Objs(documents.Metrics?["metrics"]))
        {
            kinds[Str(metric["id"])!] = Str(metric["kind"])!;
        }

        var entries = new List<(string Lane, JsonObject Entry)>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lane in Objs(documents.Summary?["lanes"]))
        {
            var name = Str(lane["lane"])!;
            foreach (var entry in Objs(lane["metrics"]))
            {
                var path = Str(entry["path"])!;
                var metric = Str(entry["metric"])!;
                if (paths.TryGetValue(path, out var other))
                {
                    throw new AefInspectExportException(
                        $"{runDirectory}: two summary entries at the path {path} ({other} and {name}/{metric}), and Inspect has one EvalScore per scorer (IN-3).");
                }

                if (kinds.GetValueOrDefault(metric) == "count")
                {
                    throw new AefInspectExportException(
                        $"{runDirectory}: the summary entry {name}/{metric} at {path} is for a metric of kind count, whose value is a sum, not a mean (IN-3).");
                }

                paths[path] = $"{name}/{metric}";
                entries.Add((name, entry));
            }
        }

        return entries;
    }

    private static JsonObject Results(List<(string Lane, JsonObject Entry)> entries, int samples)
    {
        var scores = new JsonArray();
        foreach (var (lane, entry) in entries)
        {
            var path = Str(entry["path"])!;
            var method = Str(entry["aggregate"]?["method"]) ?? "mean";
            var value = new JsonObject { ["name"] = method, ["value"] = entry["value"] is { } v ? Copy(v) : InspectJson.NaN() };
            var parameters = new JsonObject();
            foreach (var (name, member) in entry["aggregate"] as JsonObject ?? new JsonObject())
            {
                if (name != "method")
                {
                    parameters[name] = Copy(member);
                }
            }

            if (parameters.Count > 0)
            {
                value["params"] = parameters;
            }

            var metrics = new JsonObject { [method] = value };
            if (entry["stderr"] is { } stderr)
            {
                metrics["stderr"] = new JsonObject { ["name"] = "stderr", ["value"] = Copy(stderr) };
            }

            var aef = new JsonObject { ["lane"] = lane };
            foreach (var name in new[] { "metric", "N", "notMeasured", "verdict", "rule", "ci" })
            {
                if (entry[name] is { } member)
                {
                    aef[name] = Copy(member);
                }
            }

            scores.Add(new JsonObject
            {
                ["name"] = path,
                ["scorer"] = path,
                ["scored_samples"] = Copy(entry["n"]),
                ["unscored_samples"] = Copy(entry["notMeasured"]),
                ["metrics"] = metrics,
                ["metadata"] = new JsonObject { ["aef"] = aef },
            });
        }

        return new JsonObject { ["total_samples"] = samples, ["completed_samples"] = samples, ["scores"] = scores };
    }

    // stats: the run's times, and the summary's usage per model and per role (SUM-7).
    private static JsonObject Stats(JsonObject run, JsonObject? summary)
    {
        var stats = new JsonObject { ["started_at"] = Str(run["startedAt"]) };
        if (Str(run["endedAt"]) is { } endedAt)
        {
            stats["completed_at"] = endedAt;
        }

        var (byModel, byRole) = (new Totals(), new Totals());
        foreach (var entry in Objs(summary?["usage"]))
        {
            byRole.Add(Str(entry["role"])!, entry);
            if (Str(entry["model"]) is { } model)
            {
                byModel.Add(model, entry);
            }
        }

        if (byModel.Count > 0)
        {
            stats["model_usage"] = byModel.ToJson();
        }

        if (byRole.Count > 0)
        {
            stats["role_usage"] = byRole.ToJson();
        }

        return stats;
    }

    // ------------------------------------------------------------------ samples (IN-2, IN-5)

    private sealed record Sample(string CaseId, int Epoch)
    {
        public List<(string Where, JsonObject Line)> Lines { get; } = [];
    }

    private sealed record Reduction(string Path, string Reducer)
    {
        public List<(string Where, JsonObject Line)> Lines { get; } = [];
    }

    // What the samples' scores read beyond their lines.
    private sealed record Context(AefRunFolder Folder, Dictionary<string, (string Where, JsonObject Record)> Evidence, bool ContentOff);

    // evidence.ndjson by id. A record no line cites is not carried ("other evidence") and not refused (R7I-6).
    private static Dictionary<string, (string Where, JsonObject Record)> Evidence(AefRunDocuments documents)
    {
        var records = new Dictionary<string, (string, JsonObject)>(StringComparer.Ordinal);
        foreach (var (number, record) in documents.Evidence.Objects)
        {
            records[Str(record["evidenceId"])!] = ($"evidence.ndjson:{number.ToString(CultureInfo.InvariantCulture)}", record);
        }

        return records;
    }

    private static JsonObject SampleJson(Context context, Sample sample)
    {
        var where = $"the sample of case {sample.CaseId}, epoch {sample.Epoch.ToString(CultureInfo.InvariantCulture)}";

        // The evidence its lines cite (IN-5): output or transcript evidence is refused, and input or expected evidence
        // that is not a blob of the run.
        var cited = sample.Lines.SelectMany(l => Strings(l.Line["evidence"])).Distinct(StringComparer.Ordinal)
            .Select(id => context.Evidence.TryGetValue(id, out var record) ? record : default).Where(r => r.Record is not null).ToList();
        foreach (var (recordWhere, record) in cited)
        {
            var kind = Str(record["kind"]);
            if (kind is "output" or "transcript")
            {
                throw new AefInspectExportException(
                    $"{recordWhere}: {kind} evidence {where} cites: samples[].output is a ModelOutput and messages a list of ChatMessage, and the table does not say how a blob's text becomes either (IN-5).");
            }

            if (kind is "input" or "expected" && Str(record["link"]?["blob"]) is null)
            {
                throw new AefInspectExportException(
                    $"{recordWhere}: {kind} evidence {where} cites that is not a blob of the run: samples[].{(kind == "input" ? "input" : "target")} is its text (IN-5).");
            }
        }

        var json = new JsonObject
        {
            ["id"] = sample.CaseId,
            ["epoch"] = sample.Epoch,
            ["input"] = Content(context, cited, "input", where) ?? "",
            ["target"] = Content(context, cited, "expected", where) ?? "",
        };

        var scores = new JsonObject();
        var (byModel, byRole) = (new Totals(), new Totals());
        foreach (var (lineWhere, line) in sample.Lines)
        {
            var path = Str(line["path"])!;
            if (scores.ContainsKey(path))
            {
                throw new AefInspectExportException($"{lineWhere}: a second line at the path {path} in {where}: a sample has one score per path (IN-5).");
            }

            scores[path] = Score(context, line, lineWhere);

            // IN-5: the sample's usage is the sum of its lines' entries, per role and per model; a judge's entry without a
            // model is under its annotator's.
            foreach (var entry in Objs(line["usage"]))
            {
                var role = Str(entry["role"])!;
                byRole.Add(role, entry);
                if ((Str(entry["model"]) ?? (role == "judge" ? Str(line["annotator"]?["model"]) : null)) is { } model)
                {
                    byModel.Add(model, entry);
                }
            }
        }

        json["scores"] = scores;
        if (byModel.Count > 0)
        {
            json["model_usage"] = byModel.ToJson();
        }

        if (byRole.Count > 0)
        {
            json["role_usage"] = byRole.ToJson();
        }

        // The times and duration of the case's root line (IN-5: refused when its roots carry two different ones; times are
        // compared as instants, [ENC-8], and the first root's spelling is written).
        var roots = sample.Lines.Where(l => Str(l.Line["parentResultId"]) is null).Select(l => l.Line).ToList();
        foreach (var (member, name) in new[] { ("startedAt", "started_at"), ("endedAt", "completed_at") })
        {
            var times = roots.Select(r => Str(r[member])).OfType<string>().ToList();
            var instants = times.Select(AefTime.Parse).Distinct().Count();
            if (instants > 1)
            {
                throw new AefInspectExportException($"{where}: its root lines carry {instants} different {member} ({string.Join(", ", times)}), and the table takes {name} from a case's root line (IN-5).");
            }

            if (instants == 1)
            {
                json[name] = times[0];
            }
        }

        var durations = roots.Where(r => r["durationMs"] is not null).Select(r => r["durationMs"]!.GetValue<double>()).Distinct().ToList();
        if (durations.Count > 1)
        {
            throw new AefInspectExportException($"{where}: its root lines carry {durations.Count} different durationMs, and the table takes total_time from a case's root line (IN-5).");
        }

        if (durations.Count == 1)
        {
            json["total_time"] = JsonValue.Create(durations[0] / 1000);   // seconds
        }

        return json;
    }

    // The text of the input or expected evidence the sample's lines cite; refused (IN-5) when two such records give two texts.
    private static string? Content(Context context, List<(string Where, JsonObject Record)> cited, string kind, string where)
    {
        var texts = new List<string>();
        foreach (var (recordWhere, record) in cited.Where(r => Str(r.Record["kind"]) == kind))
        {
            if (BlobText(context, Str(record["link"]?["blob"])!, $"{recordWhere}: its {kind} blob") is { } text && !texts.Contains(text, StringComparer.Ordinal))
            {
                texts.Add(text);
            }
        }

        return texts.Count switch
        {
            0 => null,
            1 => texts[0],
            _ => throw new AefInspectExportException(
                $"{where}: its lines cite {texts.Count} {kind} records with different text, and samples[].{(kind == "input" ? "input" : "target")} holds one (IN-5)."),
        };
    }

    // One line as an Inspect Score: value, reason (a line without scores), explanation, metadata.aef.
    private static JsonObject Score(Context context, JsonObject line, string where)
    {
        var score = new JsonObject();
        var state = Str(line["state"])!;
        var values = Objs(line["scores"]).ToList();
        switch (values.Count)
        {
            case 0:
                // A typed absence (the table), or a measured line without scores (IN-2): NaN, the state as the reason.
                score["value"] = InspectJson.NaN();
                score["reason"] = state;
                break;
            case 1:
                score["value"] = Value(values[0]);
                break;
            default:
                // A second score goes in a map value: metric → value.
                var map = new JsonObject();
                foreach (var value in values)
                {
                    map[Str(value["metric"])!] = Value(value);
                }

                score["value"] = map;
                break;
        }

        // The line's reason, else its reasoning blob's text; a run that keeps no content exports none of its free text as
        // an explanation, whatever its reason holds (IN-13).
        if (!context.ContentOff
            && (Str(line["reason"]) ?? (Str(line["reasoning"]?["blob"]) is { } blob ? BlobText(context, blob, $"{where}: its reasoning blob") : null)) is { } explanation)
        {
            score["explanation"] = explanation;
        }

        var aef = new JsonObject
        {
            ["resultId"] = Str(line["resultId"]),
            ["state"] = state,
            ["evaluator"] = Copy(line["evaluator"]),
        };
        foreach (var (name, member) in line)
        {
            if (LineFacts.Contains(name) && member is not null)
            {
                aef[name] = Copy(member);
            }
        }

        score["metadata"] = new JsonObject { ["aef"] = aef };
        return score;
    }

    // A score's value: its label (e.g. C) as that string, else the number as the line spells it.
    private static JsonNode Value(JsonObject score) =>
        Str(score["label"]) is { } label ? JsonValue.Create(label)! : Copy(score["value"])!;

    // A blob's text (IN-12): null when an authorized redaction withholds it (the run verified, so a missing blob is a
    // withheld one), which is left out with nothing in its place; a blob that is not UTF-8 refuses the export, as OT-9 does.
    private static string? BlobText(Context context, string blob, string what)
    {
        var path = AefRunFolder.BlobPath(blob["sha256:".Length..]);
        if (!context.Folder.Has(path))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(context.Folder.Read(path, context.Folder.Size(path)));
        }
        catch (DecoderFallbackException)
        {
            throw new AefInspectExportException($"{what} is not UTF-8 text, and an explanation, an input and a target are text: the export is refused (IN-12).");
        }
    }

    // ------------------------------------------------------------------ usage

    // Usage totals by key (a role or a model), in the order first seen, as Inspect's ModelUsage.
    private sealed class Totals
    {
        private readonly List<string> _keys = [];
        private readonly Dictionary<string, Tally> _tallies = new(StringComparer.Ordinal);

        public int Count => _keys.Count;

        public void Add(string key, JsonObject entry)
        {
            if (!_tallies.TryGetValue(key, out var tally))
            {
                _tallies[key] = tally = new Tally();
                _keys.Add(key);
            }

            tally.Add(entry);
        }

        public JsonObject ToJson()
        {
            var json = new JsonObject();
            foreach (var key in _keys)
            {
                json[key] = _tallies[key].ToJson();
            }

            return json;
        }
    }

    // One ModelUsage: input_tokens, output_tokens, total_tokens (their sum), input_tokens_cache_read,
    // input_tokens_cache_write, reasoning_tokens and total_cost, each present when an entry gave it.
    private sealed class Tally
    {
        private static readonly (string Aef, string Inspect)[] Tokens =
        [
            ("gen_ai.usage.input_tokens", "input_tokens"),
            ("gen_ai.usage.output_tokens", "output_tokens"),
            ("gen_ai.usage.cache_read.input_tokens", "input_tokens_cache_read"),
            ("gen_ai.usage.cache_write.input_tokens", "input_tokens_cache_write"),
            ("gen_ai.usage.reasoning.output_tokens", "reasoning_tokens"),
        ];

        private readonly Dictionary<string, long> _tokens = new(StringComparer.Ordinal);
        private double _cost;
        private bool _hasCost;
        private bool _costIsInteger = true;

        public void Add(JsonObject entry)
        {
            foreach (var (aef, inspect) in Tokens)
            {
                if (Long(entry[aef]) is { } count)
                {
                    _tokens[inspect] = _tokens.GetValueOrDefault(inspect) + count;
                }
            }

            if (entry["costUsd"] is JsonValue cost)
            {
                // As Python adds: integers stay integers, a float makes the sum a float.
                _hasCost = true;
                _costIsInteger &= cost.ToJsonString().AsSpan().IndexOfAny(".eE") < 0;
                _cost += cost.GetValue<double>();
            }
        }

        public JsonObject ToJson()
        {
            var json = new JsonObject();
            foreach (var name in new[] { "input_tokens", "output_tokens" })
            {
                if (_tokens.TryGetValue(name, out var count))
                {
                    json[name] = count;
                }
            }

            if (_tokens.ContainsKey("input_tokens") || _tokens.ContainsKey("output_tokens"))
            {
                json["total_tokens"] = _tokens.GetValueOrDefault("input_tokens") + _tokens.GetValueOrDefault("output_tokens");
            }

            foreach (var name in new[] { "input_tokens_cache_read", "input_tokens_cache_write", "reasoning_tokens" })
            {
                if (_tokens.TryGetValue(name, out var count))
                {
                    json[name] = count;
                }
            }

            if (_hasCost)
            {
                json["total_cost"] = _costIsInteger ? JsonValue.Create((long)_cost) : JsonValue.Create(_cost);
            }

            return json;
        }
    }

    // ------------------------------------------------------------------ JSON

    // A fresh copy of a value read from the run, numbers as the run spells them.
    private static JsonNode? Copy(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static long? Long(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? (long)v.GetValue<double>() : null;

    private static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray array ? array.Select(Str).OfType<string>() : [];

    private static IEnumerable<JsonObject> Objs(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
}
