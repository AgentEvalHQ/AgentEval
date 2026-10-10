// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Inspect;

/// <summary>
/// How <see cref="AefInspectImporter"/> writes the AEF run: what the log does not record (IN-6), taken from the person
/// converting. <see cref="AefConversionOptions.ContentCapture"/> is the run's <c>contentCapture</c> (<c>on</c> unless
/// asked otherwise): with <c>off</c>, no case content, explanation or answer is kept ([RUN-11], IN-7).
/// <see cref="AefConversionOptions.TimeProvider"/> is the conversion time: the seal's <c>sealedAt</c>.
/// </summary>
public sealed record AefInspectImportOptions : AefConversionOptions
{
    /// <summary>run.json <c>execution.targetMode</c>: how the log's solver drove its target ([RUN-7]); Inspect does not say.</summary>
    public required AefTargetMode TargetMode { get; init; }
}

/// <summary>An import refused, naming the rule (contracts/aef/1/interop/inspect.md, "Inspect → AEF", "Refused"); nothing is written.</summary>
public sealed class AefInspectImportException(string message) : Exception(message);

/// <summary>
/// Inspect → AEF (contracts/aef/1/interop/inspect.md, "Inspect → AEF" and its rules settled 10-09): an Inspect
/// <c>EvalLog</c> in <c>.json</c> form as an imported AEF run ([RUN-15]), sealed <c>ingest</c> when the log is closed.
/// <list type="bullet">
/// <item>The run header (IN-6): <c>runId</c> is <c>eval.eval_id</c> (or <c>eval.run_id</c>); the status, times and
/// <c>abortReason</c> the log's, in UTC; the suite <c>suite:&lt;task&gt;</c> at <c>task_version</c>, its trials per
/// case <c>eval.config.epochs</c> and its aggregation <c>epochs_reducer</c>'s; the subject
/// <c>model:&lt;eval.model&gt;</c> (the name encoded as [ENC-13] says); the judges the models of the
/// <c>eval.model_roles</c> roles that map to <c>judge</c>, once each (R7I-16); <c>imported.from</c> <c>inspect_ai</c>
/// and its version; <c>eval.run_id</c>, <c>eval_set_id</c>, <c>dataset</c>, <c>results.headline</c> and an epochs reducer
/// without an AEF value in <c>ext.inspect_ai</c> (R7I-14); the target mode and <c>contentCapture</c> the person's, listed
/// in <c>imported.asserted</c> with the subject.</item>
/// <item>One line per sample and score key (IN-7, IN-8): the key is the path and the metric, its scorer the evaluator;
/// a number is <c>scored</c>, a letter <c>C</c>, <c>I</c>, <c>P</c> or <c>N</c> its state and value with the letter as
/// label, a map one score per member, a list <c>ext</c>; NaN is <c>error</c> (grader or scoring failed) or
/// <c>not_measured</c>; a reason that blames the model makes the line <c>failed</c>; the explanation a reasoning blob,
/// the answer and metadata <c>ext.inspect_ai</c>. A sample's times go on each of its lines, its duration and usage on
/// its first; its content is blobs cited by each line: text as written, anything else serialized by JCS (RFC 8785). A sample that stopped is a line per scorer in <c>error</c> (or
/// <c>not_measured</c> for a limit).</item>
/// <item>With more than one epoch, each line carries <c>trial</c> = <c>epoch</c> − 1, and each reduction gives the
/// case's rollup at its scorer, counted from the epochs' lines.</item>
/// <item>The summary (IN-9): one lane, <c>main</c>; an entry per <c>results.scores</c> entry, its figures computed from
/// the lines, Inspect's <c>stderr</c>, its other metrics in <c>ext.inspect_ai</c>; the run's usage from
/// <c>stats</c>. Every metric is declared <c>kind: score</c>, <c>direction: none</c>, <c>scale: unbounded</c>.</item>
/// </list>
/// Refused, naming the rule, with nothing written: what IN-6 to IN-10 list, a run that does not verify (IN-11), and a
/// conversion time before a closed log's end ([SEAL-1]: a run is sealed after it closes). A <c>started</c> log gives a
/// running run, which is not sealed ([SEAL-1]).
/// </summary>
public static partial class AefInspectImporter
{
    /// <summary>The namespace of <c>ext</c> the converter writes Inspect's own facts under.</summary>
    public const string ExtName = "inspect_ai";

    /// <summary>A line's <c>reason</c> for an unscored value (NaN) without a <c>Score.reason</c> (IN-7).</summary>
    public const string NoReason = "Inspect recorded no value (NaN) and no reason";

    /// <summary>A line's <c>reason</c> for a sample whose error has no message (R8-4, R9-2).</summary>
    public const string NoMessage = "Inspect recorded an error without a message";

    /// <summary>The lane of the summary (IN-9).</summary>
    public const string Lane = "main";

    /// <summary>The run.json fields the converter always supplies (IN-6); <c>startedAt</c> too when the log has no <c>stats.started_at</c>.</summary>
    public static readonly IReadOnlyList<string> Asserted = ["subject.ref", "subject.kind", "execution.targetMode", "contentCapture"];

    private static readonly HashSet<string> BlameTheModel = new(StringComparer.Ordinal) { "refusal", "no_response", "invalid_response_format" };
    private static readonly HashSet<string> BlameTheInstrument = new(StringComparer.Ordinal) { "grader_failed", "scoring_failed" };
    private static readonly HashSet<AefState> Measured = [AefState.Passed, AefState.Failed, AefState.Warn, AefState.Inconclusive, AefState.Scored];

    // The letters (the table): C passed 1, I failed 0, P warn 0.5, N failed 0.
    private static readonly Dictionary<string, (AefState State, double Value)> Letters = new(StringComparer.Ordinal)
    {
        ["C"] = (AefState.Passed, 1),
        ["I"] = (AefState.Failed, 0),
        ["P"] = (AefState.Warn, 0.5),
        ["N"] = (AefState.Failed, 0),
    };

    // An RFC 3339 / ISO 8601 time as Python writes one: the offset is what IN-6 requires.
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?(Z|z|[+-][0-9]{2}:[0-9]{2})?\z")]
    private static partial Regex TimePattern();

