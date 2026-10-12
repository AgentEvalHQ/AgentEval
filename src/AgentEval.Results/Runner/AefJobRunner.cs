// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Runner;

// The job engine is extracted before introducing asynchronous target/suite/case adapters. The scripted public
// entry point remains stable; the pre-refactor conformance snapshots pin every byte written by this engine.
internal static class AefJobRunner
{
    private const string EventsFile = "events.ndjson";
    private const string RunsFolder = "runs";
    private const string CasePath = AefScriptedRunner.CasePath;
    private const string EvaluatorId = AefScriptedRunner.EvaluatorId;
    private static AefMetric PassRate => AefScriptedRunner.PassRate;
    private static AefProducer DefaultProducer => AefScriptedRunner.DefaultProducer;

    internal static AefJobResult Run(byte[] plan, JsonObject runner, AefScriptedTarget target, string output, AefJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
        {
            throw new ArgumentException($"{output} is a file or a folder that is not empty: a job writes into a new or empty folder.", nameof(output));
        }

        if (AefSchemas.Reader.Validate("runner", runner) is { } manifestProblem)
        {
            throw new FormatException($"The runner manifest is not valid against the reader runner schema: {manifestProblem}");
        }

        var document = ReadPlan(plan);
        CheckClock(target, options.At);
        var job = new Job(document, plan, runner, target, options);
        Directory.CreateDirectory(output);
        return job.Run(output);
    }

    // A plan that reads ([ENC-1]–[ENC-7], within [ENC-17]) and names a planId a job.refused can carry (PLAN-7); its other
    // members may be anything the reader schema refuses: such a plan is refused by the job, not an input error.
    private static JsonObject ReadPlan(byte[] plan)
    {
        JsonObject document;
        try
        {
            document = AefJsonReader.ParseDocument(plan);
        }
        catch (AefReadException e)
        {
            throw new FormatException($"The plan does not read as an I-JSON document: {e.Message}", e);
        }

        if (!AefSchemas.Reader.IsValid("run-plan#/properties/planId", document["planId"]) || AefNode.String(document["planId"]) is null)
        {
            throw new FormatException("The plan names no planId a job.refused can carry (an id: 1 to 128 letters, digits and . _ : -), [PLAN-7].");
        }

        return document;
    }

    // §9.3: the job's clock, moved by every case's secondsBound and closeSeconds once per suite of the target, stays in
    // [ENC-8]'s years; a start from which it would not is an input error, found before anything is written. The clock of
    // any job against the target moves no further (a case takes no more than its bound, and a suite is one run).
    private static void CheckClock(AefScriptedTarget target, AefTime at)
    {
        const long beyond = 400_000_000_000;   // more seconds than 0001 to 9999 hold: the sum stops there, and cannot overflow
        var seconds = 0L;
        foreach (var suite in target.Suites)
        {
            foreach (var scripted in suite.Cases)
            {
                seconds = Math.Min(beyond, seconds + Math.Min(beyond, scripted.SecondsBound));
            }

            seconds = Math.Min(beyond, seconds + Math.Min(beyond, target.CloseSeconds));
        }

        if (!at.AddSeconds(seconds).IsValid)
        {
            throw new FormatException(
                $"--at {at}: the job's clock, moved by the target's time bounds and closeSeconds ({seconds} s), would pass the end of the year 9999 ([ENC-8], spec 09 §9.3).");
        }
    }

    /// <summary>One job: its inputs, its clock, its stream and the runs it writes.</summary>
    private sealed class Job
    {
        private readonly JsonObject _plan;
        private readonly JsonObject _runner;
        private readonly AefScriptedTarget _target;
        private readonly AefJobOptions _options;
        private readonly string _planDigest;
        private readonly string _runnerId;
        private readonly string _jobId;
        private readonly AefTime _start;
        private readonly List<double> _spent = [];
        private readonly List<double> _totals = [];
        private readonly List<string> _sealed = [];
        private AefTime _clock;
        private long _done;
        private EventWriter? _events;

