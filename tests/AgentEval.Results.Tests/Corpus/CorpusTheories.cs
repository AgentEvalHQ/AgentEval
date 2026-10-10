using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Conformance;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runner;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>
/// The conformance corpus, run in-process through the aef-dotnet driver (<see cref="Program.Dispatch"/>), one theory
/// per kind of vector this component covers: fast feedback while the code changes. A conformance claim rests on the
/// Python runner driving the published driver (run-corpus.sh), not on these.
/// </summary>
public class CorpusTheories
{
    private static readonly JsonArray Index =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "index.json")))!["vectors"]!.AsArray();

    public static TheoryData<string> Vectors(string kind) =>
        new(Index.Where(v => (string?)v!["kind"] == kind).Select(v => (string)v!["id"]!));

    public static TheoryData<int> Items(string file) =>
        new(Enumerable.Range(0, JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, file)))!.AsArray().Count));

    [Theory]
    [MemberData(nameof(Vectors), "decision")]
    public void Decision(string id)
    {
        var file = Folder(id)!;
        var vector = JsonNode.Parse(File.ReadAllBytes(file))!;
        var result = Run("decide", file);

        if (vector["expectedError"] is { } refusal)
        {
            Assert.True(result["error"] is not null, $"decided what it must refuse ({refusal}): {result.ToJsonString()}");
        }
        else
        {
            Assert.True(JsonNode.DeepEquals(vector["expected"], result["output"]),
                $"expected {vector["expected"]!.ToJsonString()}, got {result.ToJsonString()}");
        }

        if (vector["schemaInvalid"] is not null || vector["readerOnly"] is not null)
        {
            var input = Path.Combine(Path.GetTempPath(), $"aef-decision-input-{Guid.NewGuid():N}.json");
            File.WriteAllBytes(input, Encoding.UTF8.GetBytes(vector["input"]!.ToJsonString()));
            try
            {
                var verdicts = Run("document", "decision#/$defs/input", input);
                if (vector["readerOnly"] is not null)
                {
                    Assert.Equal("invalid", (string?)verdicts["writer"]);
                    Assert.Equal("valid", (string?)verdicts["reader"]);
                }
                else
                {
                    Assert.Equal("invalid", (string?)verdicts["reader"]);
                }
            }
            finally
            {
                File.Delete(input);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "document")]
    [MemberData(nameof(Vectors), "reader-only")]
    [MemberData(nameof(Vectors), "plan")]
    public void Document(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        var result = Run("document", (string)expected["schema"]!, Path.Combine(folder, (string?)expected["document"] ?? "document.json"));

        Assert.Equal((string?)expected["writer"], (string?)result["writer"]);
        Assert.Equal((string?)expected["reader"], (string?)result["reader"]);
        foreach (var (field, value) in expected["reads"]?.AsObject() ?? new JsonObject())
        {
            Assert.True(result["reads"]!.AsObject().TryGetPropertyValue(field, out var read), $"{field} was not read");
            Assert.True(JsonNode.DeepEquals(value, read), $"{field}: expected {value?.ToJsonString()}, got {read?.ToJsonString()}");
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "matching")]
    public void Matching(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var result = Run("match", Path.Combine(folder, "plan.json"), Path.Combine(folder, "runner.json"));

        Assert.Equal((bool)Expected(folder)["matches"]!, (bool)result["matches"]!);
    }

    [Theory]
    [MemberData(nameof(Vectors), "stream")]
    public void Stream(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        var result = Run("stream", Path.Combine(folder, "events.ndjson"), Path.Combine(folder, (string)expected["plan"]!));

        Assert.Equal(
            expected["problems"]!.AsArray().Select(p => $"{(string)p![0]!} {(string)p[1]!}"),
            result["problems"]!.AsArray().Select(p => $"{(string)p![0]!} {(string)p[1]!}"));
    }

    [Theory]
    [MemberData(nameof(Items), "result-ids.json")]
    public void ResultId(int item)
    {
        var v = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "result-ids.json")))![item]!;
        string[] args = ["result-id", (string)v["runId"]!, (string)v["caseId"]!, (string)v["path"]!];
        if (v["trial"] is { } trial)
        {
            args = [.. args, trial.ToJsonString()];   // as written: 3.0 is trial 3
        }

        Assert.Equal((string)v["resultId"]!, (string)Run(args)["resultId"]!);
    }

    [Theory]
    [MemberData(nameof(Items), "paths.json")]
    public void Paths(int item)
    {
        var v = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "paths.json")))![item]!;
        var list = Path.Combine(Path.GetTempPath(), $"aef-paths-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(list, Encoding.UTF8.GetBytes(v["paths"]!.ToJsonString()));
        try
        {
            var result = Run("paths", list);

            Assert.True(JsonNode.DeepEquals(v["problems"], result["problems"]),
                $"{v["name"]}: expected {v["problems"]!.ToJsonString()}, got {result["problems"]!.ToJsonString()}");
        }
        finally
        {
            File.Delete(list);
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "run")]
    [MemberData(nameof(Vectors), "encoding")]
    public void RunVerification(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        string[] args = ["run", Path.Combine(folder, (string?)expected["run"] ?? "run")];
        if ((string?)expected["policy"] is { } policy)
        {
            args = [.. args, "--policy", Path.Combine(folder, policy)];
        }

        if ((string?)expected["anchors"] is { } anchors)
        {
            args = [.. args, "--anchors", Path.Combine(folder, anchors)];
        }

        var result = Run(args);

        Assert.Equal((string?)expected["outcome"], (string?)result["outcome"]);
        AssertProblems(expected["problems"], result["problems"]);
        foreach (var field in new[] { "signedBy", "anchored", "withheld" })
        {
            if (expected.AsObject().ContainsKey(field))
            {
                Assert.True(JsonNode.DeepEquals(expected[field], result[field]), $"{field}: expected {expected[field]?.ToJsonString()}, got {result[field]?.ToJsonString()}");
            }
            else if (field == "withheld")
            {
                Assert.Null(result[field]);   // printed only when not 0
            }
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "seal")]
    public void Seal(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        string[] args = ["seal", Path.Combine(folder, (string?)expected["run"] ?? "run")];
        if ((string?)expected["policy"] is { } policy)
        {
            args = [.. args, "--policy", Path.Combine(folder, policy)];
        }

        var result = Run(args);

        AssertProblems(expected["problems"], result["problems"]);
        if ((string?)expected["manifest"] is { } manifest)
        {
            Assert.Equal(File.ReadAllText(Path.Combine(folder, manifest), new UTF8Encoding(false)), (string?)result["manifest"]);
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "chain")]
    public void Chain(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        var result = Run("chain", Path.Combine(folder, (string?)expected["run"] ?? "run"));

        AssertProblems(expected["problems"], result["problems"]);
    }

    [Theory]
    [MemberData(nameof(Vectors), "overlay-view")]
    public void OverlayView(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        string[] args = ["view", Path.Combine(folder, (string?)expected["run"] ?? "run"), "--at", (string)expected["at"]!];
        if ((string?)expected["policy"] is { } policy)
        {
            args = [.. args, "--policy", Path.Combine(folder, policy)];
        }

        var result = Run(args);

        foreach (var field in new[] { "results", "reviews", "waivers", "withheld", "unsealedEvents" })
        {
            Assert.True(JsonNode.DeepEquals(expected["view"]![field], result[field]),
                $"view.{field}: expected {expected["view"]![field]?.ToJsonString()}, got {result[field]?.ToJsonString()}");
        }

        // [OVL-3]: the assurance shown, in the vectors that state it.
        if (expected["view"]!.AsObject().ContainsKey("assurance"))
        {
            Assert.True(JsonNode.DeepEquals(expected["view"]!["assurance"], result["assurance"]),
                $"view.assurance: expected {expected["view"]!["assurance"]?.ToJsonString()}, got {result["assurance"]?.ToJsonString()}");
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "checkpoint")]
    public void Checkpoint(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        var result = Run("checkpoint", Path.Combine(folder, "document.json"));

        Assert.Equal((string?)expected["writer"], (string?)result["writer"]);
        Assert.Equal((string?)expected["reader"], (string?)result["reader"]);
        if (expected.AsObject().ContainsKey("problems"))
        {
            Assert.True(JsonNode.DeepEquals(expected["problems"], result["problems"]),
                $"problems: expected {expected["problems"]?.ToJsonString()}, got {result["problems"]?.ToJsonString()}");
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "lane")]
    public void Lane(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        string[] args = ["lanes", Path.Combine(folder, (string?)expected["checkpoint"] ?? "checkpoint.json"), "--runs", Path.Combine(folder, (string?)expected["runs"] ?? "runs")];
        if ((string?)expected["at"] is { } at)
        {
            args = [.. args, "--at", at];   // the evaluation time of a checkpoint with no recorded input ([LANE-9])
        }

        if ((string?)expected["policy"] is { } policy)
        {
            args = [.. args, "--policy", Path.Combine(folder, policy)];
        }

        if ((string?)expected["envelope"] is { } envelope)
        {
            args = [.. args, "--envelope", Path.Combine(folder, envelope)];   // the checkpoint's signature ([CKP-9])
        }

        var result = Run(args);

        Assert.True(JsonNode.DeepEquals(expected["lanes"], result["lanes"]),
            $"lanes: expected {expected["lanes"]!.ToJsonString()}, got {result["lanes"]!.ToJsonString()}");
        AssertProblems(expected["problems"], result["problems"]);
        if (expected.AsObject().ContainsKey("anchors"))
        {
            // [CKP-9], [SIG-8]: the run hashes the checkpoint anchors, in byte order, in the vectors that state them.
            Assert.True(JsonNode.DeepEquals(expected["anchors"], result["anchors"]),
                $"anchors: expected {expected["anchors"]!.ToJsonString()}, got {result["anchors"]?.ToJsonString()}");
        }
    }

    [Theory]
    [MemberData(nameof(Vectors), "plan-conformance")]
    public void PlanConformance(string id)
    {
        if (Folder(id) is not { } folder)
        {
            return;   // a generated vector this platform cannot build (spec 09 §9.2.1: skipped)
        }

        var expected = Expected(folder);
        string[] args = ["conform", Path.Combine(folder, (string)expected["events"]!), Path.Combine(folder, (string)expected["plan"]!), Path.Combine(folder, (string)expected["runs"]!)];
        if ((string?)expected["policy"] is { } policy)
        {
            args = [.. args, "--policy", Path.Combine(folder, policy)];
        }

        AssertProblems(expected["problems"], Run(args)["problems"]);
    }

    [Theory]
    [MemberData(nameof(Vectors), "job")]
    public void Job(string id)
    {
        // Spec 09 §9.3's job judge, in part (the Python runner's is the one that counts): the variables the vector names
        // are set (to a fresh value, or to the empty string) or removed for the call, and what the job wrote is compared
        // with the expected job and checked as a Stream verifier and a Run verifier check it.
        var folder = Folder(id);
        var expected = Expected(folder);
        var output = Path.Combine(Path.GetTempPath(), $"aef-job-{Guid.NewGuid():N}");
        var variables = expected["env"]?.AsObject().Select(v => (Name: v.Key, Value: (string?)v.Value switch
        {
            "set" => $"aef-secret-{Guid.NewGuid():N}",
            "empty" => "",
            _ => null,
        })).ToList() ?? [];
        var saved = variables.Select(v => (v.Name, Value: Environment.GetEnvironmentVariable(v.Name))).ToList();
        try
        {
            variables.ForEach(v => Environment.SetEnvironmentVariable(v.Name, v.Value));
            var plan = Path.Combine(folder, (string)expected["plan"]!);
            var result = Run("job", plan, Path.Combine(folder, (string)expected["runner"]!), Path.Combine(folder, (string)expected["target"]!), output, "--at", (string)expected["at"]!);

            var events = File.ReadAllLines(Path.Combine(output, "events.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal((int)result["events"]!, events.Count);
            Assert.Equal((string?)expected["at"], (string?)events[0]["at"]);
            Assert.Equal((string?)expected["endsAt"], (string?)events[^1]["at"]);
            Assert.Equal(((string?)expected["terminal"], (string?)expected["limit"]), ((string?)events[^1]["kind"], (string?)events[^1]["limit"]));

            var runs = events.Where(e => (string?)e["kind"] == "evidence.produced").Select(e => (string)e["runId"]!).ToList();
            Assert.Equal(expected["runs"]!.AsArray().Count, runs.Count);
            var planJson = JsonNode.Parse(File.ReadAllBytes(plan))!;
            var target = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, (string)expected["target"]!)))!;
            JsonNode? Fixture(string suiteRef, string version, string caseId) => target["suites"]!.AsArray()
                .Single(s => (string?)s!["ref"] == suiteRef && (string?)s["version"] == version)!["cases"]!.AsArray()
                .Single(c => (string?)c!["caseId"] == caseId);
            var clock = AefTime.Parse((string)expected["at"]!);
            foreach (var (want, runId) in expected["runs"]!.AsArray().Zip(runs))
            {
                var runFolder = Path.Combine(output, "runs", runId);
                var run = JsonNode.Parse(File.ReadAllBytes(Path.Combine(runFolder, "run.json")))!;
                var (suiteRef, version) = ((string)want!["suite"]!["ref"]!, (string)want["suite"]!["version"]!);
                Assert.Equal((suiteRef, version, (string?)want["status"]),
                    ((string?)run["suite"]!["ref"], (string?)run["suite"]!["version"], (string?)run["status"]));

                Assert.Null(run["judges"]);   // a scripted target grades with no model (§9.2.1, round 8)

                // The plan's digest or none ([PLAN-8]); endedAt the end of the run's last case on §9.2.1's clock.
                var planned = planJson["suites"]?.AsArray().FirstOrDefault(s => (string?)s!["ref"] == suiteRef && (string?)s["version"] == version);
                Assert.Equal((string?)planned?["digest"], (string?)run["suite"]!["digest"]);
                clock = clock.AddSeconds(want["cases"]!.AsArray().Sum(c => (long)Fixture(suiteRef, version, (string)c![0]!)!["seconds"]!));
                Assert.Equal(clock.ToString(), (string?)run["endedAt"]);
                clock = clock.AddSeconds((long)target["closeSeconds"]!);

                // One line per case run: its caseId, state, the fixture's severity, and the plan's lane for its suite.
                var lane = (string?)planned?["lane"];
                Assert.Equal(want["cases"]!.AsArray().Select(c => $"{c![0]} {c[1]} {(string?)Fixture(suiteRef, version, (string)c[0]!)!["severity"]} {lane}"),
                    File.ReadAllLines(Path.Combine(runFolder, "results.ndjson")).Select(l => JsonNode.Parse(l)!)
                        .Select(l => $"{l["caseId"]} {l["state"]} {(string?)l["severity"]} {(string?)l["lane"]}"));

                // pass-rate declared; the suite's lane, pass-rate at check, or no lane (§9.2.1).
                var metrics = JsonNode.Parse(File.ReadAllBytes(Path.Combine(runFolder, "metrics.json")))!["metrics"]!.AsArray();
                Assert.Contains(metrics, m => (string?)m!["id"] == "pass-rate" && (string?)m["kind"] == "rate" && (string?)m["direction"] == "higher_better");
                var lanes = JsonNode.Parse(File.ReadAllBytes(Path.Combine(runFolder, "summary.json")))!["lanes"]!.AsArray();
                Assert.Equal(lane is null ? Array.Empty<string>() : [$"{lane} pass-rate check"],
                    lanes.SelectMany(l => l!["metrics"]!.AsArray().Select(m => $"{l["lane"]} {m!["metric"]} {m["path"]}")));

                var verification = AefRunVerifier.Verify(runFolder);
                Assert.Equal((AefOutcome.Intact, 0), (verification.Outcome, verification.Problems.Count));
            }

            Assert.Equal((double)expected["spentUsd"]!, (double?)events.LastOrDefault(e => (string?)e["kind"] == "spend.updated")?["spentUsd"] ?? 0);
            var estimate = events.SingleOrDefault(e => (string?)e["kind"] == "plan.estimated");
            Assert.Equal(expected["estimated"] is null, estimate is null);
            if (estimate is not null)
            {
                Assert.Equal(((double)expected["estimated"]!["cases"]!, (double)expected["estimated"]!["usdLow"]!, (double)expected["estimated"]!["usdHigh"]!),
                    ((double)estimate["cases"]!, (double)estimate["usdLow"]!, (double)estimate["usdHigh"]!));
            }

            // [STRM-3] and [STRM-4], for a plan the reader accepts; no credential's value or path in any byte written.
            var planBytes = File.ReadAllBytes(plan);
            var planDocument = JsonNode.Parse(planBytes)!;
            if (AefSchemas.Reader.IsValid("run-plan", planDocument))
            {
                var stream = RunnerEventStream.Read(File.ReadAllBytes(Path.Combine(output, "events.ndjson")));
                Assert.Empty(RunnerEventStream.Verify(stream, planDocument, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(planBytes)).ToLowerInvariant()));
                Assert.Empty(RunnerEventStream.Conform(stream, planDocument, AefRunStore.Open(output)));
            }

            var secrets = variables.Select(v => v.Value).Where(v => !string.IsNullOrEmpty(v)).OfType<string>()
                .Concat(planDocument["credentialRefs"]?.AsArray().Select(c => (string?)c!["path"]).OfType<string>() ?? []);
            foreach (var file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(file);
                Assert.All(secrets, s => Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(s)) < 0, $"{file} holds a credential's value or path"));
            }
        }
        finally
        {
            saved.ForEach(v => Environment.SetEnvironmentVariable(v.Name, v.Value));
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public void EveryKindThisComponentCovers_HasVectors()
    {
        foreach (var kind in new[] { "decision", "document", "reader-only", "plan", "matching", "stream", "result-id", "paths", "run", "encoding", "seal", "chain", "overlay-view", "checkpoint", "lane", "plan-conformance", "produce", "job" })
        {
            Assert.Contains(Index, v => (string?)v!["kind"] == kind);
        }
    }

    // [CONF-2]: problem lists compare as ordered lists of [path, code] pairs.
    private static void AssertProblems(JsonNode? expected, JsonNode? actual) =>
        Assert.Equal(
            expected!.AsArray().Select(p => $"{(string)p![0]!} {(string)p[1]!}"),
            actual!.AsArray().Select(p => $"{(string)p![0]!} {(string)p[1]!}"));

    // A vector's folder (or file, for a decision vector), from index.json; for a generated vector (spec 09 §9.2), the
    // folder its recipe gives, made once per test run.
    private static string Folder(string id)
    {
        var folder = Path.Combine(AefCorpus.Conformance, (string)Index.Single(v => (string?)v!["id"] == id)!["path"]!);
        return Directory.Exists(folder) && Expected(folder)["generate"] is JsonArray steps
            ? GeneratedVectors.Of(id, folder, steps)
            : folder;
    }

    private static JsonNode Expected(string folder) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;

    private static JsonNode Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Dispatch(args, stdout, stderr);

        Assert.True(code == 0, $"exit {code}: {stderr}");
        Assert.EndsWith("\n", stdout.ToString(), StringComparison.Ordinal);
        return JsonNode.Parse(stdout.ToString())!;
    }
}
