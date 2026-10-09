// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// What a checkpoint is about ([CKP-1]): the subject's <c>ref</c>, its exact <c>version</c>, and the deployment it names,
/// if any. A lane's runs must be of this subject (and deployment) to count ([LANE-1]).
/// </summary>
public sealed record CheckpointSubject(string Ref, string Version, string? Deployment)
{
    /// <summary>The manifest's <c>subject</c>.</summary>
    /// <exception cref="FormatException">The manifest has no subject <c>ref</c> and <c>version</c> (the reader schema requires them).</exception>
    public static CheckpointSubject Read(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var subject = manifest["subject"];
        return new CheckpointSubject(
            AefNode.String(AefNode.Get(subject, "ref")) ?? throw new FormatException("A checkpoint names its subject's ref."),
            AefNode.String(AefNode.Get(subject, "version")) ?? throw new FormatException("A checkpoint names its subject's exact version."),
            AefNode.String(AefNode.Get(subject, "deployment")));
    }
}

/// <summary>
/// A lane's result (contracts/aef/1/spec/05-checkpoints.md, §5.3): its status, the subject version the evidence is for
/// ([LANE-9]), when the oldest run it relied on closed (as written in the run), and, for an incomparable comparison, the
/// axes that differ, in the rule's order ([LANE-6]).
/// </summary>
public sealed record LaneResult(LaneEvidenceStatus Status, string SubjectVersion, string OldestClosedAt, IReadOnlyList<string>? Axes = null)
{
    /// <summary>A status's wire name: <c>passed</c>, <c>failed</c>, <c>not_measured</c> or <c>incomparable</c>.</summary>
    public static string StatusName(LaneEvidenceStatus status) => status switch
    {
        LaneEvidenceStatus.Passed => "passed",
        LaneEvidenceStatus.Failed => "failed",
        LaneEvidenceStatus.NotMeasured => "not_measured",
        LaneEvidenceStatus.Incomparable => "incomparable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>The result as a decision input's <c>lanes[].result</c> holds it (schema <c>decision#/$defs/input</c>).</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["status"] = StatusName(Status),
            ["subjectVersion"] = SubjectVersion,
            ["oldestClosedAt"] = OldestClosedAt,
        };
        if (Axes is { Count: > 0 } axes)
        {
            json["axes"] = new JsonArray([.. axes.Select(a => (JsonNode?)a)]);
        }

        return json;
    }

    /// <summary>The result as the decision function takes it.</summary>
    public LaneEvidence ToEvidence() => new(Status, SubjectVersion, AefTime.Parse(OldestClosedAt), Axes);
}

/// <summary>
/// One lane of a checkpoint evaluated against its runs: its rule as read, its recomputed result (null when it has no
/// evidence), the problems of [CKP-8] about the runs it names (<c>run-missing</c>, <c>run-unverified</c> at
/// <c>lanes/&lt;lane&gt;/runs/&lt;runId&gt;</c>), and whether recomputing it read something this version does not know
/// (<paramref name="ReadsUnknown"/>, <see cref="LaneEvaluator.ReadsUnknown"/>): then a recorded result is not compared
/// with it, and the lane is <c>unverifiable</c>.
/// </summary>
public sealed record LaneEvaluation(string Lane, LaneRule Rule, LaneResult? Result, IReadOnlyList<AefProblem> Problems, bool ReadsUnknown = false);