        public Job(JsonObject plan, byte[] bytes, JsonObject runner, AefScriptedTarget target, AefJobOptions options)
        {
            _plan = plan;
            _runner = runner;
            _target = target;
            _options = options;
            _planDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            _runnerId = AefNode.String(runner["runnerId"])!;
            _start = _clock = options.At;

            // The job's id, from everything that decides what it writes: one job run twice writes the same bytes, and two
            // jobs that would write different runs never share a run id ([RUN-13]).
            var seed = string.Join('\u001F', "aef-job", _planDigest, Encoding.UTF8.GetString(AefJsonWriter.Compact(runner)), target.Fingerprint, _start.ToString());
            _jobId = "job-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), 0, 12).ToLowerInvariant();
        }

        public AefJobResult Run(string output)
        {
            _events = new EventWriter(Path.Combine(output, EventsFile), _jobId);
            var (refusal, suites) = Decide();
            if (refusal is not null)
            {
                _events.Emit("job.refused", _clock, new JsonObject
                {
                    ["planId"] = AefNode.String(_plan["planId"]), ["planDigest"] = _planDigest, ["runnerId"] = _runnerId, ["reason"] = Cut(refusal),
                });
                return new AefJobResult(_jobId, _events.Count, "job.refused", null, refusal, [], _clock);
            }

            _events.Emit("job.accepted", _clock, new JsonObject { ["planId"] = AefNode.String(_plan["planId"]), ["planDigest"] = _planDigest, ["runnerId"] = _runnerId });
            Estimate(suites);

            string? limit = null, why = null;
            for (var k = 0; k < suites.Count && limit is null; k++)
            {
                var (planSuite, suite) = suites[k];
                OpenRun? run = null;
                foreach (var scripted in suite.Cases)
                {
                    (limit, why) = Check(scripted, suite, run);
                    if (limit is not null)
                    {
                        break;
                    }

                    run ??= Open(k, planSuite, suite, output);
                    RunCase(run, scripted, planSuite);
                }

                if (run is not null)
                {
                    Seal(run, limit, why);
                }
            }

            var runs = new JsonArray([.. _sealed.Select(r => (JsonNode?)JsonValue.Create(r))]);
            if (limit is null)
            {
                _events.Emit("job.sealed", _clock, new JsonObject { ["runs"] = runs });
                return new AefJobResult(_jobId, _events.Count, "job.sealed", null, null, [.. _sealed], _clock);
            }

            _events.Emit("job.failed", _clock, new JsonObject { ["reason"] = Cut(why!), ["limit"] = limit, ["runs"] = runs });
            return new AefJobResult(_jobId, _events.Count, "job.failed", limit, why, [.. _sealed], _clock);
        }

