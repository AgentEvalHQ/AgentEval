// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Runner;

/// <summary>
/// One finished line of an event stream ([STRM-3]): its 1-based <paramref name="Number"/>, and the event it holds, or
/// null when the line is not an I-JSON object valid against the reader <c>runner-event</c> schema (<c>event-invalid</c>:
/// it takes no part in the other checks).
/// </summary>
public sealed record AefStreamLine(int Number, JsonObject? Event);

/// <summary>
/// A runner's event stream as a stream verifier reads it (contracts/aef/1/spec/06-runners.md, [STRM-2], [STRM-3]): its
/// finished lines (a last line without LF is still being written and is not read), each read on its own; or, when the
/// finished lines break [ENC-5] or [ENC-7] (a CR, a blank line, a byte-order mark), only that <see cref="Problem"/>
/// (<c>encoding</c>), and no line. A stream of more lines than [ENC-17] allows is <c>limit</c> the same way ([ENC-18]: a
/// part is never read as the whole).
/// </summary>
public sealed class AefEventStream
{
    internal AefEventStream(string? problem, IReadOnlyList<AefStreamLine> lines)
    {
        Problem = problem;
        Lines = lines;
    }

    /// <summary><c>encoding</c> or <c>limit</c> for the stream as a whole (reported at <c>stream</c>), or null.</summary>
    public string? Problem { get; }

    /// <summary>The finished lines, in order (none when <see cref="Problem"/> is set).</summary>
    public IReadOnlyList<AefStreamLine> Lines { get; }

    /// <summary>The valid events, in order: what the rules other than <c>event-invalid</c> read.</summary>
    public IEnumerable<JsonObject> Events => Lines.Select(l => l.Event).OfType<JsonObject>();

    /// <summary>A stream of events already read (each valid against the reader runner-event schema), numbered from 1.</summary>
    public static AefEventStream Of(IEnumerable<JsonNode> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return new AefEventStream(null, [.. events.Select((e, i) => new AefStreamLine(i + 1, e as JsonObject ?? throw new ArgumentException("An event is a JSON object.", nameof(events))))]);
    }
}