/// <summary>
/// Lane evaluation (contracts/aef/1/spec/05-checkpoints.md, §5.2, §5.3): <c>LaneResult(rule, runs, baseline?)</c> turns
/// a lane's sealed runs into the result the decision function takes. Pure apart from reading the runs: it reads only
/// their files, through a <see cref="AefRunStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// A lane's runs are found by <c>runId</c> and run hash ([CKP-8], [LANE-9]); a lane with no runs, or none of whose runs is
/// found, has no result. Otherwise the result's version and age are read over the runs found that are intact and bound
/// to the checkpoint's subject, deployment and the rule's suite (the baseline excluded; with none, the checkpoint's
/// version and the evaluation time), and its status from the rule, over eligible runs only ([LANE-1]): a lane any of
/// whose runs is missing or not eligible is <c>not_measured</c> (it fails closed). A run eligible but for another <c>subject.version</c> counts, and the
/// result then carries that version, which the decision function reports as <c>wrong-version</c>.
/// </para>
/// <para>
/// A lane that names one run twice relies on one run: its runs are the distinct pairs of <c>runId</c> and run hash (so
/// a run listed twice is neither two runs for <c>evidence-present</c> nor counted twice toward a severity rule's
/// <c>minimumN</c>); DEC-1 already makes a lane's evidence a set of run hashes. The spec does not say it (Q4-39 W4-5).
/// </para>
/// </remarks>
public static class LaneEvaluator
{
    private static readonly string[] Unmeasured = ["inconclusive", "not_measured", "skipped", "error", "pending"];

    /// <summary>
    /// Evaluates one lane of a manifest against <paramref name="store"/>: the problems of [CKP-8] about each run it
    /// names and about a comparison's baseline, and its result.
    /// </summary>
    /// <param name="lane">The manifest's lane (valid against the reader checkpoint schema).</param>
    /// <param name="subject">The checkpoint's subject.</param>
    /// <param name="store">Where its runs are found.</param>
    /// <param name="fallbackTime">
    /// The age of a lane none of whose runs found counts for it ([LANE-9]): the checkpoint's
    /// <c>decisionInput.evaluatedAt</c>, or for an undecided checkpoint the evaluation time the verifier is given.
    /// </param>
    /// <exception cref="FormatException">The lane's rule is not one the reader schema accepts.</exception>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static LaneEvaluation Evaluate(JsonObject lane, CheckpointSubject subject, AefRunStore store, string fallbackTime)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fallbackTime);
        var name = AefNode.String(lane["lane"]) ?? throw new FormatException("A lane has a name.");
        var rule = LaneRule.Read(lane["rule"]);
        var problems = new HashSet<AefProblem>();

        AefStoredRun? Locate(string? runId, string? runHash)
        {
            if (runId is null || runHash is null)
            {
                return null;
            }

            var found = store.Find(runId, runHash);
            if (found is null || !found.Intact)
            {
                problems.Add(new AefProblem($"lanes/{name}/runs/{runId}", found is null ? "run-missing" : "run-unverified"));
            }

            return found;
        }

        var refs = AefNode.Objects(lane["runs"])
            .Select(r => (RunId: AefNode.String(r["runId"]), RunHash: AefNode.String(r["runHash"])))
            .Distinct()
            .ToList();
        var runs = refs.Select(r => Locate(r.RunId, r.RunHash)).ToList();
        var baseline = rule is ComparisonRule comparison ? Locate(comparison.Baseline.RunId, comparison.Baseline.RunHash) : null;
        var readsUnknown = !AefSchemas.Writer.IsValid(RuleSchema, lane["rule"])
                           || runs.Append(baseline).OfType<AefStoredRun>().Any(run => ReadsUnknown(rule, run));