        // [PLAN-7], [PLAN-8], [PLAN-3]: whether this runner takes the plan, and, when it does, the plan's suites resolved
        // in the target, in the plan's order. The refusal names no credential's path or value.
        private (string? Refusal, List<(JsonObject Plan, AefScriptedSuite Target)> Suites) Decide()
        {
            List<(JsonObject, AefScriptedSuite)> resolved = [];
            if (AefSchemas.Reader.Validate("run-plan", _plan) is not null)
            {
                return ("the plan is not valid against the reader run-plan schema ([PLAN-7], [VER-3])", resolved);
            }

            if (RunnerEventStream.WhyNot(_plan, _runner) is { } notTaken)
            {
                return (notTaken, resolved);
            }

            var mode = RunnerEventStream.TargetModeOf(_plan);
            if (mode != "scripted")
            {
                return ($"this runner drives a scripted target only: it cannot give the target mode {mode} ([PLAN-7], [RUN-7])", resolved);
            }

            if (AefNode.String(_plan["isolation"]) is { } isolation && isolation != "process")
            {
                return ($"a scripted target runs in the runner's own process, so this runner gives the isolation process only, not {isolation} ([PLAN-7], §9.2.1)", resolved);
            }

            foreach (var planSuite in AefNode.Objects(_plan["suites"]))
            {
                var (suiteRef, version) = (AefNode.String(planSuite["ref"])!, AefNode.String(planSuite["version"])!);
                if (resolved.Any(s => AefNode.String(s.Item1["ref"]) == suiteRef && AefNode.String(s.Item1["version"]) == version))
                {
                    return ($"the plan names the suite {suiteRef} version {version} twice: a job runs a suite's cases once ([PLAN-8])", []);
                }

                if (_target.Find(suiteRef, version) is not { } suite)
                {
                    return ($"the suite {suiteRef} version {version} cannot be resolved ([PLAN-8])", []);
                }

                if (AefNode.String(planSuite["digest"]) is { } digest && digest != suite.Digest)
                {
                    return ($"the content of the suite {suiteRef} version {version} does not have the plan's digest ([PLAN-8])", []);
                }

                resolved.Add((planSuite, suite));
            }

            // [PLAN-3]: every credential, whatever the target needs, before job.accepted. A value goes to the process its
            // purpose names; the scripted target, in this process, takes none, so it is dropped here, and never written.
            var i = 0;
            foreach (var credential in AefNode.Objects(_plan["credentialRefs"]))
            {
                i++;
                var name = AefNode.String(credential["name"]) ?? "";
                var scheme = AefNode.String(credential["scheme"]);
                var set = scheme == "env" && AefNode.String(credential["path"]) is { } variable && !string.IsNullOrEmpty(_options.Environment(variable));
                if (!set)
                {
                    return (scheme == "env"
                        ? $"credential {i} ({name}) cannot be resolved: the variable it names is not set, or empty, where the runner runs ([PLAN-3])"
                        : $"credential {i} ({name}) is kept in a {scheme} this runner cannot reach ([PLAN-3])", []);
                }
            }

            return (null, resolved);
        }

        // plan.estimated: the cases of the plan's suites, at most its cases limit, and the sums of the usd and usdBound of
        // the cases it counts (the first ones, in run order), each computed exactly and rounded once (§9.2.1, [SUM-5]).
        private void Estimate(List<(JsonObject Plan, AefScriptedSuite Target)> suites)
        {
            var planned = suites.SelectMany(s => s.Target.Cases).ToList();
            var counted = CasesLimit is { } limit && limit < planned.Count ? planned.Take((int)limit).ToList() : planned;
            _events!.Emit("plan.estimated", _clock, new JsonObject
            {
                ["cases"] = (long)counted.Count,
                ["usdLow"] = AefSummaryCalculator.ExactSum(counted.Select(c => c.Usd)),
                ["usdHigh"] = AefSummaryCalculator.ExactSum(counted.Select(c => c.UsdBound)),
                ["priceTable"] = _target.PriceTable,
            });
        }

        // [PLAN-9]: the first limit, in [PLAN-2]'s order, the case could pass, and why; or none. open is the run the case
        // would be a case of, when it is open already.
        private (string? Limit, string? Why) Check(AefScriptedCase next, AefScriptedSuite suite, OpenRun? open)
        {
            var where = $"before case {next.CaseId} of suite {suite.Ref} version {suite.Version}";
            var maxUsd = AefNode.Number(AefNode.At(_plan, "limits", "maxUsd")) ?? 0;

            // The spend so far plus the case's cost bound, as the two verifiers of the budget will sum the job once the case
            // has cost its bound (Q4-39 R7R-3), each rounded once and compared with maxUsd (equal is within it): [STRM-3]'s
            // spentUsd, the job's cases summed exactly; and [STRM-4]'s sum of the runs' cost.totalUsd, each run's own cases
            // summed exactly and rounded once, this run's with the bound. The two can differ in the last bit, and a runner
            // that keeps to its bounds passes neither ([PLAN-9]).
            var spend = AefSummaryCalculator.ExactSum(_spent.Append(next.UsdBound));
            var runs = AefSummaryCalculator.ExactSum(_totals.Append(AefSummaryCalculator.ExactSum((open?.Usd ?? []).Append(next.UsdBound))));
            if (spend > maxUsd || runs > maxUsd)
            {
                return ("maxUsd", $"stopped {where}: the spend so far plus its cost bound would be above maxUsd ([PLAN-9])");
            }

            if (CasesLimit is { } cases && _done >= cases)
            {
                return ("cases", $"stopped {where}: the plan's {cases} cases are complete ([PLAN-9])");
            }

            if (TimeoutSeconds is { } timeout && (_clock.Seconds - _start.Seconds) + next.SecondsBound + _target.CloseSeconds > timeout)
            {
                return ("timeout", $"stopped {where}: the time since job.accepted, plus its time bound, plus the time kept for closing and sealing the run, would be above the timeout ([PLAN-9])");
            }

            return (null, null);
        }