/// <summary>
/// The runner protocol (contracts/aef/1/spec/06-runners.md): plan-to-runner matching ([PLAN-7]), the verification of a
/// finished event stream line by line ([STRM-3]), and the check that the runs a stream names keep to its plan
/// ([STRM-4]). Vectors: contracts/aef/1/conformance/protocol/.
/// </summary>
public static class RunnerEventStream
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "job.sealed", "job.failed", "job.cancelled", "job.refused" };

    private static readonly string[] Provenance = ["planId", "planDigest", "jobId", "runnerId"];

    /// <summary>
    /// [PLAN-7], as far as the manifest tells: a runner takes a plan when it can take it (it carries every tag of the
    /// plan's runnerSelector, supports the plan's provider, gives the plan's target mode, and, for a remote-zone plan, is
    /// in the plan's zone) and knows the plan's provider, isolation, content capture, target mode (an absent one is
    /// <c>live</c>, known) and credential schemes and purposes: what this version's writer schema accepts there
    /// ([VER-8]). A runner refuses a plan holding a value it does not know, even one its own manifest lists. The target
    /// mode it gives is one of its manifest's <c>targetModes</c> ([PLAN-6]; a manifest without them gives <c>live</c>
    /// only), compared as written with the plan's (<c>live</c> when the plan has none), round 7. Whether it can resolve
    /// the plan's suites and credentials and keep to its limits is the job's question (<see cref="AefScriptedRunner"/>),
    /// not the manifest's.
    /// </summary>
    public static bool Matches(JsonNode plan, JsonNode runner) => WhyNot(plan, runner) is null;

    /// <summary>
    /// Why the runner the manifest describes does not take the plan as far as its manifest tells ([PLAN-7], see
    /// <see cref="Matches"/>), for a <c>job.refused</c>'s reason; null when it takes it. The reason names a credential by
    /// its <c>name</c> and its place, never by its <c>path</c> ([PLAN-3]).
    /// </summary>
    public static string? WhyNot(JsonNode plan, JsonNode runner)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);
        if (Unknown(plan) is { } unknown)
        {
            return $"the plan's {unknown} is a value this version does not know ([PLAN-7], [VER-8])";
        }

        var tags = AefNode.Strings(runner["tags"]).ToHashSet(StringComparer.Ordinal);
        if (AefNode.Items(plan["runnerSelector"]).FirstOrDefault(t => AefNode.String(t) is not { } tag || !tags.Contains(tag)) is { } missing)
        {
            return $"the runner does not carry the tag {missing?.ToJsonString()} the plan's runnerSelector names ([PLAN-7])";
        }

        var providers = AefNode.Strings(runner["providers"]).ToHashSet(StringComparer.Ordinal);
        if (AefNode.String(plan["provider"]) is not { } provider || !providers.Contains(provider))
        {
            return $"the runner does not support the plan's provider {plan["provider"]?.ToJsonString()} ([PLAN-7])";
        }

        if (!TargetModesOf(runner).Contains(TargetModeOf(plan), StringComparer.Ordinal))
        {
            return $"the plan asks for the target mode {TargetModeOf(plan)}, and the runner gives {string.Join(", ", TargetModesOf(runner))} only ([PLAN-6], [PLAN-7])";
        }

        if (AefNode.String(plan["isolation"]) == "remote-zone" && AefNode.String(runner["networkZone"]) != AefNode.String(plan["zone"]))
        {
            return "the runner is not in the network zone the plan's remote-zone isolation names ([PLAN-7])";
        }

        return null;
    }

    /// <summary>The target mode a plan asks for ([PLAN-1]): its <c>targetMode</c> as written, <c>live</c> when it has none.</summary>
    public static string TargetModeOf(JsonNode plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan["targetMode"] is { } mode ? AefNode.String(mode) ?? "" : "live";
    }

    /// <summary>
    /// The target modes a runner's manifest says it gives ([PLAN-6]): its <c>targetModes</c> as written, or <c>live</c>
    /// only when it names none.
    /// </summary>
    public static IReadOnlyList<string> TargetModesOf(JsonNode runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner["targetModes"] is null ? ["live"] : [.. AefNode.Strings(runner["targetModes"])];
    }

    // The first value [PLAN-7] needs the runner to know that this version's writer schema does not accept there ([VER-8]),
    // named for a reason (a credential by its place and name, never its path), or null when it knows them all.
    private static string? Unknown(JsonNode plan)
    {
        static bool Known(string schema, JsonNode? value) => value is null || AefSchemas.Writer.IsValid(schema, value);

        foreach (var field in new[] { "provider", "isolation", "contentCapture", "targetMode" })
        {
            if (!Known($"run-plan#/properties/{field}", plan[field]))
            {
                return field;
            }
        }

        var i = 0;
        foreach (var credential in AefNode.Items(plan["credentialRefs"]))
        {
            i++;
            foreach (var field in new[] { "scheme", "purpose" })
            {
                if (!Known($"run-plan#/properties/credentialRefs/items/properties/{field}", AefNode.Get(credential, field)))
                {
                    return $"credential {i} ({AefNode.String(AefNode.Get(credential, "name")) ?? "no name"}) {field}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a stream's bytes ([STRM-2], [STRM-3]): the finished lines, up to and including the last LF, each with
    /// WP1's strict reader (<see cref="AefJsonReader"/>: I-JSON, within the limits of [ENC-17]) and the reader
    /// <c>runner-event</c> schema. A line that is neither is kept as invalid; finished lines whose framing breaks [ENC-5]
    /// or [ENC-7] make the whole stream an <c>encoding</c> problem.
    /// </summary>
    public static AefEventStream Read(ReadOnlySpan<byte> bytes)
    {
        var file = AefNdjson.Read(bytes[..AefNdjson.CompleteLength(bytes)]);
        if (file.Problem is { } problem)
        {
            return new AefEventStream(problem.Code, []);
        }

        return new AefEventStream(null, [.. file.Lines.Select(l =>
            new AefStreamLine(l.Number, l.Value is { } e && AefSchemas.Reader.IsValid("runner-event", e) ? e : null))]);
    }

    /// <summary>
    /// [STRM-3] over events already read and valid (numbered from 1): see <see cref="Verify(AefEventStream, JsonNode?, string?)"/>.
    /// </summary>
    public static IReadOnlyList<(string Where, string Problem)> Verify(IReadOnlyList<JsonNode> events, JsonNode? plan = null, string? planDigest = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Verify(AefEventStream.Of(events), plan, planDigest);
    }

    /// <summary>
    /// Every problem of [STRM-3] as (where, problem): where is <c>event:&lt;n&gt;</c> (the 1-based line) or
    /// <c>stream</c>; problems in event order, then by code (its bytes), with <c>stream</c> last. A stream that is one
    /// <see cref="AefEventStream.Problem"/> gives only it, at <c>stream</c>. A line that is not a valid event is
    /// <c>event-invalid</c> and takes no part in the other checks: the first event is the first valid one, and the event
    /// after an invalid line is not checked for <c>seq</c> (the count goes on from the value it writes). Empty when the
    /// stream keeps every rule.
    /// </summary>
    /// <param name="stream">The stream as read (<see cref="Read"/>).</param>
    /// <param name="plan">The run plan the job ran, when known: planId and its limits are checked.</param>
    /// <param name="planDigest">The SHA-256 (lower-case hex) of the plan file's bytes, when known.</param>
    public static IReadOnlyList<(string Where, string Problem)> Verify(AefEventStream stream, JsonNode? plan = null, string? planDigest = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Problem is { } whole)
        {
            return [("stream", whole)];
        }

        var problems = new List<(string, string)>();
        long previousSeq = 0;
        AefTime? previousAt = null;
        double? spent = null;
        bool terminal = false, accepted = false, overTime = false, seen = false, afterInvalid = false;
        string? job = null;
        AefTime start = default;
        long cases = 0;
        var announced = new Dictionary<string, string>(StringComparer.Ordinal);
        var limit = AefNode.String(AefNode.At(plan, "limits", "timeout")) is { } t ? TimeoutSeconds(t) : (long?)null;
        var maxUsd = AefNode.Number(AefNode.At(plan, "limits", "maxUsd"));
        var allowedCases = AefNode.Number(AefNode.At(plan, "limits", "cases"));
        foreach (var line in stream.Lines)
        {
            var where = $"event:{line.Number}";
            if (line.Event is not { } e)
            {
                problems.Add((where, "event-invalid"));
                afterInvalid = true;
                continue;
            }

            var found = new List<string>();
            var kind = AefNode.String(e["kind"]) ?? "";
            var at = AefNode.Time(e["at"]) ?? default;
            if (!seen)
            {
                // The first valid event: it opens the job, and the times and job id that follow are compared with it.
                if (kind is not ("job.accepted" or "job.refused")) found.Add("first");
                job = AefNode.String(e["jobId"]);
                start = at;
                seen = true;
            }

            var seq = (long)(AefNode.Number(e["seq"]) ?? 0);
            if (!afterInvalid && seq != previousSeq + 1) found.Add("seq");
            previousSeq = seq;
            afterInvalid = false;

            if (AefNode.String(e["jobId"]) != job) found.Add("job-id");

            if (previousAt is { } before && at < before) found.Add("time");
            previousAt = at;
            if (limit is { } seconds && !overTime && at > start.AddSeconds(seconds))
            {
                found.Add("over-time");
                overTime = true;
            }

            if (terminal) found.Add("after-terminal");

            switch (kind)
            {
                case "job.accepted" or "job.refused":
                    if (kind == "job.accepted" && accepted) found.Add("accepted-twice");
                    accepted |= kind == "job.accepted";
                    if (plan is not null && AefNode.String(e["planId"]) != AefNode.String(plan["planId"])) found.Add("plan-id");
                    if (planDigest is not null && AefNode.String(e["planDigest"]) != planDigest) found.Add("plan-digest");
                    break;
                case "plan.estimated" when AefNode.Number(e["usdLow"]) > AefNode.Number(e["usdHigh"]):
                    found.Add("estimate");
                    break;
                case "spend.updated":
                    var value = AefNode.Number(e["spentUsd"]) ?? 0;
                    if (spent is { } last && value < last) found.Add("spend-decreased");
                    if (maxUsd is { } max && value > max) found.Add("over-budget");
                    spent = value;
                    break;
                case "case.completed":
                    cases++;
                    if (allowedCases is { } allowed && cases == (long)allowed + 1) found.Add("over-cases");
                    break;
                case "evidence.produced":
                    var runId = AefNode.String(e["runId"]) ?? "";
                    var hash = AefNode.String(e["runHash"]) ?? "";
                    if (announced.TryGetValue(runId, out var first) && first != hash) found.Add("run-hash-changed");
                    announced.TryAdd(runId, hash);
                    break;
            }

            if (kind is "job.sealed" or "job.failed")
            {
                var named = AefNode.Strings(e["runs"]).ToList();
                if (named.Any(r => !announced.ContainsKey(r))) found.Add("unannounced-run");
                if (announced.Keys.Any(r => !named.Contains(r, StringComparer.Ordinal))) found.Add("unsealed-run");
            }

            if (Terminal.Contains(kind)) terminal = true;
            problems.AddRange(found.Order(AefProblemOrder.Utf8).Select(p => (where, p)));
        }

        if (!terminal) problems.Add(("stream", "no-terminal"));
        return problems;
    }

    /// <summary>[STRM-4] over events already read and valid: see <see cref="Conform(AefEventStream, JsonNode, AefRunStore)"/>.</summary>
    public static IReadOnlyList<(string Where, string Problem)> Conform(IReadOnlyList<JsonNode> events, JsonNode plan, AefRunStore runs)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Conform(AefEventStream.Of(events), plan, runs);
    }

    /// <summary>
    /// [STRM-4]: every problem as (where, problem) of the runs a job.sealed or job.failed names, where is
    /// <c>run:&lt;runId&gt;</c>, and of the job's limits over the runs found, where is <c>job</c>; ordered by where (its
    /// UTF-8 bytes), then by problem. Only the events [STRM-3] reads take part: a line that is not a valid event does
    /// not, and a stream that is one problem as a whole has no events. A run named twice is checked, and counted, once.
    /// A run is the folder of <paramref name="runs"/> with its runId whose run hash ([SEAL-4]) is the one the first
    /// evidence.produced for it announced and that is intact (§4.5; a blob withheld by a redaction the store's trust
    /// policy authorizes leaves it intact, [OVL-10]); when there is none, <c>run-missing</c> (no folder has the runId) or
    /// <c>run-hash</c> is its only problem. Empty when the runs keep to the plan.
    /// </summary>
    /// <param name="stream">The stream as read (<see cref="Read"/>).</param>
    /// <param name="plan">The run plan the job ran, valid against the reader run-plan schema.</param>
    /// <param name="runs">The runs the job produced, found by their run.json.</param>
    /// <exception cref="IOException">A run's file cannot be read.</exception>
    public static IReadOnlyList<(string Where, string Problem)> Conform(AefEventStream stream, JsonNode plan, AefRunStore runs)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runs);

        var events = stream.Events.ToList();
        var accepted = events.FirstOrDefault(e => AefNode.String(e["kind"]) == "job.accepted");
        var terminal = events.FirstOrDefault(e => AefNode.String(e["kind"]) is { } kind && Terminal.Contains(kind));
        var announced = new Dictionary<string, string>(StringComparer.Ordinal);
        var named = new List<string>();
        foreach (var e in events)
        {
            var kind = AefNode.String(e["kind"]);
            if (kind == "evidence.produced" && AefNode.String(e["runId"]) is { } runId && AefNode.String(e["runHash"]) is { } hash)
            {
                announced.TryAdd(runId, hash);   // the first announcement ([STRM-3] reports a change)
            }
            else if (kind is "job.sealed" or "job.failed")
            {
                foreach (var name in AefNode.Strings(e["runs"]))
                {
                    if (!named.Contains(name, StringComparer.Ordinal)) named.Add(name);
                }
            }
        }

        var problems = new List<(string Where, string Problem)>();
        var checkedRuns = new List<AefStoredRun>();
        foreach (var runId in named)
        {
            List<string> found;
            if (!runs.Has(runId))
            {
                found = ["run-missing"];
            }
            else if (!announced.TryGetValue(runId, out var hash) || runs.FindIntact(runId, hash) is not { } run)
            {
                found = ["run-hash"];
            }
            else
            {
                found = Departures(run.Run, plan, accepted, terminal);
                if (AefNode.Number(AefNode.At(run.Documents.Summary, "cost", "totalUsd")) is null)
                {
                    found.Add("no-cost");   // the budget cannot be checked without it
                }

                checkedRuns.Add(run);
            }

            problems.AddRange(found.Select(p => ($"run:{runId}", p)));
        }

        // The job's limits, over the runs found, each once: a runner cannot pass by splitting its work across runs. The
        // costs are summed exactly and rounded once, so the order the runs are named in does not change the total.
        var cost = AefSummaryCalculator.ExactSum(checkedRuns.Select(r => AefNode.Number(AefNode.At(r.Documents.Summary, "cost", "totalUsd")) ?? 0));
        if (AefNode.Number(AefNode.At(plan, "limits", "maxUsd")) is { } maxUsd && cost > maxUsd)
        {
            problems.Add(("job", "over-budget"));
        }

        // A case is its run's suite (ref and version) with its caseId (round 7, [PLAN-8]): one case id in two suites counts
        // twice, and in two runs of one suite once.
        if (AefNode.Number(AefNode.At(plan, "limits", "cases")) is { } allowed
            && checkedRuns.SelectMany(r => r.Documents.Results.Objects
                    .Where(l => !l.Value.ContainsKey("parentResultId"))
                    .Select(l => AefNode.String(l.Value["caseId"]))
                    .OfType<string>()
                    .Select(caseId => (
                        Ref: AefNode.String(AefNode.At(r.Run, "suite", "ref")),
                        Version: AefNode.String(AefNode.At(r.Run, "suite", "version")),
                        CaseId: caseId)))
                .Distinct().Count() > allowed)
        {
            problems.Add(("job", "over-cases"));
        }

        return [.. problems.OrderBy(p => p.Where, AefProblemOrder.Utf8).ThenBy(p => p.Problem, AefProblemOrder.Utf8)];
    }

    /// <summary>
    /// How an intact, announced run departs from the plan: [STRM-4]'s codes at <c>run:&lt;runId&gt;</c>. accepted and
    /// terminal are the stream's first job.accepted and first terminal event, or null.
    /// </summary>
    private static List<string> Departures(JsonObject run, JsonNode plan, JsonObject? accepted, JsonObject? terminal)
    {
        var found = new List<string>();

        if (accepted is null || run["provenance"] is not JsonObject provenance
            || Provenance.Any(k => AefNode.String(provenance[k]) is not { } value || value != AefNode.String(accepted[k])))
            found.Add("provenance");

        if (AefNode.String(AefNode.At(run, "subject", "ref")) != AefNode.String(AefNode.At(plan, "subject", "ref"))
            || AefNode.String(AefNode.At(run, "subject", "version")) != AefNode.String(AefNode.At(plan, "subject", "version")))
            found.Add("subject");

        // Where: the deployment and the endpoint the plan names (an absent value is not the plan's).
        if ((AefNode.String(AefNode.At(plan, "subject", "deployment")) is { } deployment && AefNode.String(AefNode.At(run, "deployment", "ref")) != deployment)
            || (AefNode.String(AefNode.At(plan, "subject", "endpoint")) is { } endpoint && AefNode.String(AefNode.At(run, "deployment", "endpoint")) != endpoint))
            found.Add("deployment");

        // When: within the job, at full precision ([ENC-8]). A run that started before the job was accepted was adopted,
        // not produced. Each half only when both its times exist.
        if ((AefNode.Time(accepted?["at"]) is { } acceptedAt && AefNode.Time(run["startedAt"]) is { } started && started < acceptedAt)
            || (AefNode.Time(terminal?["at"]) is { } terminalAt && AefNode.Time(run["endedAt"]) is { } ended && ended > terminalAt))
            found.Add("time");

        // The plan's suite and version, and, when the plan names one, its content digest.
        var suite = run["suite"];
        if (AefNode.String(AefNode.Get(suite, "ref")) is not { } suiteRef
            || !AefNode.Objects(plan["suites"]).Any(s =>
                AefNode.String(s["ref"]) == suiteRef
                && AefNode.String(s["version"]) == AefNode.String(AefNode.Get(suite, "version"))
                && (AefNode.String(s["digest"]) is not { } digest || digest == AefNode.String(AefNode.Get(suite, "digest")))))
            found.Add("suite");

        // The run's judges are the models that graded it ([RUN-9]): a sub-list of the plan's (round 8), each one of the
        // plan's by model and rubric digest (an absent value equals only an absent one), in the plan's order, none named
        // twice. None, or some of the plan's, is within. Checked only when the plan names judges.
        static List<(string?, string?)> Judges(JsonNode? list) =>
            [.. AefNode.Objects(list).Select(j => (AefNode.String(j["model"]), AefNode.String(j["rubricDigest"])))];
        var planned = Judges(plan["judges"]);
        if (planned.Count > 0 && !IsSubList(Judges(run["judges"]), planned))
            found.Add("judges");

        // [RUN-11], [VER-8]: an absent or unknown contentCapture reads as on.
        if ((AefNode.String(run["contentCapture"]) == "off" ? "off" : "on") != AefNode.String(plan["contentCapture"]))
            found.Add("content-capture");

        // [STRM-4] target-mode (round 7): the plan's targetMode, live when the plan has none, compared as written ([RUN-7]).
        if (AefNode.String(AefNode.At(run, "execution", "targetMode")) != (AefNode.String(plan["targetMode"]) ?? "live"))
            found.Add("target-mode");

        return found;
    }

    // Whether every item of run is one of planned, in planned's order, none twice: each matched at a later place of planned
    // than the item before it.
    private static bool IsSubList(List<(string?, string?)> run, List<(string?, string?)> planned)
    {
        if (run.Distinct().Count() != run.Count)
        {
            return false;
        }

        var at = 0;
        foreach (var judge in run)
        {
            while (at < planned.Count && planned[at] != judge)
            {
                at++;
            }

            if (at == planned.Count)
            {
                return false;
            }

            at++;
        }

        return true;
    }

    /// <summary>A plan's timeout in seconds: an AEF duration of days, hours and minutes ([ENC-9], <see cref="AefDuration"/>).</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static long TimeoutSeconds(string text) => AefDuration.Seconds(text);
}