        return new LaneEvaluation(name, rule, Result(rule, runs, baseline, subject, fallbackTime), ByBytes(problems), readsUnknown);
    }

    /// <summary>The writer subschema a lane's rule is valid against when this version knows everything in it ([CKP-8]).</summary>
    public const string RuleSchema = "checkpoint#/$defs/laneRule";

    /// <summary>
    /// [CKP-8], [VER-8]: whether recomputing a lane reads, in <paramref name="run"/> (a found run of the lane, or its
    /// comparison's baseline, intact or not), a value this version does not know, one its writer schema does not accept
    /// at that field: an <c>execution.targetMode</c>; for a <c>severity</c> rule, a <c>severity</c> on one of the rule's
    /// lines (its summary lane and path, trial lines included, whatever their state); for a <c>comparison</c> rule, the
    /// <c>direction</c> of the compared metric in the run's metrics.json. A rule not valid against the writer schema is
    /// the other case ([CKP-8]); <see cref="Evaluate"/> checks both.
    /// </summary>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static bool ReadsUnknown(LaneRule rule, AefStoredRun run)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(run);
        if (AefNode.At(run.Run, "execution", "targetMode") is { } mode && !AefSchemas.Writer.IsValid(TargetModeSchema, mode))
        {
            return true;
        }

        switch (rule)
        {
            case SeverityRule severity:
                var lanes = SummaryLanes(run.Documents.Summary);
                return run.Documents.Results.Objects.Any(l =>
                    (severity.Lane is not { } lane || AefSummaryCalculator.Belongs(l.Value, lane, lanes))
                    && severity.InScope(AefNode.String(l.Value["path"]))
                    && l.Value["severity"] is { } value && !AefSchemas.Writer.IsValid(SeveritySchema, value));
            case ComparisonRule comparison:
                return Metric(run.Documents.Metrics, comparison.Metric)?["direction"] is { } direction
                       && !AefSchemas.Writer.IsValid(DirectionSchema, direction);
            default:
                return false;
        }
    }

    private const string TargetModeSchema = "run#/properties/execution/properties/targetMode";
    private const string SeveritySchema = "result#/properties/severity";
    private const string DirectionSchema = "metrics#/properties/metrics/items/properties/direction";

    /// <summary>
    /// <c>LaneResult(rule, runs, baseline?)</c> (§5.3): null when <paramref name="runs"/> holds none found.
    /// </summary>
    /// <param name="rule">The lane's rule.</param>
    /// <param name="runs">The lane's runs, each once, in lane order: the run found, or null for one not found.</param>
    /// <param name="baseline">A comparison's baseline as found, or null (not found, or not a comparison).</param>
    /// <param name="subject">The checkpoint's subject.</param>
    /// <param name="fallbackTime">The age when no run found counts for it, or none of those has a closing time ([LANE-9]).</param>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static LaneResult? Result(LaneRule rule, IReadOnlyList<AefStoredRun?> runs, AefStoredRun? baseline, CheckpointSubject subject, string fallbackTime)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(fallbackTime);
        var found = runs.OfType<AefStoredRun>().ToList();
        if (found.Count == 0)
        {
            return null;
        }

        // [LANE-9]: over the runs found that are intact and bound to the checkpoint's subject, deployment and the rule's
        // suite (a run of something else never gives the lane its version or its age): the version is the checkpoint's
        // unless one of them is for another (the first in lane order), and the age is their oldest closing time; with
        // none, the checkpoint's version and the evaluation time.
        var counted = found.Where(r => r.Intact && IsBound(r, subject, rule.Suite)).ToList();
        var version = counted.Select(r => AefNode.String(AefNode.At(r.Run, "subject", "version")))
            .FirstOrDefault(v => v is not null && !string.Equals(v, subject.Version, StringComparison.Ordinal)) ?? subject.Version;
        var oldest = counted.Select(r => r.ClosedAt).OfType<string>()
            .Select((text, order) => (Text: text, Time: AefTime.Parse(text), Order: order))
            .OrderBy(c => c.Time).ThenBy(c => c.Order)
            .Select(c => c.Text).FirstOrDefault() ?? fallbackTime;
        LaneResult Of(LaneEvidenceStatus status, IReadOnlyList<string>? axes = null) => new(status, version, oldest, axes);

        // [LANE-1]: every run found and eligible, or the lane fails closed.
        if (runs.Any(r => r is null || !IsEligible(r, subject, rule.Suite)))
        {
            return Of(LaneEvidenceStatus.NotMeasured);
        }

        return rule switch
        {
            ThresholdRule threshold => Of(Threshold(threshold, found)),
            SeverityRule severity => Of(Severity(severity, found)),
            EvidencePresentRule present => Of(found.Count >= present.Runs ? LaneEvidenceStatus.Passed : LaneEvidenceStatus.NotMeasured),
            ComparisonRule comparison => Comparison(comparison, found, baseline, subject, Of),
            _ => Of(LaneEvidenceStatus.NotMeasured),   // a kind this version does not know ([VER-3])
        };
    }

    /// <summary>
    /// [LANE-1]: whether a run found may count for the lane: intact, closed <c>completed</c>, <c>live</c> (an unknown
    /// target mode reads as <c>mocked</c>, §7.3), with a closing time ([LANE-9]), of the checkpoint's subject (with a
    /// <c>subject.version</c>, unless <paramref name="anyVersion"/>: a comparison's baseline), in its deployment when it
    /// names one, and of the rule's suite when it names one.
    /// </summary>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static bool IsEligible(AefStoredRun run, CheckpointSubject subject, SuiteBinding? suite, bool anyVersion = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(subject);
        var header = run.Run;
        return run.Intact
               && AefNode.String(header["status"]) == "completed"
               && AefNode.String(AefNode.At(header, "execution", "targetMode")) == "live"
               && run.ClosedAt is not null
               && (anyVersion || AefNode.String(AefNode.At(header, "subject", "version")) is not null)
               && IsBound(run, subject, suite);
    }

    /// <summary>
    /// [LANE-1]'s binding: the run is of the checkpoint's subject (its <c>subject.ref</c>), in its deployment when the
    /// checkpoint names one, and of the rule's suite when the rule names one. Only an intact run so bound gives a lane
    /// its version and its age ([LANE-9]).
    /// </summary>
    public static bool IsBound(AefStoredRun run, CheckpointSubject subject, SuiteBinding? suite)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(subject);
        var header = run.Run;
        return AefNode.String(AefNode.At(header, "subject", "ref")) == subject.Ref
               && (subject.Deployment is null || AefNode.String(AefNode.At(header, "deployment", "ref")) == subject.Deployment)
               && (suite is null || suite.Matches(header["suite"]));
    }

    // [LANE-2]: per run, the summary entry against the value; failed beats not measured beats passed.
    private static LaneEvidenceStatus Threshold(ThresholdRule rule, IReadOnlyList<AefStoredRun> runs)
    {
        if (!ThresholdRule.KnownOps.Contains(rule.Op, StringComparer.Ordinal))
        {
            return LaneEvidenceStatus.NotMeasured;   // an operator this version does not know (§7.3)
        }

        var statuses = runs.Select(run =>
        {
            var entry = SummaryEntry(run.Documents.Summary, rule.Lane, rule.Metric, rule.Path);
            var n = AefNode.Number(entry?["n"]);
            var method = AefNode.String(AefNode.At(entry, "aggregate", "method"));
            if (entry is null || n is not > 0 || (rule.MinimumN is { } minimum && n < minimum)
                || (AefNode.Has(entry, "aggregate") && !AefSummaryCalculator.DefinedMethods.Contains(method ?? "", StringComparer.Ordinal))
                || AefNode.Number(entry["value"]) is not { } value)
            {
                return LaneEvidenceStatus.NotMeasured;   // no entry, nothing measured, too few, or a value nobody can check ([SUM-8])
            }

            return rule.Holds(value) == true ? LaneEvidenceStatus.Passed : LaneEvidenceStatus.Failed;
        }).ToList();

        return statuses.Contains(LaneEvidenceStatus.Failed) ? LaneEvidenceStatus.Failed
            : statuses.Contains(LaneEvidenceStatus.NotMeasured) ? LaneEvidenceStatus.NotMeasured
            : LaneEvidenceStatus.Passed;
    }

    // [LANE-3]: a failure worse than max fails (a trial's too); anything undecided, or fewer decided lines than minimumN
    // (rollups, not trials), is not measured.
    private static LaneEvidenceStatus Severity(SeverityRule rule, IReadOnlyList<AefStoredRun> runs)
    {
        if (Rank(rule.Max) is not { } max || !SeverityRule.KnownMax.Contains(rule.Max, StringComparer.Ordinal))
        {
            return LaneEvidenceStatus.NotMeasured;   // a max this version does not know (§7.3)
        }

        bool failed = false, undecided = false;
        long decided = 0;
        foreach (var run in runs)
        {
            var lanes = SummaryLanes(run.Documents.Summary);
            foreach (var (_, line) in run.Documents.Results.Objects)
            {
                if ((rule.Lane is { } lane && !AefSummaryCalculator.Belongs(line, lane, lanes))
                    || !rule.InScope(AefNode.String(line["path"])))
                {
                    continue;
                }

                var state = AefNode.String(line["state"]);
                if (line.ContainsKey("trial"))
                {
                    // A failing trial beyond max fails the lane (a red team's single success), even when its rollup
                    // passes; trial lines take no other part: in the counts below, a case is its rollup.
                    failed |= state is "failed" or "warn" && (Rank(AefNode.String(line["severity"])) ?? Critical) > max;
                    continue;
                }

                switch (state)
                {
                    case "failed" or "warn":
                        decided++;
                        failed |= (Rank(AefNode.String(line["severity"])) ?? Critical) > max;   // missing or unknown: critical
                        break;
                    case "passed":
                        decided++;
                        break;
                    case { } other when Unmeasured.Contains(other, StringComparer.Ordinal):
                        undecided = true;
                        break;
                }
            }
        }

        return failed ? LaneEvidenceStatus.Failed
            : undecided || decided < (rule.MinimumN ?? 1) ? LaneEvidenceStatus.NotMeasured
            : LaneEvidenceStatus.Passed;
    }

    private const int Critical = 4;

    private static int? Rank(string? severity) => severity switch
    {
        "none" => 0,
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        "critical" => Critical,
        _ => null,
    };

    // [LANE-5]–[LANE-8].
    private static LaneResult Comparison(
        ComparisonRule rule, IReadOnlyList<AefStoredRun> runs, AefStoredRun? baseline, CheckpointSubject subject,
        Func<LaneEvidenceStatus, IReadOnlyList<string>?, LaneResult> of)
    {
        // One candidate, and a baseline checked as the candidate is, except for its version.
        if (runs.Count != 1 || baseline is null || !IsEligible(baseline, subject, rule.Suite, anyVersion: true))
        {
            return of(LaneEvidenceStatus.NotMeasured, null);
        }

        var candidate = runs[0];
        var differing = rule.Axes.Where(axis => !SameOnAxis(axis, candidate.Run, baseline.Run)).ToList();
        if (differing.Count > 0)
        {
            return of(LaneEvidenceStatus.Incomparable, differing);
        }

        // The direction from the candidate's metrics.json; none (or one this version does not know) cannot regress.
        var direction = AefNode.String(Metric(candidate.Documents.Metrics, rule.Metric)?["direction"]);
        if (direction is not ("higher_better" or "lower_better"))
        {
            return of(LaneEvidenceStatus.NotMeasured, null);
        }

        var before = MeasuredByCase(baseline, rule);
        int regressions = 0, improvements = 0;
        foreach (var (caseId, c) in MeasuredByCase(candidate, rule))
        {
            if (before.TryGetValue(caseId, out var b) && c != b)
            {
                if ((c < b) == (direction == "higher_better"))
                {
                    regressions++;
                }
                else
                {
                    improvements++;
                }
            }
        }

        if ((long)regressions + improvements < rule.MinimumPairs)
        {
            return of(LaneEvidenceStatus.NotMeasured, null);
        }

        return of(SignTest.PAtMost(regressions, improvements, rule.Significance) ? LaneEvidenceStatus.Failed : LaneEvidenceStatus.Passed, null);
    }

    // [LANE-6]: whether two run.json documents agree on an axis; an absent value equals only an absent value, and an axis
    // this version does not name counts as differing.
    private static bool SameOnAxis(string axis, JsonObject a, JsonObject b)
    {
        bool Same(string parent, string field, string? other = null) =>
            JsonNode.DeepEquals(AefNode.At(a, parent, field), AefNode.At(b, parent, field))
            && (other is null || JsonNode.DeepEquals(AefNode.At(a, parent, other), AefNode.At(b, parent, other)));
        bool SameList(string field) => Judges(a, field).SequenceEqual(Judges(b, field), Comparer.Instance);
        return axis switch
        {
            "subject" => Same("subject", "ref"),
            "suite" => Same("suite", "ref", "version"),
            "suite-content" => Same("suite", "digest"),
            "judges" => SameList("model"),
            "rubrics" => SameList("rubricDigest"),
            "target-mode" => Same("execution", "targetMode"),
            "deployment" => Same("deployment", "ref"),
            "producer" => Same("producer", "name", "version"),
            _ => false,
        };
    }

    // The list of judges[].<field>, in order; no judges is the empty list.
    private static IEnumerable<JsonNode?> Judges(JsonObject run, string field) =>
        AefNode.Items(run["judges"]).Select(j => AefNode.Get(j, field));

    // [LANE-7] with [SUM-3], [SUM-4]: each case's measured value at the rule's path in its lane (trial lines excluded:
    // their rollup is the case's result), by the run's own declaration of the metric's kind.
    private static Dictionary<string, double> MeasuredByCase(AefStoredRun run, ComparisonRule rule)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (AefNode.String(Metric(run.Documents.Metrics, rule.Metric)?["kind"]) is not { } kind)
        {
            return values;   // a metric the run does not declare is measured nowhere in it
        }

        var lanes = SummaryLanes(run.Documents.Summary);
        foreach (var (_, line) in run.Documents.Results.Objects)
        {
            if (line.ContainsKey("trial") || AefNode.String(line["path"]) != rule.Path || !AefSummaryCalculator.Belongs(line, rule.Lane, lanes)
                || AefNode.String(line["caseId"]) is not { } caseId)
            {
                continue;
            }

            if (AefSummaryCalculator.ValueOf(line, rule.Metric, kind) is (false, { } value))
            {
                values.TryAdd(caseId, value);
            }
        }

        return values;
    }

    /// <summary>
    /// Problems of [CKP-8] in the order of §3.9: by path, then by code, both by their bytes. Their paths
    /// (<c>lanes/&lt;lane&gt;/runs/&lt;runId&gt;</c>) are not line paths of an NDJSON file, so a colon in a runId never
    /// orders them by number.
    /// </summary>
    internal static IReadOnlyList<AefProblem> ByBytes(IEnumerable<AefProblem> problems) =>
        [.. problems.Distinct().OrderBy(p => p.Path, AefProblemOrder.Utf8).ThenBy(p => p.Code, AefProblemOrder.Utf8)];

    private static JsonObject? Metric(JsonObject? metrics, string id) =>
        AefNode.Objects(metrics?["metrics"]).FirstOrDefault(m => AefNode.String(m["id"]) == id);

    private static List<string> SummaryLanes(JsonObject? summary) =>
        [.. AefNode.Objects(summary?["lanes"]).Select(l => AefNode.String(l["lane"])).OfType<string>()];

    private static JsonObject? SummaryEntry(JsonObject? summary, string lane, string metric, string path) =>
        AefNode.Objects(summary?["lanes"])
            .Where(l => AefNode.String(l["lane"]) == lane)
            .SelectMany(l => AefNode.Objects(l["metrics"]))
            .FirstOrDefault(e => AefNode.String(e["metric"]) == metric && AefNode.String(e["path"]) == path);

    private sealed class Comparer : IEqualityComparer<JsonNode?>
    {
        public static readonly Comparer Instance = new();

        public bool Equals(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x, y);

        public int GetHashCode(JsonNode? obj) => 0;
    }
}