        private long? CasesLimit => AefNode.Number(AefNode.At(_plan, "limits", "cases")) is { } n ? (long)n : null;

        private long? TimeoutSeconds => AefNode.String(AefNode.At(_plan, "limits", "timeout")) is { } t ? AefDuration.Seconds(t) : null;

        // A run for the k-th suite of the plan, started now: what the plan gives ([STRM-4]), what [PLAN-10] derives, and
        // the job's provenance ([RUN-12]).
        private OpenRun Open(int k, JsonObject planSuite, AefScriptedSuite suite, string output)
        {
            var runId = $"{_jobId}-{(k + 1).ToString(CultureInfo.InvariantCulture)}";
            var subject = _plan["subject"]!;
            var deployment = AefPlanDefaults.DeploymentRef(_plan);
            var header = new AefRunHeader
            {
                RunId = runId,
                Producer = _options.Producer ?? DefaultProducer,
                Subject = new AefSubject
                {
                    Ref = AefNode.String(subject["ref"])!,
                    Kind = AefPlanDefaults.SubjectKind(_plan),
                    Version = AefNode.String(subject["version"]),
                },
                Deployment = deployment is null ? null : new AefDeployment { Ref = deployment, Endpoint = AefNode.String(subject["endpoint"]) },
                Suite = new AefSuite { Ref = suite.Ref, Version = suite.Version, Digest = AefNode.String(planSuite["digest"]) },

                // No judges: a scripted target grades with no model, and a run names the models that graded it ([RUN-9];
                // §9.2.1, round 8). The plan's judges bound which may; none is within them ([STRM-4]).
                StartedAt = _clock,
                ContentCapture = AefNode.String(_plan["contentCapture"]) == "on" ? AefContentCapture.On : AefContentCapture.Off,
                CostPolicy = new AefCostPolicy { MaxUsd = AefNode.Number(AefNode.At(_plan, "limits", "maxUsd")), PriceTable = _target.PriceTable },
                Provenance = new AefProvenance { PlanId = AefNode.String(_plan["planId"])!, PlanDigest = _planDigest, JobId = _jobId, RunnerId = _runnerId },
                Execution = new AefExecution { TargetMode = AefTargetMode.Scripted, Stimulus = AefStimulus.Suite },
            };
            var directory = Path.Combine(output, RunsFolder, runId);
            var writer = AefRunWriter.Create(directory, header);
            writer.SetMetrics([PassRate]);
            return new OpenRun(runId, directory, writer, AefNode.String(planSuite["lane"]));
        }

        // A case: its line (the case's root at path check, its caseId unrewritten, its state; a severity on a failed or
        // warn line, a reason on a typed absence), the clock moved by its seconds, its usd added to the spend.
        private void RunCase(OpenRun run, AefScriptedCase scripted, JsonObject planSuite)
        {
            var started = _clock;
            _clock = _clock.AddSeconds(scripted.Seconds);
            run.Writer.AddResult(new AefResult
            {
                CaseId = scripted.CaseId,
                Path = CasePath,
                Evaluator = new AefEvaluator(EvaluatorId),
                State = scripted.State,
                Reason = scripted.State is AefState.NotMeasured or AefState.NotApplicable or AefState.Skipped or AefState.Error
                    ? $"the scripted target answers this case {AefNames.Of(scripted.State)}"
                    : null,
                Severity = scripted.Severity,
                Lane = AefNode.String(planSuite["lane"]),
                StartedAt = started,
                EndedAt = _clock,
            });
            run.Usd.Add(scripted.Usd);
            _spent.Add(scripted.Usd);
            _done++;
            _events!.Emit("case.completed", _clock, new JsonObject { ["caseId"] = scripted.CaseId, ["state"] = AefNames.Of(scripted.State), ["runId"] = run.RunId });
            _events.Emit("spend.updated", _clock, new JsonObject { ["spentUsd"] = AefSummaryCalculator.ExactSum(_spent) });
        }

