// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Runs;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// <c>produce SCENARIO OUT</c> (spec 09 §9.2.1 "A scenario", §9.3): writes the closed run a scenario describes into the
/// folder OUT with AgentEval.Results' writer, as a Producer writes one. The driver maps the scenario into the writer's
/// model: <c>run</c> into an <see cref="AefRunHeader"/> and the status, end and abort reason <see cref="AefRunWriter.Close"/>
/// takes, <c>metrics</c> into <see cref="AefMetric"/>s, <c>summary</c> into an <see cref="AefSummary"/> (as
/// <c>summarize</c> reads its request), and each case's result tree into <see cref="AefResult"/> lines, deriving what
/// [RES-4]–[RES-8] derive from the facts: each line's <c>trial</c>, a rollup's <c>trials</c> (<c>n</c>, <c>passed</c>,
/// <c>agree</c>, the case's trial aggregation and <c>k</c>), and a composite's aggregation counts and decisive ids. The
/// writer computes result ids and parents, checks every line against the writer schema and §3.9, computes summary.json
/// ([SUM-2]–[SUM-9]) and verifies the run it closed. Prints <c>{"results": the number of lines}</c>.
/// </summary>
/// <remarks>
/// A scenario not of §9.2.1's shape (a member it does not have, a value of another type), a run that is not closed, and
/// every input error §9.3 lists, are input errors (exit code 2), and OUT is left as it was: nothing written. The scenario
/// is read and every line planned before anything is written; what the writer itself refuses (a schema, a rule of §3.9,
/// a summary entry) removes what it had written.
/// </remarks>
internal static class ProduceOps
{
    private const string Usage = "produce SCENARIO OUT";

    /// <summary>The operation.</summary>
    public static int Produce(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 2, 2, Usage);
        var output = args[1];
        if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
        {
            throw new UsageException($"{output}: OUT is a folder that does not exist yet or is empty (spec 09 §9.3)");
        }

        Scenario scenario;
        try
        {
            scenario = Scenario.Read(DriverIO.Document(args[0]));
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            throw new UsageException($"{args[0]}: {e.Message}");
        }

