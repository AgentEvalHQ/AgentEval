// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Runner;

/// <summary>How <see cref="AefScriptedRunner.Run"/> runs a job.</summary>
public sealed class AefJobOptions
{
    /// <summary>
    /// Where the job's clock starts (spec 09 §9.2.1 "The job's clock", the operation's <c>--at</c>): the first event is
    /// at it, and the clock moves only when a case runs, by its <c>seconds</c>, and when a run is closed and sealed, by
    /// the target's <c>closeSeconds</c>.
    /// </summary>
    public required AefTime At { get; init; }

    /// <summary>
    /// The runner's environment: the value of a variable, or null when it is not set. A credential of scheme <c>env</c>
    /// resolves when the variable its <c>path</c> names is set ([PLAN-3]). The process's environment when not given.
    /// </summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The producer each run's run.json names; <see cref="AefScriptedRunner.DefaultProducer"/> when not given.</summary>
    public AefProducer? Producer { get; init; }
}

/// <summary>What a job wrote (<see cref="AefScriptedRunner.Run"/>).</summary>
/// <param name="JobId">The job's id, on every event.</param>
/// <param name="Events">The number of events in <c>events.ndjson</c>.</param>
/// <param name="Terminal">The terminal event's kind: <c>job.refused</c>, <c>job.sealed</c> or <c>job.failed</c>.</param>
/// <param name="Limit">The plan limit the job stopped at (<c>maxUsd</c>, <c>cases</c>, <c>timeout</c>), or null.</param>
/// <param name="Reason">Why the plan was refused or the job stopped, or null for a sealed job.</param>
/// <param name="Runs">The runs it sealed, in the order announced.</param>
/// <param name="EndsAt">The terminal event's time.</param>
public sealed record AefJobResult(string JobId, int Events, string Terminal, string? Limit, string? Reason, IReadOnlyList<string> Runs, AefTime EndsAt);

/// <summary>
/// A runner of AEF jobs against a scripted target (contracts/aef/1/spec/06-runners.md §6, and spec 09 §9.2.1 "A
/// scripted target" and "The job's clock"): given a run plan, the capability manifest it acts as, and an
/// <see cref="AefScriptedTarget"/>, it decides whether it takes the plan ([PLAN-7]), runs the plan's suites as runs
/// ([PLAN-8]) within its limits ([PLAN-9]), writes and seals each run with AgentEval.Results' writer
/// (<see cref="AefRunWriter"/>) and sealer (<see cref="AefSealer"/>), and reports the job as an event stream ([STRM-1],
/// §6.4) in <c>events.ndjson</c>. It gives the target mode <c>scripted</c> only ([RUN-7]).
/// </summary>
/// <remarks>
/// <para>
/// Before <c>job.accepted</c> it refuses (<c>job.refused</c>, one event) a plan the reader schema refuses (its
/// <c>planId</c> readable), a plan the manifest does not take (<see cref="RunnerEventStream.WhyNot"/>), a target mode
/// other than <c>scripted</c>, an isolation other than <c>process</c> (it runs the target in its own process), a suite
/// named twice, a suite the target does not have or whose content does not have the plan's digest ([PLAN-8]), and a
/// credential it cannot resolve ([PLAN-3]: <c>env</c> when its variable is set and not empty; it reaches no keychain and
/// no vault). It resolves every credential the plan names, though the target needs none, and writes no value and no
/// reference's path anywhere.
/// </para>
/// <para>
/// Running, it opens one run per suite before the suite's first case, in the plan's order, and runs the suite's cases in
/// the target's order. Before each case it checks the plan's limits in [PLAN-2]'s order against what the case could
/// take: <c>maxUsd</c> against the spend so far plus the case's cost bound, summed both ways a Stream verifier sums a
/// job (its cases, [STRM-3]; its runs' totals, [STRM-4]), each exactly and rounded once ([SUM-5]);
/// <c>cases</c> against the cases completed; <c>timeout</c> against the time since <c>job.accepted</c> plus the case's
/// time bound plus <c>closeSeconds</c>. At the first it could pass it stops: an open run is closed <c>aborted</c>, sealed
/// and announced, and <c>job.failed</c> names the limit and the runs sealed. A suite whose first case a limit stops gets
/// no run. A run whose suite's last case has run is closed <c>completed</c> and sealed before the next check. Each run
/// declares <see cref="PassRate"/>; a run of a suite the plan gives a lane names it on every line and summarises
/// <c>pass-rate</c> at <c>check</c> in that lane ([PLAN-8]); a case's severity is the target's.
/// </para>
/// <para>
/// Every id and time derives from the inputs (the plan's bytes, the manifest, the target, the clock's start): the same
/// job run twice writes the same bytes.
/// </para>
/// </remarks>
public static class AefScriptedRunner
{
    /// <summary>The path of each case's line: the case's root ([RES-5]), as §9.2.1 asks.</summary>
    public const string CasePath = "check";

    /// <summary>The evaluator each line names ([RES-11]): the scripted target's fixed answer.</summary>
    public const string EvaluatorId = "aef-scripted-target";

