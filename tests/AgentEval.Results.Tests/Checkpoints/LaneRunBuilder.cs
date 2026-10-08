using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Tests.Checkpoints;

/// <summary>
/// Builds small, valid, sealed runs for lane evaluation and plan conformance: run.json for subject
/// <c>agent:shop/assistant</c> (live, completed), metrics (<c>m</c> a higher-better score, <c>lat</c> a lower-better
/// duration, <c>flat</c> a score with no direction, <c>ok</c> a rate), result lines with ids per [RES-4], and a summary
/// whose entries are recomputed as a verifier recomputes them, so the run is intact unless a test breaks it.
/// </summary>
internal sealed class LaneRunBuilder
{
    public const string Subject = "agent:shop/assistant";

    public LaneRunBuilder(string runId, string? version = "v7")
    {
        RunId = runId;
        Run = Obj($$"""
            {"schemaVersion": "1.0", "runId": "{{runId}}", "status": "completed",
             "producer": {"name": "test", "version": "1.0"}, "subject": {"ref": "{{Subject}}", "kind": "agent"},
             "startedAt": "2026-10-01T00:00:00Z", "endedAt": "2026-10-05T00:00:00Z", "execution": {"targetMode": "live"} }
            """);
        if (version is not null)
        {
            Run["subject"]!["version"] = version;
        }

        Metrics = Obj("""
            {"schemaVersion": "1.0", "metrics": [
              {"id": "m", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
              {"id": "lat", "kind": "duration", "direction": "lower_better", "scale": {"min": 0, "max": 100000}},
              {"id": "flat", "kind": "score", "direction": "none", "scale": {"min": 0, "max": 1}},
              {"id": "ok", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]}
            """);
    }

    public string RunId { get; }

    public JsonObject Run { get; }

    public JsonObject Metrics { get; }

    public List<JsonObject> Results { get; } = [];

    /// <summary>Summary entries to write: (lane, metric, path, aggregate method or null, the value written for a method AEF does not define).</summary>
    public List<(string Lane, string Metric, string Path, string? Method, double? Written)> Entries { get; } = [];

    public double? CostUsd { get; set; }

    public static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// A result line; <paramref name="scores"/> are (metric, value) pairs; a typed absence gets a reason. A rollup line
    /// ([RES-8]) gives <paramref name="rollup"/>: how many trials, and how many of them passed.
    /// </summary>
    public LaneRunBuilder Line(string caseId, string path, string state, string? severity = null, string? lane = "quality",
        long? trial = null, string? parentCaseId = null, string? parentPath = null, (int N, int Passed)? rollup = null,
        params (string Metric, double Value)[] scores)
    {
        var line = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["resultId"] = AefResultId.Compute(RunId, caseId, path, trial),
            ["caseId"] = caseId,
            ["path"] = path,
            ["evaluator"] = new JsonObject { ["id"] = "code:check" },
            ["state"] = state,
        };
        if (lane is not null) line["lane"] = lane;
        if (trial is { } t) line["trial"] = t;
        if (rollup is { } r)
        {
            line["trials"] = new JsonObject { ["n"] = r.N, ["passed"] = r.Passed, ["aggregation"] = "MajorityVote", ["agree"] = r.Passed == 0 || r.Passed == r.N };
        }

        if (parentCaseId is not null) line["parentResultId"] = AefResultId.Compute(RunId, parentCaseId, parentPath ?? path, null);
        if (severity is not null) line["severity"] = severity;
        if (state is "not_measured" or "not_applicable" or "skipped" or "error" or "pending") line["reason"] = "not run";
        if (scores.Length > 0)
        {
            line["scores"] = new JsonArray([.. scores.Select(s => (JsonNode?)new JsonObject { ["metric"] = s.Metric, ["value"] = s.Value })]);
        }

