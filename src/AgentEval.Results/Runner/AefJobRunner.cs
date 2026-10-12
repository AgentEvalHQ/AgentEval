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

/// <summary>Executes bounded AEF jobs against asynchronous targets while preserving scripted conformance.</summary>
public static class AefJobRunner
{
    private const string EventsFile = "events.ndjson";
    private const string RunsFolder = "runs";
    private static AefProducer DefaultProducer => AefScriptedRunner.DefaultProducer;

    /// <summary>Accepts or refuses a plan, then executes its suites sequentially and writes sealed evidence and events.</summary>
    public static async Task<AefJobResult> RunAsync(byte[] plan, JsonObject runner, IAefJobTarget target, string output, AefJobOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
        {
            throw new ArgumentException($"{output} is a file or a folder that is not empty: a job writes into a new or empty folder.", nameof(output));
        }

        var job = CreateJob(plan, runner, target, options);
        Directory.CreateDirectory(output);
        return await job.RunAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uses execution's validation, acceptance and estimate rules without calls, events or filesystem writes.</summary>
    public static AefJobAnalysis Analyze(byte[] plan, JsonObject runner, IAefJobTarget target, AefJobOptions options) =>
        CreateJob(plan, runner, target, options).Analyze();

    private static Job CreateJob(byte[] plan, JsonObject runner, IAefJobTarget target, AefJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        if (AefSchemas.Reader.Validate("runner", runner) is { } manifestProblem)
        {
            throw new FormatException($"The runner manifest is not valid against the reader runner schema: {manifestProblem}");
        }

        var document = ReadPlan(plan);
        if (target.Suites.Any(s => !double.IsFinite(s.PreparationUsdLow) || !double.IsFinite(s.PreparationUsdBound)
            || s.PreparationUsdLow < 0 || s.PreparationUsdBound < s.PreparationUsdLow
            || s.PreparationSecondsBound is < 0 or > 4_294_967))
            throw new ArgumentException("Preparation requires finite nonnegative cost bounds and a time bound within the cancellation timer range.", nameof(target));
        if (target.CloseSeconds < 0 || target.Suites.SelectMany(s => s.Cases).Any(c =>
            !double.IsFinite(c.UsdLow) || !double.IsFinite(c.UsdBound) || c.UsdLow < 0 || c.UsdBound < c.UsdLow
            || c.SecondsBound < 0 || (target.TargetMode != AefTargetMode.Scripted && c.SecondsBound > 4_294_967)))
            throw new ArgumentException("Target cost bounds must be finite and nonnegative; live time bounds must fit a cancellation timer.", nameof(target));
        CheckClock(target, options.At);
        return new Job(document, plan, runner, target, options);
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
    private static void CheckClock(IAefJobTarget target, AefTime at)
    {
        const long beyond = 400_000_000_000;   // more seconds than 0001 to 9999 hold: the sum stops there, and cannot overflow
        var seconds = 0L;
        foreach (var suite in target.Suites)
        {
            seconds = Math.Min(beyond, seconds + suite.PreparationSecondsBound);
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
        private readonly IAefJobTarget _target;
        private readonly AefJobOptions _options;
        private readonly string _planDigest;
        private readonly string _runnerId;
        private readonly string _jobId;
        private readonly AefTime _start;
        private readonly List<double> _spent = [];
        private readonly List<double> _totals = [];
        private readonly List<string> _sealed = [];
        private readonly IAefJobClock _jobClock;
        private AefTime _clock => _jobClock.Now;
        private long _done;
        private EventWriter? _events;
        private AefJobCredentials? _credentials;

        public Job(JsonObject plan, byte[] bytes, JsonObject runner, IAefJobTarget target, AefJobOptions options)
        {
            _plan = plan;
            _runner = runner;
            _target = target;
            _options = options;
            _planDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            _runnerId = AefNode.String(runner["runnerId"])!;
            _jobClock = options.Clock ?? (target.TargetMode == AefTargetMode.Scripted
                ? new AefSimulatedJobClock(options.At) : new AefElapsedJobClock(options.At));
            _start = _clock;

            // The job's id, from everything that decides what it writes: one job run twice writes the same bytes, and two
            // jobs that would write different runs never share a run id ([RUN-13]).
            var seed = string.Join('\u001F', "aef-job", _planDigest, Encoding.UTF8.GetString(AefJsonWriter.Compact(runner)), target.Fingerprint, _start.ToString());
            _jobId = "job-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), 0, 12).ToLowerInvariant();
        }

        public async Task<AefJobResult> RunAsync(string output, CancellationToken cancellationToken)
        {
            _events = new EventWriter(Path.Combine(output, EventsFile), _jobId, _options.Progress);
            var (refusal, suites) = Decide();
            if (refusal is not null)
            {
                _events.Emit("job.refused", _clock, new JsonObject
                {
                    ["planId"] = AefNode.String(_plan["planId"]), ["planDigest"] = _planDigest, ["runnerId"] = _runnerId, ["reason"] = Cut(refusal),
                });
                return new AefJobResult(_jobId, _events.Count, "job.refused", null, refusal, [], _clock);
            }

            string? limit = null, why = null;
            OpenRun? activeRun = null;
            try
            {
                _events.Emit("job.accepted", _clock, new JsonObject { ["planId"] = AefNode.String(_plan["planId"]), ["planDigest"] = _planDigest, ["runnerId"] = _runnerId });
                if (_target.TargetMode != AefTargetMode.Scripted) _target.SetCredentials(_credentials!);
                var estimate = Estimate(suites);
                _events.Emit("plan.estimated", _clock, new JsonObject
                {
                    ["cases"] = estimate.Cases, ["usdLow"] = estimate.UsdLow,
                    ["usdHigh"] = estimate.UsdHigh, ["priceTable"] = estimate.PriceTable,
                });
                cancellationToken.ThrowIfCancellationRequested();
                for (var k = 0; k < suites.Count && why is null; k++)
                {
                    var (planSuite, suite) = suites[k];
                    foreach (var next in suite.Cases)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        (limit, why) = Check(next, suite, activeRun);
                        if (limit is not null) break;
                        if (activeRun is null)
                        {
                            activeRun = Open(k, planSuite, suite, output);
                            var preparationFailure = await PrepareAsync(activeRun, cancellationToken).ConfigureAwait(false);
                            (limit, why) = CheckActual(activeRun);
                            why ??= preparationFailure;
                            if (why is not null) break;
                            (limit, why) = Check(next, suite, activeRun);
                            if (why is not null) break;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        var violation = await RunCaseAsync(activeRun, next, suite, planSuite, cancellationToken).ConfigureAwait(false);
                        (limit, why) = CheckActual(activeRun);
                        why ??= violation;
                        if (why is not null) break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (activeRun is not null) Seal(activeRun, limit, why);
                    activeRun = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (activeRun is not null) Seal(activeRun, null, "The job was cancelled"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FailIo(activeRun); }
                _events.Emit("job.cancelled", _clock, new JsonObject { ["reason"] = "The job was cancelled" });
                return new AefJobResult(_jobId, _events.Count, "job.cancelled", null, "The job was cancelled", [.. _sealed], _clock);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FailIo(activeRun);
            }

            var runs = new JsonArray([.. _sealed.Select(r => (JsonNode?)JsonValue.Create(r))]);
            if (why is null)
            {
                _events.Emit("job.sealed", _clock, new JsonObject { ["runs"] = runs });
                return new AefJobResult(_jobId, _events.Count, "job.sealed", null, null, [.. _sealed], _clock);
            }

            var failure = new JsonObject { ["reason"] = Cut(why) };
            if (limit is not null) failure["limit"] = limit;
            failure["runs"] = runs;
            _events.Emit("job.failed", _clock, failure);
            return new AefJobResult(_jobId, _events.Count, "job.failed", limit, why, [.. _sealed], _clock);
        }

        public AefJobAnalysis Analyze()
        {
            var (reason, suites) = Decide();
            return new AefJobAnalysis(reason is null, reason, reason is null ? Estimate(suites) : null);
        }

        private AefJobResult FailIo(OpenRun? run)
        {
            // A permanently unwritable event file still propagates its I/O error; a successful terminal event is never claimed.
            if (run?.Writer.Status == AefRunStatus.Running)
                try { Seal(run, null, "The job encountered an I/O failure"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _events!.Emit("job.failed", _clock, new JsonObject
            {
                ["reason"] = "The job encountered an I/O failure",
                ["runs"] = new JsonArray([.. _sealed.Select(r => (JsonNode?)JsonValue.Create(r))]),
            });
            return new AefJobResult(_jobId, _events.Count, "job.failed", null, "The job encountered an I/O failure", [.. _sealed], _clock);
        }

        // [PLAN-7], [PLAN-8], [PLAN-3]: whether this runner takes the plan, and, when it does, the plan's suites resolved
        // in the target, in the plan's order. The refusal names no credential's path or value.
        private (string? Refusal, List<(JsonObject Plan, IAefJobSuite Target)> Suites) Decide()
        {
            List<(JsonObject, IAefJobSuite)> resolved = [];
            if (AefSchemas.Reader.Validate("run-plan", _plan) is not null)
            {
                return ("the plan is not valid against the reader run-plan schema ([PLAN-7], [VER-3])", resolved);
            }

            if (RunnerEventStream.WhyNot(_plan, _runner) is { } notTaken)
            {
                return (notTaken, resolved);
            }

            var mode = RunnerEventStream.TargetModeOf(_plan);
            if (mode != AefNames.Of(_target.TargetMode))
            {
                return (_target.TargetMode == AefTargetMode.Scripted
                    ? $"this runner drives a scripted target only: it cannot give the target mode {mode} ([PLAN-7], [RUN-7])"
                    : $"this target cannot give the target mode {mode} ([PLAN-7], [RUN-7])", resolved);
            }

            if (AefNode.String(_plan["isolation"]) is { } isolation && isolation != _target.Isolation)
            {
                return (_target.TargetMode == AefTargetMode.Scripted
                    ? $"a scripted target runs in the runner's own process, so this runner gives the isolation process only, not {isolation} ([PLAN-7], §9.2.1)"
                    : $"this target cannot provide isolation {isolation} ([PLAN-7])", resolved);
            }

            foreach (var planSuite in AefNode.Objects(_plan["suites"]))
            {
                var (suiteRef, version) = (AefNode.String(planSuite["ref"])!, AefNode.String(planSuite["version"])!);
                if (resolved.Any(s => AefNode.String(s.Item1["ref"]) == suiteRef && AefNode.String(s.Item1["version"]) == version))
                {
                    return ($"the plan names the suite {suiteRef} version {version} twice: a job runs a suite's cases once ([PLAN-8])", []);
                }

                if (_target.Suites.FirstOrDefault(s => s.Ref == suiteRef && s.Version == version) is not { } suite)
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
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var purposes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var credential in AefNode.Objects(_plan["credentialRefs"]))
            {
                i++;
                var name = AefNode.String(credential["name"]) ?? "";
                var scheme = AefNode.String(credential["scheme"]);
                var value = scheme == "env" && AefNode.String(credential["path"]) is { } variable ? _options.Environment(variable) : null;
                if (string.IsNullOrEmpty(value))
                {
                    return (scheme == "env"
                        ? $"credential {i} ({name}) cannot be resolved: the variable it names is not set, or empty, where the runner runs ([PLAN-3])"
                        : $"credential {i} ({name}) is kept in a {scheme} this runner cannot reach ([PLAN-3])", []);
                }
                if (_target.TargetMode == AefTargetMode.Scripted) continue;
                var purpose = AefNode.String(credential["purpose"])!;
                if (!_target.CredentialPurposes.Contains(purpose, StringComparer.Ordinal))
                    return ($"credential {i} ({name}) names a purpose this target cannot receive ([PLAN-3])", []);
                if (values.TryGetValue(name, out var existing) && !string.Equals(existing, value, StringComparison.Ordinal))
                    return ($"credential {i} ({name}) conflicts with another value assigned to the same name ([PLAN-3])", []);
                values[name] = value;
                if (!purposes.TryGetValue(purpose, out var overlay)) purposes[purpose] = overlay = new(StringComparer.Ordinal);
                overlay[name] = value;
            }

            _credentials = new AefJobCredentials(purposes);
            return (null, resolved);
        }

        // plan.estimated: the cases of the plan's suites, at most its cases limit, and the sums of the usd and usdBound of
        // the cases it counts (the first ones, in run order), each computed exactly and rounded once (§9.2.1, [SUM-5]).
        private AefJobEstimate Estimate(List<(JsonObject Plan, IAefJobSuite Target)> suites)
        {
            var remaining = CasesLimit ?? long.MaxValue;
            long count = 0;
            var low = new List<double>();
            var high = new List<double>();
            foreach (var (_, suite) in suites)
            {
                var take = (int)Math.Min(remaining, suite.Cases.Count);
                if (take == 0) continue;
                low.Add(suite.PreparationUsdLow);
                high.Add(suite.PreparationUsdBound);
                foreach (var c in suite.Cases.Take(take)) { low.Add(c.UsdLow); high.Add(c.UsdBound); }
                count += take;
                remaining -= take;
            }
            return new AefJobEstimate(count, AefSummaryCalculator.ExactSum(low), AefSummaryCalculator.ExactSum(high), _target.PriceTable);
        }

        // [PLAN-9]: the first limit, in [PLAN-2]'s order, the case could pass, and why; or none. open is the run the case
        // would be a case of, when it is open already.
        private (string? Limit, string? Why) Check(IAefJobCase next, IAefJobSuite suite, OpenRun? open)
        {
            var where = $"before case {next.CaseId} of suite {suite.Ref} version {suite.Version}";
            var maxUsd = AefNode.Number(AefNode.At(_plan, "limits", "maxUsd")) ?? 0;

            // The spend so far plus the case's cost bound, as the two verifiers of the budget will sum the job once the case
            // has cost its bound (Q4-39 R7R-3), each rounded once and compared with maxUsd (equal is within it): [STRM-3]'s
            // spentUsd, the job's cases summed exactly; and [STRM-4]'s sum of the runs' cost.totalUsd, each run's own cases
            // summed exactly and rounded once, this run's with the bound. The two can differ in the last bit, and a runner
            // that keeps to its bounds passes neither ([PLAN-9]).
            var preparation = open is null ? suite.PreparationUsdBound : 0;
            var spend = AefSummaryCalculator.ExactSum(_spent.Append(next.UsdBound).Append(preparation));
            var runs = AefSummaryCalculator.ExactSum(_totals.Append(AefSummaryCalculator.ExactSum((open?.Usd ?? []).Append(next.UsdBound).Append(preparation))));
            if (spend > maxUsd || runs > maxUsd)
            {
                return ("maxUsd", $"stopped {where}: the spend so far plus its cost bound would be above maxUsd ([PLAN-9])");
            }

            if (CasesLimit is { } cases && _done >= cases)
            {
                return ("cases", $"stopped {where}: the plan's {cases} cases are complete ([PLAN-9])");
            }

            if (TimeoutSeconds is { } timeout && _clock.CompareTo(_start.AddSeconds(timeout - next.SecondsBound
                - _target.CloseSeconds - (open is null ? suite.PreparationSecondsBound : 0))) > 0)
            {
                return ("timeout", $"stopped {where}: the time since job.accepted, plus its time bound, plus the time kept for closing and sealing the run, would be above the timeout ([PLAN-9])");
            }

            return (null, null);
        }

        private (string? Limit, string? Why) CheckActual(OpenRun run)
        {
            var maxUsd = AefNode.Number(AefNode.At(_plan, "limits", "maxUsd")) ?? 0;
            if (AefSummaryCalculator.ExactSum(_spent) > maxUsd
                || AefSummaryCalculator.ExactSum(_totals.Append(AefSummaryCalculator.ExactSum(run.Usd))) > maxUsd)
                return ("maxUsd", "The case exceeded the job's cost limit");
            if (TimeoutSeconds is { } timeout && _clock.CompareTo(_start.AddSeconds(timeout)) > 0)
                return ("timeout", "The case exceeded the job's time limit");
            return (null, null);
        }

        private long? CasesLimit => AefNode.Number(AefNode.At(_plan, "limits", "cases")) is { } n ? (long)n : null;

        private long? TimeoutSeconds => AefNode.String(AefNode.At(_plan, "limits", "timeout")) is { } t ? AefDuration.Seconds(t) : null;

        // A run for the k-th suite of the plan, started now: what the plan gives ([STRM-4]), what [PLAN-10] derives, and
        // the job's provenance ([RUN-12]).
        private OpenRun Open(int k, JsonObject planSuite, IAefJobSuite suite, string output)
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
                Execution = new AefExecution { TargetMode = _target.TargetMode, Stimulus = AefStimulus.Suite },
            };
            var directory = Path.Combine(output, RunsFolder, runId);
            var writer = AefRunWriter.Create(directory, header);
            writer.SetMetrics(suite.Metrics);
            return new OpenRun(runId, directory, writer, AefNode.String(planSuite["lane"]), suite);
        }

        // A case: its line (the case's root at path check, its caseId unrewritten, its state; a severity on a failed or
        // warn line, a reason on a typed absence), the clock moved by its seconds, its usd added to the spend.
        private async Task<string?> PrepareAsync(OpenRun run, CancellationToken cancellationToken)
        {
            var suite = run.Suite;
            var started = _clock;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_target.TargetMode != AefTargetMode.Scripted && suite.PreparationSecondsBound > 0)
                deadline.CancelAfter(TimeSpan.FromSeconds(suite.PreparationSecondsBound));
            AefJobPreparationResult result;
            var unboundedAsync = false;
            try
            {
                var preparation = suite.PrepareAsync(deadline.Token);
                unboundedAsync = suite.PreparationSecondsBound == 0 && !preparation.IsCompleted;
                if (unboundedAsync) deadline.Cancel();
                result = await preparation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ChargePreparation(run, suite.PreparationIncurredUsd);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ChargePreparation(run, suite.PreparationIncurredUsd);
                throw;
            }
            catch (Exception ex)
            {
                ChargePreparation(run, suite.PreparationIncurredUsd);
                return deadline.IsCancellationRequested ? "Run preparation exceeded its time bound"
                    : $"Run preparation threw {ex.GetType().Name}";
            }
            if (result.Seconds < 0) throw new InvalidOperationException("Preparation duration must be nonnegative.");
            _jobClock.Advance(result.Seconds);
            ChargePreparation(run, result.Usd);
            if (result.Usd > suite.PreparationUsdBound) return "Run preparation exceeded its cost bound";
            if (unboundedAsync || result.Seconds > suite.PreparationSecondsBound
                || (suite.PreparationSecondsBound > 0 && _clock.CompareTo(started.AddSeconds(suite.PreparationSecondsBound)) > 0))
                return "Run preparation exceeded its time bound";
            return result.Ready ? null : "The target was not ready";
        }

        private void ChargePreparation(OpenRun run, double usd)
        {
            if (usd == 0) return;
            AddSpend(run, usd);
            _events!.Emit("spend.updated", _clock, new JsonObject { ["spentUsd"] = AefSummaryCalculator.ExactSum(_spent) });
        }

        private async Task<string?> RunCaseAsync(OpenRun run, IAefJobCase next, IAefJobSuite suite, JsonObject planSuite, CancellationToken cancellationToken)
        {
            var started = _clock;
            AefJobCaseResult result;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_target.TargetMode != AefTargetMode.Scripted)
                deadline.CancelAfter(TimeSpan.FromSeconds(next.SecondsBound));
            var timedOut = false;
            try
            {
                result = await next.RunAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AddSpend(run, next.IncurredUsd);
                if (next.IncurredUsd > 0)
                    _events!.Emit("spend.updated", _clock, new JsonObject { ["spentUsd"] = AefSummaryCalculator.ExactSum(_spent) });
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddSpend(run, next.IncurredUsd);
                if (next.IncurredUsd > 0)
                    _events!.Emit("spend.updated", _clock, new JsonObject { ["spentUsd"] = AefSummaryCalculator.ExactSum(_spent) });
                throw;
            }
            catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
            {
                timedOut = deadline.IsCancellationRequested;
                result = new AefJobCaseResult(new AefResult
                {
                    CaseId = next.CaseId, Path = suite.CasePath, Evaluator = new AefEvaluator("agenteval.runner"),
                    State = AefState.Error, Reason = timedOut ? "The case exceeded its time bound" : $"The case threw {ex.GetType().Name}",
                }, next.IncurredUsd, 0);
            }
            if (result.Root.CaseId != next.CaseId || result.Root.Path != suite.CasePath || result.Seconds < 0)
                throw new InvalidOperationException("A case result must retain its declared id, root path and nonnegative duration.");
            _jobClock.Advance(result.Seconds);
            var root = result.Root with
            {
                Lane = AefNode.String(planSuite["lane"]),
                StartedAt = started,
                EndedAt = _clock,
            };
            var handle = run.Writer.AddResult(root);
            WriteChildren(run.Writer, handle, result.Children, root.Lane);
            if (result.Judges is { Count: > 0 })
            {
                foreach (var judge in result.Judges) run.Judges[judge.Model] = judge;
                run.Writer.SetJudges(run.Judges.Values.ToArray());
            }
            AddSpend(run, result.Usd);
            _done++;
            _events!.Emit("case.completed", _clock, new JsonObject { ["caseId"] = next.CaseId, ["state"] = AefNames.Of(root.State), ["runId"] = run.RunId });
            _events.Emit("spend.updated", _clock, new JsonObject { ["spentUsd"] = AefSummaryCalculator.ExactSum(_spent) });
            if (result.Usd > next.UsdBound) return "The case exceeded its declared cost bound";
            if (timedOut || _clock.CompareTo(started.AddSeconds(next.SecondsBound)) > 0)
                return "The case exceeded its declared time bound";
            return null;
        }

        private static void WriteChildren(AefRunWriter writer, AefResultHandle parent, IReadOnlyList<AefJobCaseNode>? children, string? lane)
        {
            if (children is null) return;
            foreach (var child in children)
            {
                var handle = writer.AddResult(child.Result with { Lane = lane }, parent);
                WriteChildren(writer, handle, child.Children, lane);
            }
        }

        private void AddSpend(OpenRun run, double usd)
        {
            if (!double.IsFinite(usd) || usd < 0) throw new InvalidOperationException("A case cost must be finite and nonnegative.");
            run.Usd.Add(usd);
            _spent.Add(usd);
        }

        // Closes the run (completed, or aborted when a limit cut it off, [RUN-5]) when its last case has run, seals it once
        // closeSeconds have passed, and announces it.
        private void Seal(OpenRun run, string? limit, string? why)
        {
            // The suite's lane, when the plan gives one ([PLAN-8]): pass-rate at the cases' root path (§9.2.1).
            var total = AefSummaryCalculator.ExactSum(run.Usd);
            run.Writer.SetSummary(new AefSummary
            {
                Lanes = run.Lane is { } lane ? [new AefSummaryLane(lane, run.Suite.SummaryEntries)] : [],
                Cost = new AefCost(total, $"price table {_target.PriceTable}"),
            });
            run.Writer.Close(why is null ? AefRunStatus.Completed : AefRunStatus.Aborted, _clock, why is null ? null : Cut(why));
            _jobClock.Advance(_target.CloseSeconds);
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

    private sealed record OpenRun(string RunId, string Directory, AefRunWriter Writer, string? Lane, IAefJobSuite Suite)
    {
        public List<double> Usd { get; } = [];
        public Dictionary<string, AefJudge> Judges { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The job's event stream ([STRM-1], §6.4): each event numbered from 1, with the job's id, checked against the writer
    /// runner-event schema ([VER-2]) and appended to the file as one NDJSON line ([ENC-5]).
    /// </summary>
    private sealed class EventWriter(string path, string jobId, Stream? progress)
    {
        private Stream? _progress = progress;
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

            var line = AefJsonWriter.Line(e);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(line);
            }

            Count++;
            try
            {
                _progress?.Write(line);
                _progress?.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The persisted event already has its sequence number. Disable a broken mirror so a terminal
                // failure can still be appended to the authoritative stream without duplicating that number.
                _progress = null;
                throw;
            }
        }
    }
}