    /// <summary>run.json <c>producer.name</c> of a run this runner writes.</summary>
    public const string ProducerName = "agenteval-results";

    /// <summary>
    /// The metric every run declares (§9.2.1): <c>pass-rate</c>, a <c>rate</c> (a line counts 1 when <c>passed</c>, 0
    /// otherwise, [SUM-4]), higher is better, from 0 to 1; a suite's lane summarises it at <see cref="CasePath"/>.
    /// </summary>
    public static AefMetric PassRate { get; } = new()
    {
        Id = "pass-rate", Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
    };

    private const string EventsFile = "events.ndjson";
    private const string RunsFolder = "runs";

    /// <summary>This runner as a producer: <see cref="ProducerName"/> at AgentEval.Results' release version.</summary>
    public static AefProducer DefaultProducer { get; } = new() { Name = ProducerName, Version = ReadVersion() };

    /// <summary>
    /// Runs the plan <paramref name="plan"/> (its exact bytes) as the runner <paramref name="runner"/> describes, against
    /// <paramref name="target"/>, writing into <paramref name="output"/> (which does not exist yet or is empty) the event
    /// stream <c>events.ndjson</c> and <c>runs/&lt;runId&gt;/</c> for each run it seals, and nothing else.
    /// </summary>
    /// <returns>What the job wrote: refused, sealed or failed, all of them a job that ran.</returns>
    /// <exception cref="FormatException">
    /// An input error, with nothing written: a plan that does not read as an I-JSON document or names no <c>planId</c> a
    /// <c>job.refused</c> can carry (a valid id), or a manifest the reader <c>runner</c> schema refuses.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is a file, or a folder that is not empty.</exception>
    /// <exception cref="IOException">The output cannot be written.</exception>
    public static AefJobResult Run(byte[] plan, JsonObject runner, AefScriptedTarget target, string output, AefJobOptions options)
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

    private static string ReadVersion()
    {
        var assembly = typeof(AefScriptedRunner).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is not null && version.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0)
        {
            version = version[..plus];
        }

        return version is { Length: > 0 and <= 128 } && version.All(c => c is >= '!' and <= '~')
            ? version
            : assembly.GetName().Version?.ToString() ?? "0.0.0";
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
                Judges = AefNode.Objects(_plan["judges"]).Select(j => new AefJudge
                {
                    Model = AefNode.String(j["model"])!,
                    Provider = AefNode.String(j["provider"]),
                    RubricDigest = AefNode.String(j["rubricDigest"]),
                }).ToList() is { Count: > 0 } judges ? judges : null,
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

        // A reason within the 2,048 characters the schema allows it.
        private static string Cut(string reason) => reason.Length <= 2048 ? reason : reason[..2045] + "...";
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

/// <summary>
/// What a run plan does not say and a runner derives ([PLAN-10]), so that two runners given one plan write the same
/// values: <c>subject.kind</c> and <c>deployment.ref</c>.
/// </summary>
public static class AefPlanDefaults
{
    /// <summary>
    /// <c>subject.kind</c>: the kind of the plan's <c>subject.ref</c> (the part before its colon) when it is one of
    /// run.json's <c>subject.kind</c> values, and <c>other</c> otherwise.
    /// </summary>
    public static AefSubjectKind SubjectKind(JsonNode plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var subjectRef = AefNode.String(AefNode.At(plan, "subject", "ref")) ?? "";
        var colon = subjectRef.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && AefNames.TryParse<AefSubjectKind>(subjectRef[..colon], out var kind) ? kind.Value : AefSubjectKind.Other;
    }

    /// <summary>
    /// <c>deployment.ref</c>: the plan's <c>subject.deployment</c>; when it names none but names an <c>endpoint</c>,
    /// <c>endpoint:</c> followed by the endpoint encoded as [ENC-13] encodes a name (<see cref="EncodeName"/>); null when
    /// it names neither.
    /// </summary>
    public static string? DeploymentRef(JsonNode plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (AefNode.String(AefNode.At(plan, "subject", "deployment")) is { } deployment)
        {
            return deployment;
        }

        return AefNode.String(AefNode.At(plan, "subject", "endpoint")) is { } endpoint ? "endpoint:" + EncodeName(endpoint) : null;
    }

    /// <summary>
    /// A typed reference's name derived from free text ([ENC-13]): each UTF-8 byte from <c>!</c> to <c>~</c> but
    /// <c>%</c> kept, every other byte, and <c>%</c>, as <c>%</c> and two upper-case hex digits; an empty name is
    /// <c>-</c>, and a name that is exactly <c>-</c> is <c>%2D</c>; a result longer than 256 characters is its first 239,
    /// <c>~</c>, and the first 16 lower-case hex characters of the SHA-256 of the text's UTF-8 bytes.
    /// </summary>
    public static string EncodeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var name = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b is >= 0x21 and <= 0x7E && b != (byte)'%')
            {
                name.Append((char)b);
            }
            else
            {
                name.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        var encoded = name.ToString() switch
        {
            "" => "-",
            "-" => "%2D",
            var written => written,
        };
        return encoded.Length <= 256
            ? encoded
            : encoded[..239] + "~" + Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant();
    }
}