        Results.Add(line);
        return this;
    }

    /// <summary>A measured line of metric <c>m</c> (or <paramref name="metric"/>) at <paramref name="path"/> in lane quality.</summary>
    public LaneRunBuilder Score(string caseId, double value, string path = "p", string metric = "m", string state = "passed") =>
        Line(caseId, path, state, scores: (metric, value));

    public LaneRunBuilder Entry(string lane, string metric, string path, string? method = null, double? written = null)
    {
        Entries.Add((lane, metric, path, method, written));
        return this;
    }

    /// <summary>Writes the run into <paramref name="root"/>/<paramref name="folder"/> and seals it (unless <paramref name="seal"/> is false).</summary>
    public string Write(string root, string? folder = null, bool seal = true)
    {
        var dir = Path.Combine(root, folder ?? RunId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "run.json"), AefJsonWriter.Document(Run));
        File.WriteAllBytes(Path.Combine(dir, "metrics.json"), AefJsonWriter.Document(Metrics));
        File.WriteAllBytes(Path.Combine(dir, "results.ndjson"), [.. Results.SelectMany(AefJsonWriter.Line)]);
        if (AefNode.String(Run["status"]) is "completed" or "aborted")
        {
            File.WriteAllBytes(Path.Combine(dir, "summary.json"), AefJsonWriter.Document(Summary()));
        }

        if (seal)
        {
            Seal(dir, Run);
        }

        return dir;
    }

    /// <summary>Seals the files in <paramref name="dir"/> as a producer does.</summary>
    public static void Seal(string dir, JsonObject run)
    {
        var manifest = AefRunFolder.Open(dir).Manifest();
        var subjects = new JsonArray();
        foreach (var entry in manifest.Entries)
        {
            subjects.Add(new JsonObject { ["name"] = entry.Path, ["digest"] = new JsonObject { ["sha256"] = entry.Sha256 } });
        }

        var seal = new JsonObject
        {
            ["_type"] = "https://in-toto.io/Statement/v1",
            ["subject"] = subjects,
            ["predicateType"] = "https://agenteval.dev/aef/1/evidence",
            ["predicate"] = new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["runId"] = run["runId"]?.DeepClone(),
                ["runHash"] = manifest.RunHash,
                ["producer"] = run["producer"]!.DeepClone(),
                ["subject"] = new JsonObject { ["ref"] = run["subject"]!["ref"]!.DeepClone(), ["version"] = run["subject"]!["version"]?.DeepClone() },
                ["deployment"] = run["deployment"] is JsonObject d ? new JsonObject { ["ref"] = d["ref"]?.DeepClone() } : null,
                ["suite"] = run["suite"]?.DeepClone(),
                ["judges"] = new JsonArray([.. AefNode.Objects(run["judges"]).Select(j => (JsonNode?)new JsonObject
                {
                    ["model"] = j["model"]?.DeepClone(),
                    ["rubricDigest"] = j["rubricDigest"]?.DeepClone(),
                })]),
                ["closedAt"] = run["endedAt"]?.DeepClone(),
                ["sealedBy"] = "producer",
                ["sealedAt"] = "2026-12-31T00:00:00Z",   // no earlier than it closed ([SEAL-6])
            },
        };
        if (seal["predicate"]!["subject"]!["version"] is null)
        {
            seal["predicate"]!["subject"]!.AsObject().Remove("version");
        }

        File.WriteAllBytes(Path.Combine(dir, SealVerifier.SealPath), AefJsonWriter.Document(seal, AefLimits.MaxSealBytes));
    }

    /// <summary>The run's run hash once written ([SEAL-4]).</summary>
    public static string RunHashOf(string dir) => SealVerifier.RunHashOf(AefRunFolder.Open(dir)).Value;

    private JsonObject Summary()
    {
        var lanes = new JsonArray();
        var names = Entries.Select(e => e.Lane).Distinct(StringComparer.Ordinal).ToList();
        foreach (var lane in names)
        {
            var entries = new JsonArray();
            foreach (var (_, metric, path, method, written) in Entries.Where(e => e.Lane == lane))
            {
                var kind = AefNode.String(AefNode.Objects(Metrics["metrics"]).First(m => AefNode.String(m["id"]) == metric)["kind"])!;
                var figures = AefSummaryCalculator.Compute(Results, names, lane, metric, kind, path, method);
                var entry = new JsonObject
                {
                    ["metric"] = metric,
                    ["path"] = path,
                    ["n"] = figures.Measured,
                    ["N"] = figures.N,
                    ["notMeasured"] = figures.NotMeasured,
                    ["value"] = figures.Measured == 0 ? null : figures.ValueDefined ? figures.Value : written,
                    ["verdict"] = figures.Measured == 0 ? "not_measured" : "scored",
                    ["sum"] = figures.Sum,
                };
                if (method is not null) entry["aggregate"] = new JsonObject { ["method"] = method };
                entries.Add(entry);
            }

            lanes.Add(new JsonObject { ["lane"] = lane, ["metrics"] = entries });
        }

        var summary = new JsonObject { ["schemaVersion"] = "1.0", ["runId"] = RunId, ["lanes"] = lanes };
        if (CostUsd is { } cost) summary["cost"] = new JsonObject { ["totalUsd"] = cost };
        return summary;
    }
}