        var existed = Directory.Exists(output);
        try
        {
            var writer = AefRunWriter.Create(output, scenario.Header);
            writer.SetMetrics(scenario.Metrics, scenario.MetricsExt);
            var handles = new Dictionary<PlannedLine, AefResultHandle>();
            foreach (var line in scenario.Lines)
            {
                handles[line] = writer.AddResult(line.Result, line.Parent is null ? null : handles[line.Parent]);
            }

            writer.SetSummary(scenario.Summary);
            writer.Close(scenario.Status, scenario.EndedAt, scenario.AbortReason);
            return DriverIO.Print(stdout, new JsonObject { ["results"] = scenario.Lines.Count });
        }
        catch (Exception e) when (DriverIO.IsInputError(e))
        {
            Clear(output, existed);
            throw new UsageException($"{args[0]}: the writer refuses the run: {e.Message}");
        }
    }

    // Leaves OUT as it was before the call: absent, or empty.
    private static void Clear(string output, bool existed)
    {
        if (!Directory.Exists(output))
        {
            return;
        }

        if (!existed)
        {
            Directory.Delete(output, recursive: true);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(output))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    /// <summary>One line the writer adds: the line's facts, derived ones included, and the line it is a child of.</summary>
    internal sealed class PlannedLine(AefResult result, PlannedLine? parent)
    {
        public AefResult Result { get; } = result;

        public PlannedLine? Parent { get; } = parent;
    }

    /// <summary>A scenario read and its lines planned: what the writer is given.</summary>
    internal sealed class Scenario
    {
        public required AefRunHeader Header { get; init; }

        public required AefRunStatus Status { get; init; }

        public required AefTime EndedAt { get; init; }

        public string? AbortReason { get; init; }

        public required IReadOnlyList<AefMetric> Metrics { get; init; }

        public JsonObject? MetricsExt { get; init; }

        public required AefSummary Summary { get; init; }

        public required IReadOnlyList<PlannedLine> Lines { get; init; }

        /// <summary>Reads a scenario ({"run", "metrics", "cases", "summary"}) and plans its lines.</summary>
        /// <exception cref="FormatException">Not of §9.2.1's shape, or an input error of §9.3.</exception>
        public static Scenario Read(JsonObject json)
        {
            var scenario = new Facts(json, "the scenario");
            var run = RunOf(scenario.Object("run"));
            var (metrics, metricsExt) = MetricsOf(scenario.Object("metrics"));
            var cases = scenario.Objects("cases");
            AefSummary summary;
            try
            {
                summary = WriteSideOps.Request(scenario.Raw("summary") ?? throw new FormatException("the scenario: summary is required: an object"));
            }
            catch (UsageException e)
            {
                throw new FormatException($"the scenario's summary: {e.Message}");
            }

            scenario.Done();
            return new Scenario
            {
                Header = run.Header,
                Status = run.Status,
                EndedAt = run.EndedAt,
                AbortReason = run.AbortReason,
                Metrics = metrics,
                MetricsExt = metricsExt,
                Summary = summary,
                Lines = Plan(run.Header.RunId, cases),
            };
        }
    }

    // ------------------------------------------------------------------ the result trees

    // A node's facts (§9.2.1): what is written as given, and what a composite's aggregation is derived from.
    private sealed record Node(
        string Path, AefEvaluator Evaluator, AefState State, string? Reason, IReadOnlyList<AefScore>? Scores, AefSeverity? Severity,
        string? Lane, AefComponent? Component, AggregationFacts? Aggregation, IReadOnlyList<Node> Children)
    {
        public IEnumerable<Node> Tree => Children.SelectMany(c => c.Tree).Prepend(this);
    }

    // The producer's facts about a composite ([RES-6]): what only it knows. The counts and the decisive ids are derived.
    private sealed record AggregationFacts(AefAggregationStrategy Strategy, AefRulePath RulePath, double? Threshold, double? Score, IReadOnlyList<string>? Decisive);

    // [RES-1]: the measured states; the others are typed absences.
    private static bool IsMeasured(AefState state) =>
        state is AefState.Passed or AefState.Failed or AefState.Warn or AefState.Inconclusive or AefState.Scored;

    // The lines of every case, parents before their children: a case run in trials as its trial trees (each line with
    // its trial), then its rollups (the case's own tree, each line with trials over the trial lines at its path).
    private static List<PlannedLine> Plan(string runId, IReadOnlyList<Facts> cases)
    {
        var lines = new List<PlannedLine>();
        var keys = new HashSet<(string Case, string Path, int? Trial)>();
        foreach (var facts in cases)
        {
            var caseId = facts.Text("caseId");
            var trials = facts.OptObject("trials");
            var tree = NodeOf(facts, null);
            if (trials is null)
            {
                Emit(runId, caseId, tree, null, null, null, lines, keys);
                continue;
            }

            var aggregation = trials.Enum<AefTrialAggregation>("aggregation");
            var k = trials.OptInteger("k");
            var trees = trials.Objects("trees").Select(t => NodeOf(t, null)).ToList();
            trials.Done();
            if (trees.Count == 0)
            {
                throw new FormatException($"case {caseId}: trials.trees holds one tree per trial, at least one (§9.2.1)");
            }

            // [RES-8]: each trial's lines are the case's at its own paths, rooted at the case's path; and the case's tree
            // (its rollups) has a node at each path its trial trees have, and at no other.
            if (trees.FirstOrDefault(t => t.Path != tree.Path) is { } elsewhere)
            {
                throw new FormatException($"case {caseId}: a trial tree is rooted at {elsewhere.Path}, not at the case's path {tree.Path} ([RES-8])");
            }

            var trialLines = trees.SelectMany(t => t.Tree).GroupBy(n => n.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var rollupPaths = tree.Tree.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
            if (trialLines.Keys.FirstOrDefault(p => !rollupPaths.Contains(p)) is { } noRollup)
            {
                throw new FormatException($"case {caseId}: its trials have a line at {noRollup}, and its tree no rollup there ([RES-8])");
            }

            if (rollupPaths.FirstOrDefault(p => !trialLines.ContainsKey(p)) is { } noTrial)
            {
                throw new FormatException($"case {caseId}: its tree has a rollup at {noTrial}, where no trial has a line ([RES-8])");
            }

            for (var trial = 0; trial < trees.Count; trial++)
            {
                Emit(runId, caseId, trees[trial], trial, null, null, lines, keys);
            }

            // n, passed and agree over the trial lines at the rollup's path: agree is true exactly when they are all in
            // one state ([RES-8]).
            AefTrials RollupAt(string path)
            {
                var here = trialLines[path];
                return new AefTrials(
                    here.Count, here.Count(n => n.State == AefState.Passed), aggregation, here.Select(n => n.State).Distinct().Count() == 1, k);
            }

            Emit(runId, caseId, tree, null, RollupAt, null, lines, keys);
        }

        return lines;
    }

    // One node's line and its children's, parents first.
    private static void Emit(
        string runId, string caseId, Node node, int? trial, Func<string, AefTrials>? rollup, PlannedLine? parent,
        List<PlannedLine> lines, HashSet<(string, string, int?)> keys)
    {
        if (!keys.Add((caseId, node.Path, trial)))
        {
            throw new FormatException(
                $"case {caseId} has two lines at {node.Path}{(trial is { } t ? $", trial {t.ToString(CultureInfo.InvariantCulture)}" : "")}: they would have one resultId ([RES-4])");
        }

        AefAggregation? aggregation = null;
        if (node.Aggregation is { } facts)
        {
            // [RES-5], [RES-6]: total the children, measured those in a measured state, the others counted by state,
            // and the decisive children by their ids.
            var absent = node.Children.Where(c => !IsMeasured(c.State)).GroupBy(c => c.State).ToDictionary(g => g.Key, g => (long)g.Count());
            long? Count(AefState state) => absent.TryGetValue(state, out var count) ? count : null;
            aggregation = new AefAggregation
            {
                Strategy = facts.Strategy,
                RulePath = facts.RulePath,
                Threshold = facts.Threshold,
                Score = facts.Score,
                Total = node.Children.Count,
                Measured = node.Children.Count(c => IsMeasured(c.State)),
                Unmeasured = absent.Count == 0 ? null : new AefUnmeasured
                {
                    NotMeasured = Count(AefState.NotMeasured),
                    NotApplicable = Count(AefState.NotApplicable),
                    Skipped = Count(AefState.Skipped),
                    Error = Count(AefState.Error),
                    Pending = Count(AefState.Pending),
                },
                Decisive = facts.Decisive?.Select(path => AefResultId.Compute(runId, caseId, path, trial)).ToList(),
            };
        }

        var line = new PlannedLine(
            new AefResult
            {
                CaseId = caseId,
                Path = node.Path,
                Trial = trial,
                Trials = rollup?.Invoke(node.Path),
                Evaluator = node.Evaluator,
                State = node.State,
                Reason = node.Reason,
                Scores = node.Scores,
                Severity = node.Severity,
                Lane = node.Lane,
                Component = node.Component,
                Aggregation = aggregation,
            },
            parent);
        lines.Add(line);
        foreach (var child in node.Children)
        {
            Emit(runId, caseId, child, trial, rollup, line, lines, keys);
        }
    }

    // A node of §9.2.1, with its children: `parent` is the parent's path, or null for a case's or a trial's root.
    private static Node NodeOf(Facts facts, string? parent)
    {
        var path = facts.Text("path");
        if (parent is not null && !(path.StartsWith(parent + "/", StringComparison.Ordinal) && path.Length > parent.Length + 1 && !path[(parent.Length + 1)..].Contains('/')))
        {
            throw new FormatException($"the child {path} of {parent}: a child's path is its parent's, then / and a name without / ([RES-5], [RES-8])");
        }

        var evaluator = facts.Object("evaluator");
        var node = new Node(
            path,
            new AefEvaluator(evaluator.Text("id"), evaluator.OptText("version")),
            facts.Enum<AefState>("state"),
            facts.OptText("reason"),
            facts.OptObjects("scores")?.Select(s =>
            {
                var score = new AefScore { Metric = s.Text("metric"), Value = s.Number("value"), Normalized = s.OptNumber("normalized"), Label = s.OptText("label") };
                s.Done();
                return score;
            }).ToList(),
            facts.OptEnum<AefSeverity>("severity"),
            facts.OptText("lane"),
            ComponentOf(facts.OptObject("component")),
            null,
            []);
        evaluator.Done();

        // [RES-3]: a closed run has no pending line; only the producer can say whether it was skipped or an error.
        if (node.State == AefState.Pending)
        {
            throw new FormatException($"{path} is pending: a closed run has no pending line ([RES-3])");
        }

        // [RES-5]: every child has its component, and only a child.
        if ((parent is null) != (node.Component is null))
        {
            throw new FormatException(parent is null
                ? $"{path}: component is a child's, and this is a root (§9.2.1)"
                : $"{path} is a child without component: its weight and whether it is required are the producer's ([RES-5])");
        }

        var children = (facts.OptObjects("children") ?? []).Select(c => NodeOf(c, path)).ToList();
        AggregationFacts? aggregation = null;
        if (facts.OptObject("aggregation") is { } a)
        {
            aggregation = new AggregationFacts(
                a.Enum<AefAggregationStrategy>("strategy"), a.Enum<AefRulePath>("rulePath"), a.OptNumber("threshold"), a.OptNumber("score"), a.OptStrings("decisive"));
            a.Done();

            // [RES-6]: a decisive id is a child's.
            if (aggregation.Decisive?.FirstOrDefault(d => !children.Any(c => c.Path == d)) is { } stranger)
            {
                throw new FormatException($"{path}: the decisive path {stranger} is no child's ([RES-6])");
            }
        }
        else if (children.Count > 0)
        {
            throw new FormatException($"{path} has children and no aggregation: its strategy and rulePath are the producer's ([RES-5])");
        }

        facts.Done();
        return node with { Aggregation = aggregation, Children = children };
    }

    private static AefComponent? ComponentOf(Facts? facts)
    {
        if (facts is null)
        {
            return null;
        }

        var component = new AefComponent(facts.Number("weight"), facts.Bool("required"));
        facts.Done();
        return component;
    }

    // ------------------------------------------------------------------ run.json and metrics.json

    private sealed record ClosedRun(AefRunHeader Header, AefRunStatus Status, AefTime EndedAt, string? AbortReason);

    // run.json as the writer's header, and what Close takes: a closed status, the end and the abort reason ([RUN-5]).
    private static ClosedRun RunOf(Facts run)
    {
        if (run.Text("schemaVersion") != AefRunWriter.SchemaVersion)
        {
            throw new FormatException($"run.schemaVersion: the writer writes AEF {AefRunWriter.SchemaVersion} ([VER-2])");
        }

        var status = run.Enum<AefRunStatus>("status");
        if (status == AefRunStatus.Running)
        {
            throw new FormatException("run.status is running: produce writes a closed run, with its summary ([RUN-2], [RUN-5])");
        }

        var producer = run.Object("producer");
        var runtime = producer.OptObject("runtime");
        var subject = run.Object("subject");
        var telemetry = subject.OptObject("telemetry");
        var deployment = run.OptObject("deployment");
        var suite = run.OptObject("suite");
        var policy = suite?.OptObject("executionPolicy");
        var otel = run.OptObject("otel");
        var costPolicy = run.OptObject("costPolicy");
        var provenance = run.OptObject("provenance");
        var execution = run.Object("execution");
        var imported = run.OptObject("imported");
        var header = new AefRunHeader
        {
            RunId = run.Text("runId"),
            Producer = new AefProducer
            {
                Name = producer.Text("name"),
                Version = producer.Text("version"),
                Runtime = runtime is null ? null : new AefRuntime { Name = runtime.Text("name"), Version = runtime.OptText("version") },
            },
            Subject = new AefSubject
            {
                Ref = subject.Text("ref"),
                Kind = subject.Enum<AefSubjectKind>("kind"),
                Version = subject.OptText("version"),
                Environment = subject.OptText("environment"),
                ExternalIds = subject.OptExternalIds("externalIds"),
                Telemetry = telemetry is null ? null : new AefSubjectTelemetry { AgentId = telemetry.OptText("agentId"), ServiceName = telemetry.OptText("serviceName") },
            },
            Deployment = deployment is null ? null : new AefDeployment
            {
                Ref = deployment.Text("ref"),
                Environment = deployment.OptText("environment"),
                Endpoint = deployment.OptText("endpoint"),
                ExternalIds = deployment.OptExternalIds("externalIds"),
            },
            Suite = suite is null ? null : new AefSuite
            {
                Ref = suite.Text("ref"),
                Version = suite.Text("version"),
                Digest = suite.OptText("digest"),
                Frozen = suite.OptBool("frozen"),
                ExecutionPolicy = policy is null ? null : new AefExecutionPolicy
                {
                    TrialsPerCase = policy.Int32("trialsPerCase"),
                    RequirePasses = policy.OptInt32("requirePasses"),
                    Aggregation = policy.OptEnum<AefTrialAggregation>("aggregation"),
                    K = policy.OptInteger("k"),
                },
            },
            Judges = run.OptObjects("judges")?.Select(JudgeOf).ToList(),
            Config = ConfigOf(run.OptObject("config")),
            StartedAt = run.Time("startedAt"),
            Otel = otel is null ? null : new AefOtel
            {
                SemconvVersion = otel.OptText("semconvVersion"),
                Dialects = otel.OptStrings("dialects"),
                SchemaUrls = otel.OptStrings("schemaUrls"),
            },

            // [RUN-11]: a reader treats a run without contentCapture as on, and the writer always writes it.
            ContentCapture = run.OptEnum<AefContentCapture>("contentCapture") ?? AefContentCapture.On,
            CostPolicy = costPolicy is null ? null : new AefCostPolicy { MaxUsd = costPolicy.OptNumber("maxUsd"), PriceTable = costPolicy.OptText("priceTable") },
            Ext = run.Raw("ext"),
            Provenance = provenance is null ? null : new AefProvenance
            {
                PlanId = provenance.Text("planId"),
                PlanDigest = provenance.Text("planDigest"),
                JobId = provenance.Text("jobId"),
                RunnerId = provenance.Text("runnerId"),
            },
            Execution = new AefExecution { TargetMode = execution.Enum<AefTargetMode>("targetMode"), Stimulus = execution.OptEnum<AefStimulus>("stimulus") },
            Imported = imported is null ? null : new AefImported { From = imported.Text("from"), Asserted = imported.OptStrings("asserted") ?? throw new FormatException("run.imported.asserted is required: a list of strings") },
        };
        var endedAt = run.OptTime("endedAt") ?? throw new FormatException("run.endedAt: a closed run has one ([RUN-5])");
        var abortReason = run.OptText("abortReason");
        foreach (var facts in new[] { producer, runtime, subject, telemetry, deployment, suite, policy, otel, costPolicy, provenance, execution, imported, run })
        {
            facts?.Done();
        }

        return new ClosedRun(header, status, endedAt, abortReason);
    }

    private static AefJudge JudgeOf(Facts judge)
    {
        var calibration = judge.OptObject("calibration");
        var result = new AefJudge
        {
            Model = judge.Text("model"),
            Provider = judge.OptText("provider"),
            Mode = judge.OptEnum<AefJudgeMode>("mode"),
            PanelSize = judge.OptInt32("panelSize"),
            RubricDigest = judge.OptText("rubricDigest"),
            Calibration = calibration is null ? null : new AefCalibration
            {
                LabelSet = calibration.Text("labelSet"),
                N = calibration.Integer("n"),
                Accuracy = calibration.OptNumber("accuracy"),
                Kappa = calibration.OptNumber("kappa"),
                DangerousErrors = calibration.OptInteger("dangerousErrors"),
                MeasuredAt = calibration.Time("measuredAt"),
            },
        };
        calibration?.Done();
        judge.Done();
        return result;
    }

    // config: its thresholds, and the rest as the producer's settings (the schema leaves config open).
    private static AefRunConfig? ConfigOf(Facts? config)
    {
        if (config is null)
        {
            return null;
        }

        Dictionary<string, AefThreshold>? thresholds = null;
        if (config.OptObject("thresholds") is { } given)
        {
            thresholds = new Dictionary<string, AefThreshold>(StringComparer.Ordinal);
            foreach (var key in given.Names)
            {
                var threshold = given.Object(key);
                thresholds[key] = new AefThreshold(threshold.Enum<AefThresholdOp>("op"), threshold.Number("value"));
                threshold.Done();
            }

            given.Done();
        }

        var settings = config.Rest();
        return new AefRunConfig { Thresholds = thresholds, Settings = settings.Count == 0 ? null : settings };
    }

    private static (List<AefMetric> Metrics, JsonObject? Ext) MetricsOf(Facts metrics)
    {
        if (metrics.Text("schemaVersion") != AefRunWriter.SchemaVersion)
        {
            throw new FormatException($"metrics.schemaVersion: the writer writes AEF {AefRunWriter.SchemaVersion} ([VER-2])");
        }

        var list = metrics.Objects("metrics").Select(m =>
        {
            var metric = new AefMetric
            {
                Id = m.Text("id"),
                Kind = m.Enum<AefMetricKind>("kind"),
                Direction = m.Enum<AefMetricDirection>("direction"),
                Scale = m.Scale("scale"),
                Unit = m.OptText("unit"),
                Description = m.OptText("description"),
            };
            m.Done();
            return metric;
        }).ToList();
        var ext = metrics.Raw("ext");
        metrics.Done();
        return (list, ext);
    }

    // ------------------------------------------------------------------ reading the scenario's objects

    /// <summary>
    /// An object of the scenario, read member by member, each by the type §9.2.1 (or the writer schema, for run.json and
    /// metrics.json) gives it; <see cref="Done"/> refuses a member that was not read. A null member is an absent one.
    /// </summary>
    private sealed class Facts(JsonObject json, string where)
    {
        private readonly HashSet<string> _read = new(StringComparer.Ordinal);

        public IEnumerable<string> Names => json.Select(m => m.Key).ToList();

        public string Text(string name) => OptText(name) ?? throw Missing(name, "a string");

        public string? OptText(string name) => Take(name) switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            _ => throw Wrong(name, "a string"),
        };

        public double Number(string name) => OptNumber(name) ?? throw Missing(name, "a number");

        public double? OptNumber(string name) => Take(name) switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => AefNumber(v),
            _ => throw Wrong(name, "a number"),
        };

        public long Integer(string name) => OptInteger(name) ?? throw Missing(name, "an integer");

        public long? OptInteger(string name) => OptNumber(name) is { } n
            ? Math.Floor(n) == n && Math.Abs(n) <= 9_007_199_254_740_991 ? (long)n : throw Wrong(name, "an integer")
            : null;

        public int Int32(string name) => OptInt32(name) ?? throw Missing(name, "an integer");

        public int? OptInt32(string name) => OptInteger(name) is { } n
            ? n is >= int.MinValue and <= int.MaxValue ? (int)n : throw Wrong(name, "an integer the writer holds")
            : null;

        public bool Bool(string name) => OptBool(name) ?? throw Missing(name, "true or false");

        public bool? OptBool(string name) => Take(name) switch
        {
            null => null,
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True,
            _ => throw Wrong(name, "true or false"),
        };

        public T Enum<T>(string name)
            where T : struct, System.Enum => OptEnum<T>(name) ?? throw Missing(name, "a name the writer schema lists");

        public T? OptEnum<T>(string name)
            where T : struct, System.Enum => OptText(name) is not { } text ? null
            : AefNames.TryParse<T>(text, out var value) ? value : throw Wrong(name, "a name the writer schema lists ([VER-2])");

        // An [ENC-8] time at its full precision (nine fraction digits included), as the writer's model holds it (AefTime):
        // written as given (n2-d), compared as a time by the judge.
        public AefTime Time(string name) => OptTime(name) ?? throw Missing(name, "a time");

        public AefTime? OptTime(string name) => OptText(name) is { } text ? AefTime.Parse(text) : null;

        public Facts Object(string name) => OptObject(name) ?? throw Missing(name, "an object");

        public Facts? OptObject(string name) => Take(name) switch
        {
            null => null,
            JsonObject o => new Facts(o, $"{where}.{name}"),
            _ => throw Wrong(name, "an object"),
        };

        public List<Facts> Objects(string name) => OptObjects(name) ?? throw Missing(name, "a list of objects");

        public List<Facts>? OptObjects(string name) => Take(name) switch
        {
            null => null,
            JsonArray a => [.. a.Select((item, i) => item is JsonObject o ? new Facts(o, $"{where}.{name}[{i.ToString(CultureInfo.InvariantCulture)}]") : throw Wrong(name, "a list of objects"))],
            _ => throw Wrong(name, "a list of objects"),
        };

        public List<string>? OptStrings(string name) => Take(name) switch
        {
            null => null,
            JsonArray a => [.. a.Select(item => item is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : throw Wrong(name, "a list of strings"))],
            _ => throw Wrong(name, "a list of strings"),
        };

        // An object as it is (ext, a summarize request): a copy.
        public JsonObject? Raw(string name) => Take(name) switch
        {
            null => null,
            JsonObject o => o.DeepClone().AsObject(),
            _ => throw Wrong(name, "an object"),
        };

        // externalIds: system name to id, or null when the system has none.
        public IReadOnlyDictionary<string, string?>? OptExternalIds(string name) => OptObject(name) is not { } ids ? null
            : ids.Names.ToDictionary(n => n, n => ids.Take(n) switch
            {
                null => null,
                JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
                _ => throw ids.Wrong(n, "a string or null"),
            }, StringComparer.Ordinal);

        // A metric's scale: "unbounded", or {"min", "max"}.
        public AefScale Scale(string name)
        {
            switch (Take(name))
            {
                case JsonValue v when v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "unbounded":
                    return AefScale.Unbounded;
                case JsonObject o:
                    var scale = new Facts(o, $"{where}.{name}");
                    var between = AefScale.Between(scale.Number("min"), scale.Number("max"));
                    scale.Done();
                    return between;
                default:
                    throw Wrong(name, "\"unbounded\" or {\"min\", \"max\"}");
            }
        }

        // The members not read yet, as they are (config's settings).
        public JsonObject Rest()
        {
            var rest = new JsonObject();
            foreach (var (name, value) in json.Where(m => !_read.Contains(m.Key)).ToList())
            {
                rest[name] = value?.DeepClone();
                _read.Add(name);
            }

            return rest;
        }

        /// <summary>Refuses a member that was not read: the scenario is not of §9.2.1's shape.</summary>
        public void Done()
        {
            var extra = json.Select(m => m.Key).Where(k => !_read.Contains(k)).ToList();
            if (extra.Count > 0)
            {
                throw new FormatException($"{where}: {string.Join(", ", extra)}: not a member it has (§9.2.1)");
            }
        }

        private JsonNode? Take(string name)
        {
            _read.Add(name);
            return json[name];
        }

        private FormatException Missing(string name, string what) => new($"{where}: {name} is required: {what}");

        private FormatException Wrong(string name, string what) => new($"{where}: {name} is not {what}");

        // A JSON number as binary64 ([ENC-4]).
        private static double AefNumber(JsonValue value) =>
            value.TryGetValue<double>(out var number) ? number : double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