        // Closes the run (completed, or aborted when a limit cut it off, [RUN-5]) when its last case has run, seals it once
        // closeSeconds have passed, and announces it.
        private void Seal(OpenRun run, string? limit, string? why)
        {
            // The suite's lane, when the plan gives one ([PLAN-8]): pass-rate at the cases' root path (§9.2.1).
            var total = AefSummaryCalculator.ExactSum(run.Usd);
            run.Writer.SetSummary(new AefSummary
            {
                Lanes = run.Lane is { } lane ? [new AefSummaryLane(lane, [new AefSummaryEntry { Metric = PassRate.Id, Path = CasePath }])] : [],
                Cost = new AefCost(total, $"price table {_target.PriceTable}"),
            });
            run.Writer.Close(limit is null ? AefRunStatus.Completed : AefRunStatus.Aborted, _clock, limit is null ? null : Cut(why!));
            _clock = _clock.AddSeconds(_target.CloseSeconds);
            var seal = AefSealer.Seal(run.Directory, new AefSealOptions { SealedAt = _clock.ToString() });
            _events!.Emit("evidence.produced", _clock, new JsonObject { ["runId"] = run.RunId, ["runHash"] = seal.RunHash });
            _sealed.Add(run.RunId);
            _totals.Add(total);
        }

        private static string Cut(string reason) => CutReason(reason);
    }

    /// <summary>
    /// A reason within the 2,048 characters the schemas allow (<c>reason</c>, <c>abortReason</c>), counted as JSON Schema
    /// counts them, in code points: a longer one keeps its first 2,045 and ends with <c>...</c>, never splitting a
    /// character (a surrogate pair cut in two is no Unicode text, [ENC-1]).
    /// </summary>
    internal static string CutReason(string reason)
    {
        const int max = 2048;
        if (AefScriptedTarget.Characters(reason) <= max)
        {
            return reason;
        }

        var (kept, end) = (0, 0);
        foreach (var rune in reason.EnumerateRunes())
        {
            if (kept == max - 3)
            {
                break;
            }

            kept++;
            end += rune.Utf16SequenceLength;
        }

        return reason[..end] + "...";
    }

    private sealed record OpenRun(string RunId, string Directory, AefRunWriter Writer, string? Lane)
    {
        public List<double> Usd { get; } = [];
    }

    /// <summary>
    /// The job's event stream ([STRM-1], §6.4): each event numbered from 1, with the job's id, checked against the writer
    /// runner-event schema ([VER-2]) and appended to the file as one NDJSON line ([ENC-5]).
    /// </summary>
    private sealed class EventWriter(string path, string jobId)
    {
        public int Count { get; private set; }

        public void Emit(string kind, AefTime at, JsonObject members)
        {
            var e = new JsonObject
            {
                ["schemaVersion"] = AefRunWriter.SchemaVersion,
                ["seq"] = (long)(Count + 1),
                ["kind"] = kind,
                ["jobId"] = jobId,
                ["at"] = AefWire.Time(at),
            };
            foreach (var (name, value) in members.ToList())
            {
                members.Remove(name);
                e[name] = value;
            }

            if (AefSchemas.Writer.Validate("runner-event", e) is { } problem)
            {
                throw new InvalidOperationException($"The {kind} event would not be valid against the writer runner-event schema ([VER-2]): {problem}");
            }

            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(AefJsonWriter.Line(e));
            }

            Count++;
        }
    }
}