    // summary.json aggregate.method (the writer schema): what an Inspect metric name must be to be one (IN-9).
    [GeneratedRegex(@"^[a-z][a-z0-9@._-]{0,63}\z")]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"^(pass_at|at_least)_([0-9]{1,9})\z")]
    private static partial Regex CountedReducer();

    /// <summary>Imports the Inspect log <paramref name="logFile"/> (<c>.json</c> form) into the AEF run folder <paramref name="outputDirectory"/>.</summary>
    /// <param name="logFile">The <c>EvalLog</c> as one JSON document (Inspect's <c>.json</c>, or <c>inspect log dump</c> of an <c>.eval</c>).</param>
    /// <param name="outputDirectory">The AEF run folder to write: it must not exist, or be empty.</param>
    /// <param name="options">What the log does not record, and how to seal.</param>
    /// <exception cref="AefInspectImportException">
    /// The page refuses the log (the message names the rule): every malformed log is refused so, as it is read (IN-6) or
    /// where a rule refuses it.
    /// </exception>
    /// <exception cref="ArgumentException">The output folder is not empty.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static AefConversion Import(string logFile, string outputDirectory, AefInspectImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(logFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        // Read everything, and refuse what the page refuses, before anything is written.
        var log = ReadLog(File.ReadAllBytes(logFile), logFile);
        ImportPlan plan;
        try
        {
            plan = Read(log, options);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException or KeyNotFoundException)
        {
            // A part of the log in a shape no EvalLog has (an object where a list is, a string where an object is): refused
            // rather than thrown, so every malformed log names a rule.
            throw new AefInspectImportException($"{logFile} is not an Inspect EvalLog as the page reads one: {e.Message} (IN-6).");
        }

        var clock = options.TimeProvider ?? TimeProvider.System;
        var now = AefTime.FromDateTimeOffset(clock.GetUtcNow());
        if (plan.EndedAt is { } endedAt && options.Seal && now < endedAt)
        {
            throw new AefInspectImportException(
                $"The conversion time {now} is before the log's end ({endedAt}): the run is sealed at the conversion time, and only a closed run is sealed ([SEAL-1]).");
        }

        var existed = Directory.Exists(outputDirectory);
        AefRunWriter writer;
        try
        {
            writer = AefRunWriter.Create(outputDirectory, plan.Header);
        }
        catch (ArgumentException e) when (e.ParamName != "directory")
        {
            // IN-11: a run the writer refuses is a run that would not verify.
            throw new AefInspectImportException($"The run's header cannot be written as AEF, so nothing is written (IN-11): {e.Message}");
        }

        try
        {
            writer.SetMetrics(plan.Metrics.Select(id => new AefMetric
            {
                Id = id,
                Kind = AefMetricKind.Score,
                Direction = AefMetricDirection.None,
                Scale = AefScale.Unbounded,
            }));
            foreach (var record in plan.Evidence)
            {
                writer.AddEvidence(new AefEvidence
                {
                    EvidenceId = record.Id,
                    Kind = record.Kind,
                    Link = AefEvidenceLink.ToBlob(writer.PutBlob(record.Bytes)),
                    Description = record.Description,
                });
            }

            foreach (var line in plan.Lines)
            {
                writer.AddResult(line.Reasoning is { } reasoning
                    ? line.Result with { Reasoning = writer.PutBlob(Encoding.UTF8.GetBytes(reasoning)) }
                    : line.Result);
            }

            if (plan.Status != AefRunStatus.Running)
            {
                writer.SetSummary(plan.Summary!);
                return AefConverter.Finish(writer, options, plan.From, plan.Header.Imported!.Asserted, plan.Notes, plan.Status, plan.EndedAt!.Value, plan.AbortReason);
            }

            // A started log is a running run: not closed, so not sealed ([SEAL-1]); it is verified all the same (IN-11).
            var verification = AefRunVerifier.Verify(outputDirectory);
            if (verification.Outcome == AefOutcome.Invalid || verification.Problems.Count > 0)
            {
                throw new AefInspectImportException(
                    $"The run written from the log does not verify, so nothing is written (IN-11): {string.Join(", ", verification.Problems.Take(10).Select(p => $"{p.Path} {p.Code}"))}");
            }

            return new AefConversion(writer.Directory, writer.RunId, plan.From, plan.Header.Imported!.Asserted, plan.Notes, verification, null);
        }
        catch (Exception e)
        {
            AefConverter.Discard(outputDirectory, existed);
            if (e is ArgumentException)
            {
                // IN-11: a line or document the writer refuses (a caseId or path too long, a value its schema refuses)
                // would make a run that does not verify.
                throw new AefInspectImportException($"The log cannot be written as AEF, so nothing is written (IN-11): {e.Message}");
            }

            if (e is InvalidOperationException)
            {
                // IN-11: the run written from the log is verified (AefWriteException), and refused when it does not
                // verify or close.
                throw new AefInspectImportException($"The run written from the log does not verify, so nothing is written (IN-11): {e.Message}");
            }

            throw;
        }
    }

    // ------------------------------------------------------------------ reading the log (IN-6, R9-2)

    // The log, read as I-JSON within a depth of 64, a bare NaN allowed only where an unscored value can be (a score's
    // value or a member of a map one, a reduced score's value, a metric's value) and where a rule then refuses it (a
    // limit's number and content that is not text, IN-8). Anything else is refused as the log is read (IN-6).
    private static JsonObject ReadLog(byte[] bytes, string logFile)
    {
        JsonNode? node;
        try
        {
            node = InspectJson.Parse(bytes);
        }
        catch (FormatException e)
        {
            // Not I-JSON (R9-2), Infinity, a bare NaN where a member name stands (R10-3), or any other syntax error.
            throw new AefInspectImportException($"{logFile}: {e.Message}: the log is refused as it is read (IN-6).");
        }

        var log = node as JsonObject ?? throw new AefInspectImportException($"{logFile}: its value is not an object, as an EvalLog is (IN-6).");
        var unscored = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        var content = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        void Score(JsonNode? score)
        {
            if (At(score, "value") is not { } value)
            {
                return;
            }

            unscored.Add(value);
            foreach (var (_, member) in value as JsonObject ?? new JsonObject())
            {
                if (member is not null)
                {
                    unscored.Add(member);
                }
            }
        }

        foreach (var sample in Objects(log["samples"]))
        {
            foreach (var (_, score) in sample["scores"] as JsonObject ?? new JsonObject())
            {
                Score(score);
            }

            if (At(sample["limit"], "limit") is { } limit)
            {
                unscored.Add(limit);   // refused there by IN-8
            }

            foreach (var member in new[] { "input", "target", "output", "messages" })
            {
                if (sample[member] is { } held)
                {
                    content.Add(held);   // refused there by IN-8 when written
                }
            }
        }

        foreach (var reduction in Objects(log["reductions"]))
        {
            foreach (var score in Objects(reduction["samples"]))
            {
                Score(score);
            }
        }

        foreach (var entry in Objects(At(log["results"], "scores")))
        {
            foreach (var (_, metric) in entry["metrics"] as JsonObject ?? new JsonObject())
            {
                if (At(metric, "value") is { } value)
                {
                    unscored.Add(value);
                }
            }
        }

        void Walk(JsonNode? at, string path)
        {
            switch (at)
            {
                case null:
                    return;
                case var _ when content.Contains(at):
                    // Content may hold a NaN value: IN-8 refuses it when the content is written. A bare NaN as a member
                    // name, there as anywhere, was refused by the parser (not JSON).
                    return;
                case JsonObject obj:
                    foreach (var (name, child) in obj)
                    {
                        Walk(child, $"{path}.{name}");
                    }

                    return;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        Walk(array[i], $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]");
                    }

                    return;
                default:
                    if (InspectJson.IsNaN(at) && !unscored.Contains(at))
                    {
                        throw new AefInspectImportException(
                            $"{logFile}: {path} is NaN, where no unscored value can be (a score's value, a reduced score's value, a metric's value): the log is refused as it is read (IN-6).");
                    }

                    return;
            }
        }

        Walk(log, "$");
        return log;
    }

    // A member of an object, or null when the node is no object.
    private static JsonNode? At(JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    // ------------------------------------------------------------------ the plan: everything read and checked

    private sealed record Planned(AefResult Result, string? Reasoning);

    private sealed record PlannedEvidence(string Id, AefEvidenceKind Kind, byte[] Bytes, string Description);

    private sealed record ImportPlan(
        AefRunHeader Header, AefRunStatus Status, AefTime? EndedAt, string? AbortReason, string From,
        List<string> Metrics, List<PlannedEvidence> Evidence, List<Planned> Lines, AefSummary? Summary, List<string> Notes);

    // What one Inspect Score reads as (IN-7).
    private sealed record ScoreRead(AefState State, string? Reason, List<AefScore>? Scores, string? Reasoning, JsonObject? Ext);

    private static ImportPlan Read(JsonObject log, AefInspectImportOptions options)
    {
        var contentOn = options.ContentCapture == AefContentCapture.On;
        var notes = new List<string>
        {
            "The subject's ref and kind (model:<eval.model>), the target mode and contentCapture are the converter's (imported.asserted, IN-6): Inspect's subject is a solver and a model.",
            "Each metric is declared kind score, direction none, scale unbounded: Inspect gives no kind, direction or range (IN-9).",
            "Not carried: working_time, Inspect's events, each sample's metadata, eval.metadata, the plan and the solver.",
        };
        var eval = log["eval"] as JsonObject ?? throw Refuse("IN-6", "The log has no eval object (EvalSpec).");

        // IN-10: post-run edits; the table makes them overlay events, and the converter writes none.
        if (log["log_updates"] is JsonArray { Count: > 0 })
        {
            throw new AefInspectImportException("The log has log_updates: the table makes them overlay annotate events, and the converter writes no overlays (IN-10).");
        }

        // ---- the run header (IN-6)
        var (status, closed) = Text(log["status"]) switch
        {
            "success" => (AefRunStatus.Completed, true),
            "error" or "cancelled" => (AefRunStatus.Aborted, true),
            "started" => (AefRunStatus.Running, false),
            var other => throw Refuse("IN-6", $"The log's status '{other}' is not started, success, cancelled or error."),
        };
        var evalId = Text(eval["eval_id"]);
        var runId = string.IsNullOrEmpty(evalId) ? Text(eval["run_id"]) : evalId;
        if (!AefConverter.IsId(runId))
        {
            throw new AefInspectImportException($"The log's eval_id '{runId}' is not an AEF id (1-128 letters, digits, '.', '_', ':' or '-'), and runId is it (IN-6).");
        }

        var stats = log["stats"] as JsonObject;
        var asserted = Asserted.ToList();
        AefTime startedAt;
        if (Text(stats?["started_at"]) is { Length: > 0 } started)
        {
            startedAt = Time(started, "stats.started_at");
        }
        else
        {
            startedAt = Time(Text(eval["created"]) ?? throw Refuse("IN-6", "The log has neither stats.started_at nor eval.created."), "eval.created");
            asserted.Add("startedAt");
        }

        AefTime? endedAt = null;
        string? abortReason = null;
        if (closed)
        {
            endedAt = Text(stats?["completed_at"]) is { Length: > 0 } completed
                ? Time(completed, "stats.completed_at")
                : throw new AefInspectImportException($"The log is closed ({Text(log["status"])}) and has no stats.completed_at, and endedAt is it (IN-6).");
            if (endedAt < startedAt)
            {
                throw new AefInspectImportException($"The log ends ({endedAt}) before it starts ({startedAt}) (IN-6).");
            }

            abortReason = Text(log["status"]) switch
            {
                "error" => Text(At(log["error"], "message")) is { Length: > 0 } message
                    ? Cut(message)
                    : throw new AefInspectImportException("The log's status is error and its error has no message, and abortReason is it (IN-6)."),
                "cancelled" => "cancelled",
                _ => null,
            };
        }

        var version = Text(At(eval["packages"], ExtName));
        var from = version is { Length: > 0 } ? $"{ExtName} {version}" : ExtName;
        var task = Text(eval["task"]) is { Length: > 0 } t ? t : throw Refuse("IN-6", "The log's eval has no task.");
        var taskVersion = eval["task_version"] switch
        {
            null => "0",   // Inspect's default
            JsonValue v when v.GetValueKind() == JsonValueKind.String && !InspectJson.IsNaN(v) => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => SafeInteger(v)   // an integer of at most 2^53 − 1, in decimal digits (R9-2)
                ?? throw Refuse("IN-6", $"The log's task_version {v.ToJsonString()} is a number that is no integer of at most 2^53 − 1 in magnitude (R9-2)"),
            _ => throw Refuse("IN-6", "The log's task_version is neither a number nor a string"),
        };
        if (!AefConverter.IsExactVersion(taskVersion))
        {
            throw Refuse("IN-6", $"The log's task_version '{taskVersion}' cannot be suite.version (printable ASCII without spaces, at most 128 characters, [ENC-10]).");
        }

        var model = Text(eval["model"]) is { Length: > 0 } m ? m : throw Refuse("IN-6", "The log's eval has no model.");
        var roles = new List<(string Role, string? Model)>();
        foreach (var (role, roleConfig) in eval["model_roles"] as JsonObject ?? new JsonObject())
        {
            roles.Add((role, Text(At(roleConfig, "model")) ?? Text(roleConfig)));
        }

        var scorerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Objects(At(log["results"], "scores")))
        {
            if (Text(entry["name"]) is { } name && Text(entry["scorer"]) is { Length: > 0 } scorer)
            {
                scorerOf.TryAdd(name, scorer);
            }
        }

        // ---- epochs (an integer from 1 to 1000, IN-6, "Integers") and the reducer (IN-8)
        var config = eval["config"] as JsonObject;
        var epochsGiven = Integer(config?["epochs"], "eval.config.epochs", "IN-6", min: 1, max: 1000);

        var epochs = epochsGiven ?? 1;
        (AefTrialAggregation Aggregation, long? K)? runAggregation = null;
        JsonNode? reducerWithoutValue = null;
        if (config?["epochs_reducer"] is JsonArray reducers)
        {
            if (reducers.Count > 1)
            {
                throw new AefInspectImportException($"eval.config.epochs_reducer names {reducers.Count} reducers, and a run has one aggregation (IN-8).");
            }

            if (reducers.Count == 1)
            {
                var name = Text(reducers[0]) ?? throw Refuse("IN-8", "eval.config.epochs_reducer holds a value that is not a reducer's name.");
                runAggregation = Reducer(name, epochs);
                if (runAggregation is null)
                {
                    // R7I-14: left out of executionPolicy and kept in ext; with more than one epoch, a reduction that uses
                    // it is refused (IN-8).
                    reducerWithoutValue = reducers;
                }
            }
        }

        var header = new AefRunHeader
        {
            RunId = runId!,
            Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = new AefSubject { Ref = AefConverter.TypedRef("model", model), Kind = AefSubjectKind.Model },
            Suite = new AefSuite
            {
                Ref = AefConverter.TypedRef("suite", task),
                Version = taskVersion,
                ExecutionPolicy = epochsGiven is { } given
                    ? new AefExecutionPolicy { TrialsPerCase = (int)given, Aggregation = runAggregation?.Aggregation, K = runAggregation?.K }
                    : null,
            },
            // R7I-16: each model of a role IN-8 maps to judge, once, in the log's order (agent and attacker grade nothing).
            Judges = roles.Where(r => Role(r.Role, roles) == AefUsageRole.Judge).Select(r => r.Model).OfType<string>().Distinct(StringComparer.Ordinal)
                .Select(j => new AefJudge { Model = j }).ToList() is { Count: > 0 } judges ? judges : null,
            StartedAt = startedAt,
            ContentCapture = options.ContentCapture,   // IN-6: the converter's choice, on unless asked otherwise
            Ext = Ext(eval, log, reducerWithoutValue),
            Execution = new AefExecution { TargetMode = options.TargetMode },
            Imported = new AefImported { From = from, Asserted = asserted },
        };

        // ---- the samples (IN-7, IN-8)
        var lines = new List<Planned>();
        var evidence = new List<PlannedEvidence>();
        var evidenceOf = new Dictionary<(AefEvidenceKind, string), string>();
        var trialStates = new Dictionary<(string Case, string Path), List<AefState>>();
        var seen = new HashSet<(string Case, string Path, long Epoch)>();
        var scorers = Objects(eval["scorers"]).Select(s => Text(s["name"])).OfType<string>().ToList();
        var samples = log["samples"] as JsonArray ?? new JsonArray();
        for (var i = 0; i < samples.Count; i++)
        {
            var where = $"samples[{i.ToString(CultureInfo.InvariantCulture)}]";
            var sample = samples[i] as JsonObject ?? throw Refuse("IN-8", $"{where} is not an object.");
            if (sample["invalidation"] is not null)
            {
                throw new AefInspectImportException($"{where} has an invalidation: the table makes it an overlay annotate event, and the converter writes no overlays (IN-10).");
            }

            var caseId = CaseId(sample["id"], $"{where}.id");
            var epoch = Integer(sample["epoch"], $"{where}.epoch", "IN-8", min: 1) ?? 1;   // 1 to epochs ("Integers")
            if (epoch > epochs)
            {
                throw new AefInspectImportException($"{where}: epoch {epoch} is beyond eval.config.epochs ({epochs}) (IN-8).");
            }

            int? trial = epochs > 1 ? (int)epoch - 1 : null;
            var sampleStarted = Text(sample["started_at"]) is { Length: > 0 } s1 ? Time(s1, $"{where}.started_at") : (AefTime?)null;
            var sampleEnded = Text(sample["completed_at"]) is { Length: > 0 } s2 ? Time(s2, $"{where}.completed_at") : (AefTime?)null;
            double? duration = sample["total_time"] is JsonValue time && time.GetValueKind() == JsonValueKind.Number ? time.GetValue<double>() * 1000 : null;

            // The sample's lines: a score per key; a sample that stopped, a line per key (or per scorer) in error.
            var reads = new List<(string Key, ScoreRead Read)>();
            var scores = sample["scores"] as JsonObject;

            // An error that is present and not null is an object (R9-2): its message, cut to its first 4096 characters
            // with no mark (R7I-12), or, absent, null or empty, "Inspect recorded an error without a message"; a message
            // that is not text, or an error that is not an object, is refused (IN-8).
            string? errorReason = null;
            if (sample["error"] is { } error)
            {
                errorReason = (error as JsonObject ?? throw Refuse("IN-8", $"{where}.error is {error.ToJsonString()}, not an object as Inspect writes one (R9-2)"))["message"] switch
                {
                    null => NoMessage,
                    JsonValue said when said.GetValueKind() == JsonValueKind.String && !InspectJson.IsNaN(said) => said.GetValue<string>() is { Length: > 0 } message ? Cut(message) : NoMessage,
                    _ => throw Refuse("IN-8", $"{where}.error.message is not text (R9-2)"),
                };
            }

            // A limit that is present and not null is {type, limit}, a type and a finite number, on a scored sample too
            // (R8-4, R9-2): "<type> limit <limit>", the number written from its binary64 value alone; any other is refused (IN-8).
            string? limitReason = null;
            if (sample["limit"] is { } limit)
            {
                limitReason = limit is JsonObject l && Text(l["type"]) is { Length: > 0 } type && l["limit"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                    ? Cut($"{type} limit {InspectJson.Shortest(value.GetValue<double>())}")
                    : throw Refuse("IN-8", $"{where}.limit is {InspectJson.Indented(limit).ReplaceLineEndings(" ")}: a limit is {{type, limit}} with a type and a finite number, and the line's reason is \"<type> limit <limit>\" (R8-4, R9-2)");
            }

            if ((errorReason ?? limitReason) is { } reason)
            {
                var keys = scores is { Count: > 0 } ? scores.Select(k => k.Key).ToList() : scorers;
                if (keys.Count == 0)
                {
                    throw new AefInspectImportException($"{where} stopped before it was scored, and the log names no scorer (eval.scorers) to give it a line per scorer (IN-8).");
                }

                foreach (var (_, score) in scores ?? new JsonObject())
                {
                    NoHistory(score as JsonObject, where);
                }

                // A sample with an error puts its lines in error; one stopped by a limit, in not_measured.
                var state = errorReason is not null ? AefState.Error : AefState.NotMeasured;
                reads.AddRange(keys.Select(k => (k, new ScoreRead(state, reason, null, null, null))));
            }
            else
            {
                if (scores is not { Count: > 0 })
                {
                    throw new AefInspectImportException($"{where} has no scores, and a line is a score (IN-8).");
                }

                foreach (var (key, score) in scores)
                {
                    reads.Add((key, ReadScore(key, score as JsonObject, $"{where}.scores.{key}", contentOn, notes)));
                }
            }

            foreach (var (key, _) in reads)
            {
                if (!seen.Add((caseId, key, epoch)))
                {
                    throw new AefInspectImportException($"{where}: a second score of case '{caseId}' at '{key}' in epoch {epoch}: the two lines would have one result id (IN-8).");
                }
            }

            // The sample's content, as blobs cited by each of its lines (contentCapture on).
            var cited = new List<string>();
            if (contentOn)
            {
                foreach (var (member, kind, field) in new[]
                         {
                             ("input", AefEvidenceKind.Input, "input"), ("target", AefEvidenceKind.Expected, "target"),
                             ("output", AefEvidenceKind.Output, "output"), ("messages", AefEvidenceKind.Transcript, "messages"),
                         })
                {
                    if (Content(sample[member], member, where) is not { } bytes)
                    {
                        continue;
                    }

                    var key = (kind, AefConverter.Sha256Of(bytes));
                    if (!evidenceOf.TryGetValue(key, out var id))
                    {
                        evidenceOf[key] = id = $"E-{(evidence.Count + 1).ToString(CultureInfo.InvariantCulture)}";
                        evidence.Add(new PlannedEvidence(id, kind, bytes, $"Inspect samples[].{field}"));
                    }

                    cited.Add(id);
                }
            }

            var usage = Usage(sample, roles, model, where);
            var first = true;
            foreach (var (key, read) in reads)
            {
                lines.Add(new Planned(new AefResult
                {
                    CaseId = caseId,
                    Path = key,
                    Trial = trial,
                    Evaluator = new AefEvaluator(scorerOf.GetValueOrDefault(key) ?? key),
                    State = read.State,
                    Reason = read.Reason,
                    Scores = read.Scores,
                    Ext = read.Ext,
                    StartedAt = sampleStarted,
                    EndedAt = sampleEnded,
                    DurationMs = first ? duration : null,
                    Usage = first && usage.Count > 0 ? usage : null,
                    Evidence = cited.Count > 0 ? cited : null,
                }, read.Reasoning));
                first = false;
                if (trial is not null)
                {
                    if (!trialStates.TryGetValue((caseId, key), out var states))
                    {
                        trialStates[(caseId, key)] = states = [];
                    }

                    states.Add(read.State);
                }
            }
        }

        // ---- the reductions: a rollup per case and scorer (IN-8, [RES-8])
        if (epochs > 1)
        {
            var rollups = new HashSet<(string Case, string Path)>();
            var reductions = log["reductions"] as JsonArray ?? new JsonArray();
            for (var r = 0; r < reductions.Count; r++)
            {
                var where = $"reductions[{r.ToString(CultureInfo.InvariantCulture)}]";
                var reduction = reductions[r] as JsonObject ?? throw Refuse("IN-8", $"{where} is not an object.");
                var scorer = Text(reduction["scorer"]) is { Length: > 0 } sc ? sc : throw Refuse("IN-8", $"{where} has no scorer.");
                var reducerName = Text(reduction["reducer"]) ?? throw Refuse("IN-8", $"{where} has no reducer.");
                var aggregation = Reducer(reducerName, epochs)
                                  ?? throw new AefInspectImportException($"{where}: the reducer {reducerName} has no AEF value (IN-8).");
                var reduced = reduction["samples"] as JsonArray ?? new JsonArray();
                for (var j = 0; j < reduced.Count; j++)
                {
                    var at = $"{where}.samples[{j.ToString(CultureInfo.InvariantCulture)}]";
                    var score = reduced[j] as JsonObject ?? throw Refuse("IN-8", $"{at} is not an object.");
                    var caseId = CaseId(score["sample_id"], $"{at}.sample_id");
                    if (!trialStates.TryGetValue((caseId, scorer), out var states))
                    {
                        throw new AefInspectImportException($"{at}: a reduction of case '{caseId}' at '{scorer}' with no epoch lines (IN-8, [RES-8]).");
                    }

                    if (!rollups.Add((caseId, scorer)))
                    {
                        throw new AefInspectImportException($"{at}: a second reduction of case '{caseId}' at '{scorer}': a case has one rollup per path (IN-8, [RES-8]).");
                    }

                    var read = ReadScore(scorer, score, at, contentOn, notes);
                    lines.Add(new Planned(new AefResult
                    {
                        CaseId = caseId,
                        Path = scorer,
                        Trials = new AefTrials(states.Count, states.Count(s => s == AefState.Passed), aggregation.Aggregation, states.Distinct().Count() == 1, aggregation.K),
                        Evaluator = new AefEvaluator(scorerOf.GetValueOrDefault(scorer) ?? scorer),
                        State = read.State,
                        Reason = read.Reason,
                        Scores = read.Scores,
                        Ext = read.Ext,
                    }, read.Reasoning));
                }
            }

            if (closed && trialStates.Keys.FirstOrDefault(k => !rollups.Contains(k)) is { Case: { } missingCase } missing)
            {
                throw new AefInspectImportException(
                    $"Case '{missingCase}' has epoch lines at '{missing.Path}' and no reduction there, and a closed run has a rollup per case and path run in trials (IN-8, [RES-8]).");
            }
        }
        else if (log["reductions"] is JsonArray { Count: > 0 })
        {
            notes.Add("The log has reductions, but one epoch: each case's line is its result, and the reductions are not read.");
        }

        // ---- the summary (IN-9) and the metrics
        var metrics = new List<string>();
        foreach (var score in lines.SelectMany(l => l.Result.Scores ?? []))
        {
            if (!metrics.Contains(score.Metric, StringComparer.Ordinal))
            {
                metrics.Add(score.Metric);
            }
        }

        AefSummary? summary = null;
        if (closed)
        {
            summary = Summary(log, stats, lines, roles, model, metrics);
        }

        return new ImportPlan(header, status, endedAt, abortReason, from, metrics, evidence, lines, summary, notes);
    }

    // run.json ext."inspect_ai": eval.run_id, eval.eval_set_id, eval.dataset, results.headline and an epochs reducer without
    // an AEF value (R7I-14), as written.
    private static JsonObject? Ext(JsonObject eval, JsonObject log, JsonNode? reducerWithoutValue)
    {
        var ext = new JsonObject();
        foreach (var (name, value) in new[]
                 {
                     ("run_id", eval["run_id"]), ("eval_set_id", eval["eval_set_id"]), ("dataset", eval["dataset"]),
                     ("headline", At(log["results"], "headline")), ("epochs_reducer", reducerWithoutValue),
                 })
        {
            if (value is not null)
            {
                ext[name] = Copy(value, name);
            }
        }

        return ext.Count > 0 ? new JsonObject { [ExtName] = ext } : null;
    }

    // ------------------------------------------------------------------ one score (IN-7)

    private static ScoreRead ReadScore(string key, JsonObject? score, string where, bool contentOn, List<string> notes)
    {
        if (score is null)
        {
            throw Refuse("IN-7", $"{where} is not a Score object.");
        }

        NoHistory(score, where);
        var value = score["value"];
        var reason = Text(score["reason"]);
        AefState state;
        List<AefScore>? scores = null;
        var ext = new JsonObject();
        if (InspectJson.IsNaN(value))
        {
            // Unscored: grader_failed or scoring_failed is error; any other reason, or none, not_measured.
            state = reason is not null && BlameTheInstrument.Contains(reason) ? AefState.Error : AefState.NotMeasured;
            reason ??= NoReason;
        }
        else
        {
            switch (value)
            {
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                    state = AefState.Scored;   // Inspect sets no pass threshold
                    scores = [new AefScore { Metric = key, Value = v.GetValue<double>() }];
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    var letter = v.GetValue<string>();
                    if (!Letters.TryGetValue(letter, out var row))
                    {
                        throw new AefInspectImportException($"{where}: the value '{letter}' is a string other than C, I, P and N (IN-7).");
                    }

                    state = row.State;
                    scores = [new AefScore { Metric = key, Value = row.Value, Label = letter }];
                    break;
                case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                    throw new AefInspectImportException($"{where}: the value {v.ToJsonString()} is a boolean (IN-7).");
                case JsonObject map:
                    // One score per member: a number as its value, a letter as the value its row gives with the letter as label.
                    state = AefState.Scored;
                    scores = [];
                    foreach (var (member, item) in map)
                    {
                        if (InspectJson.IsNaN(item))
                        {
                            notes.Add($"{where}: the member {member} is NaN, and a score's value is a number: it has no score.");
                            continue;
                        }

                        scores.Add(item switch
                        {
                            JsonValue n when n.GetValueKind() == JsonValueKind.Number && !InspectJson.IsNaN(n) => new AefScore { Metric = member, Value = n.GetValue<double>() },
                            JsonValue s when s.GetValueKind() == JsonValueKind.String && Letters.TryGetValue(s.GetValue<string>(), out var r) =>
                                new AefScore { Metric = member, Value = r.Value, Label = s.GetValue<string>() },
                            JsonValue s when s.GetValueKind() == JsonValueKind.String && !InspectJson.IsNaN(s) =>
                                throw new AefInspectImportException($"{where}: the member {member} is '{s.GetValue<string>()}', a string other than C, I, P and N (IN-7)."),
                            JsonValue b when b.GetValueKind() is JsonValueKind.True or JsonValueKind.False =>
                                throw new AefInspectImportException($"{where}: the member {member} is a boolean (IN-7)."),
                            _ => throw Refuse("IN-7", $"{where}: the member {member} is not a number or a string."),
                        });
                    }

                    if (scores.Count == 0)
                    {
                        scores = null;
                    }

                    break;
                case JsonArray list:
                    // A list goes to the line's ext, and the line is scored without a score.
                    state = AefState.Scored;
                    ext["value"] = Copy(list, $"{where}.value");
                    break;
                default:
                    throw Refuse("IN-7", $"{where}: the value is not a number, a string, a map or a list.");
            }
        }

        // The three reasons that blame the model make the line failed, whatever its value.
        if (reason is not null && BlameTheModel.Contains(reason))
        {
            state = AefState.Failed;
        }

        string? reasoning = null;
        if (contentOn)
        {
            if (Text(score["explanation"]) is { Length: > 0 } explanation)
            {
                reasoning = explanation;
            }

            if (score["answer"] is { } answer)
            {
                ext["answer"] = Copy(answer, $"{where}.answer");
            }
        }

        if (score["metadata"] is { } metadata)
        {
            ext["metadata"] = Copy(metadata, $"{where}.metadata");
        }

        // Answer, metadata, then a list value, in the page's order.
        var ordered = new JsonObject();
        foreach (var name in new[] { "answer", "metadata", "value" })
        {
            if (ext[name] is { } member)
            {
                ext.Remove(name);
                ordered[name] = member;
            }
        }

        return new ScoreRead(state, reason is null ? null : Cut(reason), scores, reasoning,
            ordered.Count > 0 ? new JsonObject { [ExtName] = ordered } : null);
    }

    private static void NoHistory(JsonObject? score, string where)
    {
        if (score?["history"] is JsonArray { Count: > 0 })
        {
            throw new AefInspectImportException($"{where} has Score.history edits: the table makes them overlay override events, and the converter writes no overlays (IN-10).");
        }
    }

    // The reducer's AEF value (the table): majority and mode → MajorityVote, mean → Mean, median → Median, max → Max,
    // pass_at_<k> → PassAtK with k, at_least_1 → AnyPass, at_least_<n> with n = epochs → AllPass; anything else none.
    private static (AefTrialAggregation Aggregation, long? K)? Reducer(string name, long epochs)
    {
        switch (name)
        {
            case "majority" or "mode":
                return (AefTrialAggregation.MajorityVote, null);
            case "mean":
                return (AefTrialAggregation.Mean, null);
            case "median":
                return (AefTrialAggregation.Median, null);
            case "max":
                return (AefTrialAggregation.Max, null);
        }

        var m = CountedReducer().Match(name);
        if (!m.Success)
        {
            return null;
        }

        var k = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value switch
        {
            "pass_at" when k >= 1 => (AefTrialAggregation.PassAtK, k),
            "at_least" when k == 1 => (AefTrialAggregation.AnyPass, null),
            "at_least" when k == epochs => (AefTrialAggregation.AllPass, null),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ usage (IN-8, SUM-7)

    // A sample's usage: one entry per role_usage role (agent, judge and attacker as they are, a role of eval.model_roles as
    // judge, any other as other), with the role's model when the sample's model_usage has it; and an agent entry from
    // model_usage under eval.model when no role is the agent. Two entries with one role and model are added together.
    private static List<AefUsage> Usage(JsonObject sample, List<(string Role, string? Model)> roles, string evalModel, string where)
    {
        var modelUsage = sample["model_usage"] as JsonObject;
        var entries = new List<AefUsage>();
        var agent = false;
        foreach (var (role, usage) in sample["role_usage"] as JsonObject ?? new JsonObject())
        {
            var aefRole = Role(role, roles);
            agent |= aefRole == AefUsageRole.Agent;
            var roleModel = role == "agent" ? evalModel : roles.FirstOrDefault(r => r.Role == role).Model;
            var model = roleModel is not null && modelUsage?.ContainsKey(roleModel) == true ? roleModel : null;
            entries.Add(UsageOf(usage as JsonObject, aefRole, model, $"{where}.role_usage.{role}", "IN-8"));
        }

        if (!agent && modelUsage?[evalModel] is JsonObject own)
        {
            entries.Add(UsageOf(own, AefUsageRole.Agent, evalModel, $"{where}.model_usage", "IN-8"));
        }

        return Merge(entries);
    }

    // The run's usage (summary.json, SUM-7): one entry per stats.model_usage model, its role agent for eval.model, else
    // the eval.model_roles role that names it (as a sample's roles are named), else other; without model_usage, one per
    // stats.role_usage role.
    private static List<AefUsage> RunUsage(JsonObject? stats, List<(string Role, string? Model)> roles, string evalModel)
    {
        var entries = new List<AefUsage>();
        if (stats?["model_usage"] is JsonObject { Count: > 0 } byModel)
        {
            foreach (var (model, usage) in byModel)
            {
                var role = model == evalModel ? AefUsageRole.Agent
                    : roles.FirstOrDefault(r => r.Model == model) is { Role: { } named } ? Role(named, roles)
                    : AefUsageRole.Other;
                entries.Add(UsageOf(usage as JsonObject, role, model, $"stats.model_usage.{model}", "IN-9"));
            }
        }
        else
        {
            // Only without model_usage: one entry per role_usage role, without a model (R7I-10).
            foreach (var (role, usage) in stats?["role_usage"] as JsonObject ?? new JsonObject())
            {
                entries.Add(UsageOf(usage as JsonObject, Role(role, roles), null, $"stats.role_usage.{role}", "IN-9"));
            }
        }

        return Merge(entries);
    }

    private static AefUsageRole Role(string role, List<(string Role, string? Model)> roles) => role switch
    {
        "agent" => AefUsageRole.Agent,
        "judge" => AefUsageRole.Judge,
        "attacker" => AefUsageRole.Attacker,
        _ when roles.Any(r => r.Role == role) => AefUsageRole.Judge,
        _ => AefUsageRole.Other,
    };

    // A ModelUsage as an AEF usage entry: tokens under gen_ai.usage.* names, total_cost as costUsd; total_tokens is read
    // and not kept. Every token count is an integer of at least 0 ("Integers"): a sample's under IN-8, the run's under IN-9.
    private static AefUsage UsageOf(JsonObject? usage, AefUsageRole role, string? model, string where, string rule)
    {
        if (usage is null)
        {
            throw Refuse(rule, $"{where} is not a ModelUsage object.");
        }

        long? Tokens(string name) => Integer(usage[name], $"{where}.{name}", rule, min: 0);
        Tokens("total_tokens");
        return new AefUsage
        {
            Role = role,
            Model = model,
            InputTokens = Tokens("input_tokens"),
            OutputTokens = Tokens("output_tokens"),
            CacheReadInputTokens = Tokens("input_tokens_cache_read"),
            CacheWriteInputTokens = Tokens("input_tokens_cache_write"),
            ReasoningOutputTokens = Tokens("reasoning_tokens"),
            CostUsd = usage["total_cost"] is JsonValue cost && !InspectJson.IsNaN(cost) && cost.GetValueKind() == JsonValueKind.Number ? cost.GetValue<double>() : null,
        };
    }

    private static List<AefUsage> Merge(List<AefUsage> entries)
    {
        var merged = new List<AefUsage>();
        foreach (var entry in entries)
        {
            var at = merged.FindIndex(u => u.Role == entry.Role && u.Model == entry.Model);
            if (at < 0)
            {
                merged.Add(entry);
                continue;
            }

            var u = merged[at];
            static long? Add(long? a, long? b) => a is null ? b : b is null ? a : a + b;
            merged[at] = u with
            {
                InputTokens = Add(u.InputTokens, entry.InputTokens),
                OutputTokens = Add(u.OutputTokens, entry.OutputTokens),
                CacheReadInputTokens = Add(u.CacheReadInputTokens, entry.CacheReadInputTokens),
                CacheWriteInputTokens = Add(u.CacheWriteInputTokens, entry.CacheWriteInputTokens),
                ReasoningOutputTokens = Add(u.ReasoningOutputTokens, entry.ReasoningOutputTokens),
                CostUsd = u.CostUsd is null ? entry.CostUsd : entry.CostUsd is null ? u.CostUsd : u.CostUsd + entry.CostUsd,
            };
        }

        return merged;
    }

    // ------------------------------------------------------------------ the summary (IN-9)

    private static AefSummary Summary(JsonObject log, JsonObject? stats, List<Planned> lines, List<(string Role, string? Model)> roles, string evalModel, List<string> metrics)
    {
        var entries = new List<AefSummaryEntry>();
        var others = new JsonObject();
        var scores = Objects(At(log["results"], "scores")).ToList();
        for (var i = 0; i < scores.Count; i++)
        {
            var where = $"results.scores[{i.ToString(CultureInfo.InvariantCulture)}]";
            var entry = scores[i];
            var name = Text(entry["name"]) is { Length: > 0 } n ? n : throw Refuse("IN-9", $"{where} has no name.");
            var all = (entry["metrics"] as JsonObject ?? new JsonObject()).Select(m => (Name: m.Key, Metric: m.Value as JsonObject ?? new JsonObject())).ToList();
            var accuracy = all.FirstOrDefault(m => m.Name == "accuracy").Metric;
            var mean = all.FirstOrDefault(m => m.Name == "mean").Metric;
            if (accuracy is not null && mean is not null)
            {
                throw new AefInspectImportException($"{where} ({name}) has both accuracy and mean, and a summary entry has one mean (IN-9).");
            }

            var stderr = all.FirstOrDefault(m => m.Name == "stderr").Metric?["value"] is JsonValue se && se.GetValueKind() == JsonValueKind.Number && !InspectJson.IsNaN(se)
                ? se.GetValue<double>()
                : (double?)null;
            var rest = all.Where(m => m.Name is not ("accuracy" or "mean" or "stderr")).ToList();
            var figures = Figures(lines, name);
            AefAggregate? aggregate = null;
            double? producerValue = null;
            if ((accuracy ?? mean) is { } theMean)
            {
                Check(theMean["value"], figures.Count == 0 ? null : figures.Average(), name, accuracy is not null ? "accuracy" : "mean", where);
                if (rest.Count > 0)
                {
                    var kept = new JsonObject();
                    foreach (var (metric, value) in rest)
                    {
                        kept[metric] = Copy(value["value"], $"{where}.metrics.{metric}");
                    }

                    others[name] = kept;
                }
            }
            else if (rest.Count > 1)
            {
                throw new AefInspectImportException($"{where} ({name}) has no mean and {rest.Count} other metrics ({string.Join(", ", rest.Select(r => r.Name))}), and an entry has one aggregate (IN-9).");
            }
            else if (rest.Count == 1)
            {
                var (method, metric) = rest[0];
                if (!MethodPattern().IsMatch(method))
                {
                    throw new AefInspectImportException($"{where} ({name}): its metric {method} cannot be an aggregate method (a lower-case letter, then lower-case letters, digits and @ . _ -, at most 64) (IN-9).");
                }

                aggregate = new AefAggregate(method, Integer(At(metric["params"], "k"), $"{where}.metrics.{method}.params.k", "IN-9", min: 1));
                if (method is "median" or "min" or "max")
                {
                    Check(metric["value"], figures.Count == 0 ? null : Aggregate(method, figures), name, method, where);
                }
                else if (figures.Count > 0)
                {
                    producerValue = metric["value"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.Number && !InspectJson.IsNaN(pv)
                        ? pv.GetValue<double>()
                        : throw Refuse("IN-9", $"{where} ({name}): its metric {method} has no finite value, and its lines are measured ([SUM-8]).");
                }
            }

            if (!metrics.Contains(name, StringComparer.Ordinal))
            {
                metrics.Add(name);
            }

            entries.Add(new AefSummaryEntry
            {
                Metric = name,
                Path = name,
                Aggregate = aggregate,
                // No pass rule for a metric: measured, no rule applied (SUM-6); Inspect's stderr.
                Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Scored, Value: producerValue, Stderr: stderr),
            });
        }

        var usage = RunUsage(stats, roles, evalModel);
        var costs = usage.Select(u => u.CostUsd).OfType<double>().ToList();
        return new AefSummary
        {
            Lanes = entries.Count > 0 ? [new AefSummaryLane(Lane, entries)] : [],   // no entry, no lane (aef-inspect-edges)
            Usage = usage.Count > 0 ? usage : null,
            Cost = costs.Count > 0 ? new AefCost(costs.Aggregate(0.0, (a, b) => a + b)) : null,
            Ext = others.Count > 0 ? new JsonObject { [ExtName] = new JsonObject { ["metrics"] = others } } : null,
        };
    }

    // The measured values of the lines a summary entry at <name> for the metric <name> reads ([SUM-3], [SUM-4], kind score):
    // the case's result at the path (a rollup when the case ran in trials), measured when its state is and it scores the
    // metric.
    private static List<double> Figures(List<Planned> lines, string name) =>
        [.. lines.Select(l => l.Result)
            .Where(r => r.Path == name && r.Trial is null && Measured.Contains(r.State))
            .Select(r => r.Scores?.FirstOrDefault(s => s.Metric == name))
            .OfType<AefScore>()
            .Select(s => s.Value)];

    private static double Aggregate(string method, List<double> values)
    {
        var sorted = values.Order().ToList();
        return method switch
        {
            "min" => sorted[0],
            "max" => sorted[^1],
            _ => sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2,
        };
    }

    // IN-9: a mean, median, minimum or maximum Inspect gives that the lines do not is refused (compared as a verifier
    // compares a summary value, within 1e-9 × max(1, |recomputed|), [SUM-5]).
    private static void Check(JsonNode? given, double? computed, string name, string metric, string where)
    {
        if (given is null)
        {
            return;
        }

        var differs = InspectJson.IsNaN(given)
            ? computed is not null
            : given is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || computed is null
              || Math.Abs(v.GetValue<double>() - computed.Value) > 1e-9 * Math.Max(1, Math.Abs(computed.Value));
        if (differs)
        {
            throw new AefInspectImportException(
                $"{where} ({name}): Inspect's {metric} is {InspectJson.Indented(given)}, and the lines give {(computed is { } c ? InspectJson.Shortest(c) : "none (nothing measured)")} (IN-9).");
        }
    }

    // ------------------------------------------------------------------ reading values

    // A time with an offset, in UTC at the precision written (IN-6: a time without an offset is refused).
    private static AefTime Time(string text, string where)
    {
        var m = TimePattern().Match(text);
        if (!m.Success)
        {
            throw Refuse("IN-6", $"{where}: '{text}' is not an ISO 8601 time.");
        }

        if (!m.Groups[8].Success)
        {
            throw new AefInspectImportException($"{where}: the time '{text}' has no offset, so its instant is unknown (IN-6).");
        }

        int Part(int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
        var zone = m.Groups[8].Value;
        var offset = zone is "Z" or "z" ? TimeSpan.Zero
            : (zone[0] == '-' ? -1 : 1) * new TimeSpan(int.Parse(zone[1..3], CultureInfo.InvariantCulture), int.Parse(zone[4..6], CultureInfo.InvariantCulture), 0);
        try
        {
            var whole = new DateTimeOffset(Part(1), Part(2), Part(3), Part(4), Part(5), Part(6), offset);
            var time = new AefTime(whole.ToUnixTimeSeconds(), m.Groups[7].Success ? int.Parse(m.Groups[7].Value.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0);
            return time.IsValid ? time : throw Refuse("IN-6", $"{where}: '{text}' is outside the years 0001 to 9999 ([ENC-8]).");
        }
        catch (ArgumentException)
        {
            throw Refuse("IN-6", $"{where}: '{text}' is not a time that exists.");
        }
    }

    // A refusal naming its rule.
    private static AefInspectImportException Refuse(string rule, string message) => new($"{message.TrimEnd('.')} ({rule}).");

    // A number of the log read as its binary64 value ([ENC-4]) that is an integer of at most 2^53 − 1 in magnitude, in
    // decimal digits (1, 1.0 and 1e0 alike); null for any other number (R9-2).
    private static string? SafeInteger(JsonValue number)
    {
        var value = number.GetValue<double>();
        return Math.Floor(value) == value && Math.Abs(value) <= 9_007_199_254_740_991
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    // A message or reason cut to its first 4096 characters (code points, as JSON Schema counts them), with no mark (R7I-12).
    private static string Cut(string text)
    {
        const int max = 4096;
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

    // A sample id (or a reduction's sample_id) as a caseId: a string as written; a number only when it is an integer of at
    // most 2^53 − 1 in magnitude, in decimal digits (R9-2); anything else refused (IN-8).
    private static string CaseId(JsonNode? id, string where)
    {
        var text = id switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String && !InspectJson.IsNaN(v) => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => SafeInteger(v)
                ?? throw Refuse("IN-8", $"{where} is {v.ToJsonString()}, a number that is no integer of at most 2^53 − 1 in magnitude, so it gives no caseId (R9-2)"),
            _ => throw Refuse("IN-8", $"{where} is neither an integer nor a string"),
        };
        return AefConverter.IsResultText(text, 256)
            ? text
            : throw Refuse("IN-8", $"{where}: '{text}' cannot be a caseId (1-256 characters, no control character, [RES-4])");
    }

    // A sample's input, target, output (when it has choices) or messages as a blob: text as written, anything else (a list
    // of messages, a ModelOutput, a list of targets) serialized by JCS, RFC 8785 ("Content that is not text", IN-8); none
    // when empty. Content JCS cannot write (NaN, an unpaired surrogate) is refused (IN-8).
    private static byte[]? Content(JsonNode? value, string member, string where)
    {
        if (InspectJson.IsNaN(value))
        {
            throw new AefInspectImportException($"{where}.{member} is NaN, which JCS cannot write: content that is not text holding NaN is refused (IN-8).");
        }

        if (value is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            try
            {
                return text.GetValue<string>() is { Length: > 0 } written ? Encoding.UTF8.GetBytes(written) : null;   // text, as written
            }
            catch (InvalidOperationException e)
            {
                throw Refuse("IN-8", $"{where}.{member} is not text that UTF-8 can hold: {e.Message}");
            }
        }

        var json = value switch
        {
            JsonObject o when member == "output" => o["choices"] is JsonArray { Count: > 0 } ? o : null,
            JsonArray { Count: 0 } or JsonObject { Count: 0 } => null,
            _ => value,
        };
        if (json is null)
        {
            return null;
        }

        try
        {
            return InspectJson.Canonical(json);
        }
        catch (FormatException e)
        {
            throw new AefInspectImportException($"{where}.{member}: {e.Message}: content that is not text holding NaN or an unpaired surrogate is refused (IN-8).");
        }
    }

    // An integer of the log ("Integers", settled 10-10): read by its binary64 value alone ([ENC-4]: 2, 2.0 and 2e0 alike),
    // of at most 2^53 − 1 in magnitude and within the field's range; anything else refused, naming the field's rule.
    private static long? Integer(JsonNode? node, string where, string rule, long min = long.MinValue, long max = long.MaxValue)
    {
        if (node is null)
        {
            return null;
        }

        if (InspectJson.IsNaN(node) || node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || SafeInteger(v) is not { } text)
        {
            throw Refuse(rule, $"{where} is not an integer of at most 2^53 − 1 in magnitude");
        }

        var value = long.Parse(text, CultureInfo.InvariantCulture);
        return value >= min && value <= max
            ? value
            : throw Refuse(rule, $"{where} is {value.ToString(CultureInfo.InvariantCulture)}, outside {(max == long.MaxValue ? $"its range (at least {min.ToString(CultureInfo.InvariantCulture)})" : $"{min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}")}");
    }

    // A value of the log copied into AEF: AEF numbers are finite ([ENC-3]).
    private static JsonNode? Copy(JsonNode? node, string where)
    {
        if (InspectJson.HoldsNaN(node))
        {
            throw Refuse("IN-7", $"{where} holds NaN or an infinity, and AEF numbers are finite ([ENC-3]).");
        }

        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && !InspectJson.IsNaN(v) ? v.GetValue<string>() : null;

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
}
