// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.Output;
using Microsoft.Extensions.AI;

namespace AgentEval.Benchmarks;

/// <summary>
/// Runs one <see cref="BenchmarkDefinition"/> against one <see cref="BenchmarkArm"/> into one run
/// directory: every check admitted <b>before</b> any case is observed, one row per (case, check).
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Admission happens first, and that ordering is the point.</b> Every check goes through
/// <see cref="AgentEvalBuilder.AddEval(IEval, ChanceFloor)"/> — the only door — before
/// <see cref="BenchmarkArm.Observe"/> is called even once. A definition carrying a floorless check
/// therefore fails at the door having spent nothing: no agent ran, no token was bought, no partial
/// run directory exists. Admitting lazily per case would buy the first case's run and then refuse.
/// </para>
/// <para>
/// <b>What this deliberately does NOT do.</b> It applies <b>no</b> floor to any verdict: the floors
/// ride on each result exactly as <see cref="FloorAdmittedEval"/> records them, and whether one may
/// GATE a verdict is ADR-030's Q6 — answered "yes on the principle, staged in execution", which
/// means the binding test lands in Slice 2.6 under its stated conditions and not here. It writes no
/// <c>VOID</c> verdict and claims no exit code 12 (Q5: no controls lane; a degraded
/// <see cref="BenchmarkArm"/> is the control). It adds no manifest field (Q4(ii): the schema's
/// <c>additionalProperties: false</c> stands, and <see cref="MeasurementState.NotApplicable"/> and
/// <see cref="MeasurementState.NotMeasured"/> share the one <c>skipped</c> bucket the schema has —
/// said here rather than papered over).
/// </para>
/// <para>
/// <b>One arm per run, one rep per run.</b> Putting arms or reps in a single run collides scenario
/// ids and hides the arm from <c>compare</c>, which pairs run DIRECTORIES. Reps are several runs of
/// the same <see cref="BenchmarkArm.ArmId"/>, and <see cref="BenchmarkScore"/> collapses them per
/// case.
/// </para>
/// </remarks>
public sealed class BenchmarkRunner
{
    /// <summary>
    /// Separator between a case id and a check key in a scenario id. A middle dot rather than a
    /// colon or a slash, because <c>FileSystemLayout.Sanitize</c> rewrites an invalid character AND
    /// appends a hash of the original — which would make the on-disk name unpredictable and break the
    /// by-name pairing two arms' run directories rely on. <c>ScenarioIdSurvivesSanitizeUnchanged</c>
    /// pins that this character round-trips.
    /// </summary>
    public const string ScenarioIdSeparator = "·";

    /// <summary>The manifest's <c>kind</c>, from the schema's closed enum.</summary>
    public const string RunKind = "benchmark";

    private readonly IOutputStore _store;
    private readonly SubjectIdentity _subject;

    /// <summary>Creates a runner that writes into <paramref name="store"/> under <paramref name="subject"/>.</summary>
    /// <param name="store">Where the run directory is written.</param>
    /// <param name="subject">Who was measured. Not the arm — an arm is a way to observe, a subject is who.</param>
    public BenchmarkRunner(IOutputStore store, SubjectIdentity subject)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
    }

    /// <summary>
    /// Observes every case through <paramref name="arm"/>, runs every admitted check on each, and
    /// writes one scenario row per (case, check).
    /// </summary>
    /// <param name="definition">What to measure.</param>
    /// <param name="arm">How to observe one case.</param>
    /// <param name="parentInvocationId">Optional parent invocation, recorded in the manifest.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The run, with every observation it recorded.</returns>
    /// <exception cref="ArgumentException">
    /// A check arrives without a usable floor — thrown by the door, before anything is observed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An arm smuggled an execution-bearing object through <see cref="EvalInput.Metadata"/>.
    /// </exception>
    /// <remarks>
    /// Whatever goes wrong, the manifest is COMPLETED before the exception leaves: a run directory
    /// with a manifest and no summary is unreadable by every consumer in this repository, and it
    /// reads as "still running" forever. The run records what it wrote, then the exception is
    /// rethrown — the caller is not told a failed run succeeded.
    /// </remarks>
    public async Task<BenchmarkRun> RunAsync(
        BenchmarkDefinition definition,
        BenchmarkArm arm,
        string? parentInvocationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arm);

        // 1 · Admit every check FIRST. AddEval is FloorAdmittedEval.Admit with a duplicate-key guard
        //     in front of it; a floorless check throws here, having observed nothing.
        var builder = new AgentEvalBuilder();
        foreach (var check in definition.Checks)
        {
            builder = builder.AddEval(check.Eval, check.Floor);
        }

        var evalRunner = await builder.BuildAsync(ct).ConfigureAwait(false);

        // 2 · The run directory. Harness carries the definition's identity AND version, because two
        //     versions are not the same measurement and the manifest is where that survives.
        var context = new RunContext(
            EvalProject: definition.Key,
            EvalProjectPath: string.Empty,
            Harness: $"BenchmarkRunner/{definition.Key}@{definition.Version}",
            Seed: null,
            ParentInvocationId: parentInvocationId,
            Kind: RunKind);

        var manifest = await _store.StartRunAsync(_subject, context, ct).ConfigureAwait(false);
        var observations = new List<CheckObservation>(definition.Cases.Count * definition.Checks.Count);

        try
        {
            foreach (var testCase in definition.Cases)
            {
                ct.ThrowIfCancellationRequested();

                // 3 · Identity comes from the CASE, never from a display name.
                var observed = await arm.Observe(testCase, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Arm '{arm.ArmId}' returned no EvalInput for case '{testCase.Id}'. An arm that cannot "
                        + "observe a case must say so in the input it returns, not by handing back null — a null "
                        + "here would be scored as an absence nobody declared.");

                var input = observed with { CaseId = testCase.Id };

                RefuseSmuggledExecutable(input, arm.ArmId, testCase.Id!);

                // 4 · Every admitted check, in registration order, each result carrying its floor.
                var results = await evalRunner.EvaluateEvalsAsync(input, ct).ConfigureAwait(false);

                if (results.Count != definition.Checks.Count)
                {
                    throw new InvalidOperationException(
                        $"The runner admitted {definition.Checks.Count} check(s) and got {results.Count} result(s) "
                        + "back. Rows are paired with checks by POSITION, so a length mismatch would silently "
                        + "attribute one check's verdict to another's key.");
                }

                // 5 · One row per (case, check).
                for (int i = 0; i < results.Count; i++)
                {
                    var check = definition.Checks[i];
                    var result = results[i];
                    var scenarioId = ScenarioId(testCase.Id!, check.Eval.Key);

                    var scenario = EvalResultPersistence.ToScenarioResult(
                        result,
                        scenarioId,
                        check.Eval.Name,
                        input: testCase.Input,
                        subjectModel: input.SubjectModel);

                    await _store.WriteScenarioResultAsync(manifest.Run.RunId, scenario, ct).ConfigureAwait(false);

                    observations.Add(new CheckObservation(
                        check.Eval.Key,
                        ToObservation(result, testCase.Id!, arm.ArmId),
                        result));
                }
            }
        }
        finally
        {
            // 6 · No orphan manifest, whatever happened. The summary describes what was WRITTEN.
            await _store.CompleteRunAsync(manifest, BuildSummary(observations, manifest.Run.RunId), ct)
                .ConfigureAwait(false);
        }

        return new BenchmarkRun(manifest.Run.RunId, arm.ArmId, definition, observations);
    }

    /// <summary>The scenario id for one (case, check) pair. Unique in a run, identical across runs.</summary>
    /// <param name="caseId">The case.</param>
    /// <param name="checkKey">The check's eval key.</param>
    /// <returns>The composed id.</returns>
    public static string ScenarioId(string caseId, string checkKey) =>
        $"{caseId}{ScenarioIdSeparator}{checkKey}";

    /// <summary>
    /// Refuses an arm that put something EXECUTABLE in <see cref="EvalInput.Metadata"/>.
    /// </summary>
    /// <remarks>
    /// 🔴 The recorded smuggle, and the reason <see cref="BenchmarkArm"/> binds the subject in a typed
    /// field instead: the four duck-typed benchmark families pass an agent through
    /// <c>Metadata["agent"]</c> (<c>OwaspBenchmarkRun.cs:137</c>) and fish it back out by string key.
    /// It compiles, it has no contract, and it turns a data bag into a second execution path that
    /// nothing type-checks. A DATA record in <see cref="EvalInput.Metadata"/> is fine and stays fine —
    /// only agents, chat clients and delegates are refused, and the refusal happens before any check
    /// runs so nothing is scored against a smuggled subject.
    /// </remarks>
    private static void RefuseSmuggledExecutable(EvalInput input, string armId, string caseId)
    {
        if (input.Metadata is not { Count: > 0 } metadata) return;

        foreach (var (key, value) in metadata)
        {
            var what = value switch
            {
                IEvaluableAgent => nameof(IEvaluableAgent),
                IChatClient => nameof(IChatClient),
                Delegate => nameof(Delegate),
                _ => null,
            };

            if (what is null) continue;

            throw new InvalidOperationException(
                $"Arm '{armId}' put a {what} in EvalInput.Metadata[\"{key}\"] on case '{caseId}', so the run was "
                + "refused before any check ran. Metadata is DATA. An execution-bearing object in it is a second, "
                + "untyped way to run the subject — the shape the duck-typed benchmark families use via "
                + "Metadata[\"agent\"] — and it breaks at the first rename with a null reference three layers from "
                + "the cause. Bind the subject in BenchmarkArm, where it has a signature.");
        }
    }

    /// <summary>
    /// Projects one result into the meta lane's five-field tuple.
    /// </summary>
    /// <remarks>
    /// The VALUE carried is the score, not the pass bit, so partial credit survives to the analysis
    /// and the caller chooses the threshold with <c>RepCollapse</c>'s <c>passAt</c>. Collapsing to a
    /// Bernoulli outcome is the collapse's job; doing it here would decide the threshold for every
    /// consumer at the point where the least is known.
    /// </remarks>
    private static Observation ToObservation(EvalResult result, string caseId, string armId) =>
        result.Score.CensusBucket() switch
        {
            MeasurementState.NotApplicable => Observation.NotApplicable(caseId, armId),
            MeasurementState.NotMeasured => Observation.NotMeasured(caseId, armId),
            _ => Observation.Measured(caseId, armId, result.Score.Value),
        };

    /// <summary>
    /// The run's counts and verdict, over the schema's closed buckets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No floor is consulted. This is the repository's existing pass/fail semantics written down:
    /// <c>skipped</c> is everything not <see cref="MeasurementState.Measured"/>, <c>warnings</c> is
    /// the <c>warn</c> label, <c>failed</c> is a real measurement that did not pass, and
    /// <c>passed</c> is the rest. The four buckets are disjoint and reconcile against <c>total</c>.
    /// </para>
    /// <para>
    /// ⚠ <b><c>PENDING</c>, not <c>PASS</c>, when nothing was measured.</b> A run whose every row is
    /// inapplicable has an empty denominator, and a verdict computed over one is the silent-<c>{}</c>
    /// shape — green from an instrument that measured nothing. It is also NOT ADR-031 S4's
    /// <c>VOID</c>: no exit code and no control ledger are claimed here (Q5).
    /// </para>
    /// <para>
    /// <c>Metrics</c> carries the CENSUS and nothing else. An aggregate score across checks would
    /// pool rows whose floors differ — a mean over incomparable things — and the census is the number
    /// that has to sit beside any such mean anyway.
    /// </para>
    /// </remarks>
    private static RunSummary BuildSummary(IReadOnlyList<CheckObservation> observations, string runId)
    {
        int passed = 0, failed = 0, warnings = 0, skipped = 0;
        int notApplicable = 0, notMeasured = 0;

        foreach (var observation in observations)
        {
            var score = observation.Result.Score;
            var bucket = score.CensusBucket();

            if (bucket != MeasurementState.Measured)
            {
                skipped++;
                if (bucket == MeasurementState.NotApplicable) notApplicable++; else notMeasured++;
                continue;
            }

            if (string.Equals(score.Label, "warn", StringComparison.Ordinal)) warnings++;
            else if (score.Passed) passed++;
            else failed++;
        }

        var total = observations.Count;
        var verdict =
            failed > 0 ? "FAIL"
            : passed + failed + warnings == 0 ? "PENDING"
            : warnings > 0 ? "WARN"
            : "PASS";

        return new RunSummary(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: verdict,
            Stats: new RunStats(total, passed, failed, warnings, skipped),
            Metrics: new Dictionary<string, double>
            {
                ["benchmark.rows"] = total,
                ["benchmark.measured"] = passed + failed + warnings,
                ["benchmark.not_applicable"] = notApplicable,
                ["benchmark.not_measured"] = notMeasured,
            });
    }
}
