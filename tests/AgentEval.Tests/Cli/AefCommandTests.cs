// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.Tests.Contracts;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval aef verify | seal | view | checkpoint | export | import assert-ai</c> end to end on files: one test per
/// verb (with its JSON output and exit codes), on the sample ASSERT run, a store written by the real
/// <see cref="FileSystemOutputStore"/>, and a lane vector of the AEF conformance corpus.
/// </summary>
public class AefCommandTests : IDisposable
{
    private static readonly string AssertSample = Path.Combine(AefSchemaSet.RepoRoot(), "samples", "interop", "assert-ai", "example-run", "results", "billing-safety", "run-1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aef-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Out(string name = "run") => Path.Combine(_root, name);

    [Fact]
    public void ImportAssertAi_WritesASealedImportedRun_AndSaysWhatItSupplied()
    {
        var (exit, stdout, _) = Run((o, e) => AefCommand.RunImportAssertAi(AssertSample, Out(), null, null, null, 0.05, null, "on", false, null, false, o, e));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("assert-ai 0.3 → AEF run billing-safety.run-1", stdout, StringComparison.Ordinal);
        Assert.Contains(": intact", stdout, StringComparison.Ordinal);
        Assert.Contains("subject.ref, subject.kind, deployment.ref, suite.version, suite.frozen, execution.targetMode, execution.stimulus, contentCapture, judges[].mode", stdout, StringComparison.Ordinal);

        var (jsonExit, json, _) = Run((o, e) => AefCommand.RunImportAssertAi(AssertSample, Out("again"), null, null, null, null, null, "off", true, null, true, o, e));
        var report = JsonNode.Parse(json)!;
        Assert.Equal(ExitCodes.Success, jsonExit);
        Assert.Equal(("unsealed", "billing-safety.run-1", false), ((string)report["outcome"]!, (string)report["runId"]!, (bool)report["sealed"]!));
        Assert.Equal(18, File.ReadAllLines(Path.Combine(Out("again"), "results.ndjson")).Length);

        // Usage errors: a bad capture value, a rate out of range, a folder that is not an ASSERT run, an output folder in use.
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportAssertAi(AssertSample, Out("x"), null, null, null, null, null, "maybe", false, null, false, o, e)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportAssertAi(AssertSample, Out("x"), null, null, null, 1.5, null, "on", false, null, false, o, e)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportAssertAi(_root, Out("x"), null, null, null, null, null, "on", false, null, false, o, e)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportAssertAi(AssertSample, Out(), null, null, null, null, null, "on", false, null, false, o, e)).Exit);
    }

    [Fact]
    public void Verify_SaysIntact_ThenInvalidWithItsProblems_WhenAFileChanges()
    {
        Import(Out(), seal: true);

        var (exit, stdout, _) = Run((o, e) => AefCommand.RunVerify(Out(), null, null, false, o, e));
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("intact", stdout.Split('\n')[0].TrimEnd());
        Assert.Contains("run billing-safety.run-1, run hash", stdout, StringComparison.Ordinal);

        // A sealed file edited afterwards: the seal no longer matches it.
        File.AppendAllText(Path.Combine(Out(), "ext", "assert-ai", "taxonomy.json"), " ");
        (exit, stdout, _) = Run((o, e) => AefCommand.RunVerify(Out(), null, null, false, o, e));
        Assert.Equal(ExitCodes.TestFailure, exit);
        Assert.StartsWith("invalid: 1 problem", stdout, StringComparison.Ordinal);
        Assert.Contains("ext/assert-ai/taxonomy.json  digest", stdout, StringComparison.Ordinal);

        var (jsonExit, json, _) = Run((o, e) => AefCommand.RunVerify(Out(), null, null, true, o, e));
        var report = JsonNode.Parse(json)!;
        Assert.Equal(ExitCodes.TestFailure, jsonExit);
        Assert.Equal("invalid", (string)report["outcome"]!);
        Assert.Equal(["ext/assert-ai/taxonomy.json", "digest"], report["problems"]![0]!.AsArray().Select(p => (string)p!));

        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunVerify(Path.Combine(_root, "nowhere"), null, null, false, o, e)).Exit);
    }

    [Fact]
    public void Seal_SealsAClosedRun_SignedWithAKey_AndRefusesToSealItTwice()
    {
        Import(Out(), seal: false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(_root, "key.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var policyPath = Path.Combine(_root, "policy.json");
        File.WriteAllText(policyPath, new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject { ["identity"] = "git:alice@example.com", ["publicKey"] = key.ExportSubjectPublicKeyInfoPem() }),
        }.ToJsonString());

        var (exit, json, _) = Run((o, e) => AefCommand.RunSeal(Out(), keyPath, "ingest", true, o, e));
        var report = JsonNode.Parse(json)!;
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(("intact", "ingest", true), ((string)report["outcome"]!, (string)report["sealedBy"]!, (bool)report["signed"]!));
        Assert.True(File.Exists(Path.Combine(Out(), "attestation.dsse.json")));

        var (verified, text, _) = Run((o, e) => AefCommand.RunVerify(Out(), policyPath, null, false, o, e));
        Assert.Equal(ExitCodes.Success, verified);
        Assert.Contains("signed by git:alice@example.com", text, StringComparison.Ordinal);

        var (again, _, stderr) = Run((o, e) => AefCommand.RunSeal(Out(), null, "producer", false, o, e));
        Assert.Equal(ExitCodes.TestFailure, again);
        Assert.Contains("already sealed", stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunSeal(Out(), null, "someone", false, o, e)).Exit);
    }

    [Fact]
    public void View_ShowsTheEffectiveView_OfARunWithoutOverlays()
    {
        Import(Out(), seal: true);

        var (exit, stdout, _) = Run((o, e) => AefCommand.RunView(Out(), "2026-10-09T00:00:00Z", null, false, o, e));
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Results: as sealed", stdout, StringComparison.Ordinal);

        var (jsonExit, json, _) = Run((o, e) => AefCommand.RunView(Out(), null, null, true, o, e));
        var view = JsonNode.Parse(json)!;
        Assert.Equal(ExitCodes.Success, jsonExit);
        Assert.Empty(view["results"]!.AsArray());
        Assert.Empty(view["withheld"]!.AsArray());
        Assert.Equal(0, (int)view["unsealedEvents"]!);
        Assert.Empty(view["assurance"]!.AsArray());   // [OVL-3]: per event of the view, none here
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunView(Out(), "yesterday", null, false, o, e)).Exit);
    }

    [Fact]
    public void Checkpoint_RecomputesEachLane_AsTheCorpusExpects()
    {
        var vector = Path.Combine(AefSchemaSet.RepoRoot(), "contracts", "aef", "1", "conformance", "lane-vectors", "severity");
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(vector, "expected.json")))!;

        var (exit, json, _) = Run((o, e) => AefCommand.RunCheckpoint(Path.Combine(vector, "checkpoint.json"), Path.Combine(vector, "runs"), null, null, null, true, o, e));
        var report = JsonNode.Parse(json)!;
        var problems = report["manifestProblems"]!.AsArray().Count + report["problems"]!.AsArray().Count;
        Assert.Equal(problems == 0 ? ExitCodes.Success : ExitCodes.TestFailure, exit);
        Assert.Equal(
            expected["lanes"]!.AsArray().Select(l => ((string)l!["lane"]!, (string?)l["result"]?["status"])),
            report["lanes"]!.AsArray().Select(l => ((string)l!["lane"]!, (string?)l["result"]?["status"])));

        var (textExit, text, _) = Run((o, e) => AefCommand.RunCheckpoint(Path.Combine(vector, "checkpoint.json"), Path.Combine(vector, "runs"), null, null, null, false, o, e));
        Assert.Equal(exit, textExit);
        Assert.Contains("lane low-allows-low: passed", text, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunCheckpoint(Path.Combine(vector, "expected.json"), Path.Combine(vector, "runs"), null, null, null, false, o, e)).Exit);
    }

    [Fact]
    public async Task Export_WritesAStoreV1RunAsAnIntactAefRun()
    {
        var (workspace, runDirectory, runId) = await StoreRunAsync();

        var stdout = new StringWriter();
        var exit = await AefCommand.RunExportAsync(runDirectory, Out(), null, "live", "on", false, null, true, stdout, new StringWriter());
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(("intact", runId, true), ((string)report["outcome"]!, (string)report["runId"]!, (bool)report["sealed"]!));
        Assert.StartsWith("agenteval store v1", (string)report["from"]!, StringComparison.Ordinal);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(Out(), "results.ndjson")).Length);   // the scenario and its assertion

        // The workspace with --run works too; without --run, or with a bad target mode, it is a usage error.
        Assert.Equal(ExitCodes.Success, await AefCommand.RunExportAsync(workspace, Out("again"), runId, "mocked", "off", true, null, false, TextWriter.Null, TextWriter.Null));
        Assert.Equal(ExitCodes.UsageError, await AefCommand.RunExportAsync(workspace, Out("x"), null, "mocked", "on", false, null, false, TextWriter.Null, TextWriter.Null));
        Assert.Equal(ExitCodes.UsageError, await AefCommand.RunExportAsync(runDirectory, Out("x"), null, "real", "on", false, null, false, TextWriter.Null, TextWriter.Null));
        Assert.Equal(ExitCodes.UsageError, await AefCommand.RunExportAsync(_root, Out("x"), null, "mocked", "on", false, null, false, TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void ExportOtel_WritesOneLogsDataLinePerResultLine_AndRefusesWhatItCannotExport()
    {
        // The checked example's input run (interop/examples/aef-otel-aef/input): nine result lines, nine events.
        var example = Path.Combine(AefSchemaSet.RepoRoot(), "contracts", "aef", "1", "interop", "examples", "aef-otel-aef");
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "otel.jsonl");

        var (exit, json, _) = Run((o, e) => AefCommand.RunExportOtel(Path.Combine(example, "input"), output, null, true, o, e));
        var report = JsonNode.Parse(json)!;
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "intact", 9, 9), ((string)report["runId"]!, (string)report["outcome"]!, (int)report["lines"]!, (int)report["events"]!));
        var lines = File.ReadAllLines(output);
        Assert.Equal(9, lines.Length);
        Assert.All(lines, l => Assert.Equal("gen_ai.evaluation.result", (string)JsonNode.Parse(l)!["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!["eventName"]!));

        // The text report; then the input errors (exit 2), with nothing written: the output file exists, the run is not
        // a folder, the folder is not a run that verifies, the trust policy does not read.
        var (textExit, text, _) = Run((o, e) => AefCommand.RunExportOtel(Path.Combine(example, "input"), Path.Combine(_root, "again.jsonl"), null, false, o, e));
        Assert.Equal(ExitCodes.Success, textExit);
        Assert.Contains("→", text, StringComparison.Ordinal);
        Assert.Contains("9 LogsData lines, 9 gen_ai.evaluation.result events", text, StringComparison.Ordinal);

        var (existsExit, _, existsError) = Run((o, e) => AefCommand.RunExportOtel(Path.Combine(example, "input"), output, null, false, o, e));
        Assert.Equal(ExitCodes.UsageError, existsExit);
        Assert.Contains("exists", existsError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunExportOtel(Path.Combine(_root, "nowhere"), Path.Combine(_root, "x.jsonl"), null, false, o, e)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunExportOtel(example, Path.Combine(_root, "x.jsonl"), null, true, o, e)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunExportOtel(Path.Combine(example, "input"), Path.Combine(_root, "x.jsonl"), Path.Combine(example, "expected.json"), false, o, e)).Exit);
        Assert.False(File.Exists(Path.Combine(_root, "x.jsonl")));
    }

    [Fact]
    public void ImportOtel_WritesASealedImportedRun_AndRefusesWhatThePageRefuses()
    {
        // interop/examples/otel-aef: seven evaluation events (and one log record that is none) as an AEF run.
        var example = Path.Combine(AefSchemaSet.RepoRoot(), "contracts", "aef", "1", "interop", "examples", "otel-aef");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));

        var (exit, json, error) = Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "input.jsonl"), Out(), "otel-import-0001", "hand-written OTLP/JSON (opentelemetry.md)", "agent:support/support-triage", "agent", "live", "on", false, null, true, o, e, clock));
        Assert.True(exit == ExitCodes.Success, error);
        var report = JsonNode.Parse(json)!;
        Assert.Equal(("intact", "otel-import-0001", true), ((string)report["outcome"]!, (string)report["runId"]!, (bool)report["sealed"]!));
        Assert.Equal(File.ReadAllLines(Path.Combine(example, "run", "results.ndjson")).Length, File.ReadAllLines(Path.Combine(Out(), "results.ndjson")).Length);
        Assert.Contains("startedAt", report["asserted"]!.AsArray().Select(a => (string)a!));

        // Refused, naming the rule (exit 2, nothing written); a bad target mode or subject kind is a usage error.
        var (refusedExit, _, refusedError) = Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "refusals", "pending.jsonl"), Out("x"), "r", "f", "agent:a", "agent", "live", "on", false, null, false, o, e, clock));
        Assert.Equal(ExitCodes.UsageError, refusedExit);
        Assert.Contains("OT-6", refusedError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Out("x")));
        var (offExit, _, offError) = Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "input.jsonl"), Out("x"), "r", "f", "agent:a", "agent", "live", "off", false, null, false, o, e, clock));
        Assert.Equal(ExitCodes.UsageError, offExit);   // OT-8: logs carrying explanations, asked for contentCapture off
        Assert.Contains("content-capture", offError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Out("x")));
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "input.jsonl"), Out("x"), "r", "f", "agent:a", "agent", "live", "maybe", false, null, false, o, e, clock)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "input.jsonl"), Out("x"), "r", "f", "agent:a", "agent", "real", "on", false, null, false, o, e, clock)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "input.jsonl"), Out("x"), "r", "f", "agent:a", "robot", "live", "on", false, null, false, o, e, clock)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportOtel(Path.Combine(example, "input.jsonl"), Out(), "r", "f", "agent:a", "agent", "live", "on", false, null, false, o, e, clock)).Exit);   // out-dir in use
    }

    [Fact]
    public void ExportInspect_WritesTheCheckedLog_AndRefusesWhatItCannotExport()
    {
        // interop/examples/aef-inspect: the input run (six cases, with overlay events) as one Inspect EvalLog, byte for
        // byte the example's inspect.json; without --ignore-overlays the run is refused (IN-4).
        var example = Path.Combine(AefSchemaSet.RepoRoot(), "contracts", "aef", "1", "interop", "examples", "aef-inspect");
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "inspect.json");

        var (exit, json, error) = Run((o, e) => AefCommand.RunExportInspect(Path.Combine(example, "input"), output, true, null, true, o, e));
        Assert.True(exit == ExitCodes.Success, error);
        var report = JsonNode.Parse(json)!;
        Assert.Equal(("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "intact", 6), ((string)report["runId"]!, (string)report["outcome"]!, (int)report["samples"]!));
        Assert.Equal(File.ReadAllText(Path.Combine(example, "inspect.json")).Replace("\r\n", "\n", StringComparison.Ordinal), File.ReadAllText(output));

        var (textExit, text, _) = Run((o, e) => AefCommand.RunExportInspect(Path.Combine(example, "input"), Path.Combine(_root, "again.json"), true, null, false, o, e));
        Assert.Equal(ExitCodes.Success, textExit);
        Assert.Contains("one Inspect EvalLog, 6 samples", text, StringComparison.Ordinal);

        // Refused (exit 2, nothing written): overlay events without --ignore-overlays, an output file that exists, a
        // folder that is not a run.
        var (refusedExit, _, refusedError) = Run((o, e) => AefCommand.RunExportInspect(Path.Combine(example, "input"), Path.Combine(_root, "x.json"), false, null, false, o, e));
        Assert.Equal(ExitCodes.UsageError, refusedExit);
        Assert.Contains("IN-4", refusedError, StringComparison.Ordinal);
        var (existsExit, _, existsError) = Run((o, e) => AefCommand.RunExportInspect(Path.Combine(example, "input"), output, true, null, false, o, e));
        Assert.Equal(ExitCodes.UsageError, existsExit);
        Assert.Contains("exists", existsError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunExportInspect(Path.Combine(_root, "nowhere"), Path.Combine(_root, "x.json"), true, null, false, o, e)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunExportInspect(example, Path.Combine(_root, "x.json"), true, null, true, o, e)).Exit);
        Assert.False(File.Exists(Path.Combine(_root, "x.json")));
    }

    [Fact]
    public void ImportInspect_WritesASealedImportedRun_AndRefusesWhatThePageRefuses()
    {
        // interop/examples/inspect-aef: a hand-written Inspect log (two epochs, three scorers, a sample that crashed).
        var example = Path.Combine(AefSchemaSet.RepoRoot(), "contracts", "aef", "1", "interop", "examples", "inspect-aef");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));

        var (exit, json, error) = Run((o, e) => AefCommand.RunImportInspect(Path.Combine(example, "log.json"), Out(), "live", "on", false, null, true, o, e, clock));
        Assert.True(exit == ExitCodes.Success, error);
        var report = JsonNode.Parse(json)!;
        Assert.Equal(("intact", "kE7rQx2mVt9uJ3pL8sNw4a", true, "inspect_ai 0.3.277"), ((string)report["outcome"]!, (string)report["runId"]!, (bool)report["sealed"]!, (string)report["from"]!));
        Assert.Equal(File.ReadAllLines(Path.Combine(example, "run", "results.ndjson")).Length, File.ReadAllLines(Path.Combine(Out(), "results.ndjson")).Length);
        Assert.Contains("execution.targetMode", report["asserted"]!.AsArray().Select(a => (string)a!));

        // Refused, naming the rule (exit 2, nothing written): a Score.history edit, a conversion time before the log's
        // end; a bad target mode or capture, or an output folder in use, is a usage error.
        var (refusedExit, _, refusedError) = Run((o, e) => AefCommand.RunImportInspect(Path.Combine(example, "refusals", "history-edit.json"), Out("x"), "live", "on", false, null, false, o, e, clock));
        Assert.Equal(ExitCodes.UsageError, refusedExit);
        Assert.Contains("IN-10", refusedError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Out("x")));
        var early = new FixedClock(new DateTimeOffset(2026, 10, 6, 10, 4, 29, TimeSpan.Zero));
        var (sealExit, _, sealError) = Run((o, e) => AefCommand.RunImportInspect(Path.Combine(example, "log.json"), Out("x"), "live", "on", false, null, false, o, e, early));
        Assert.Equal(ExitCodes.UsageError, sealExit);
        Assert.Contains("SEAL-1", sealError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Out("x")));
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportInspect(Path.Combine(example, "log.json"), Out("x"), "real", "on", false, null, false, o, e, clock)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportInspect(Path.Combine(example, "log.json"), Out("x"), "live", "maybe", false, null, false, o, e, clock)).Exit);
        Assert.Equal(ExitCodes.UsageError, Run((o, e) => AefCommand.RunImportInspect(Path.Combine(example, "log.json"), Out(), "live", "on", false, null, false, o, e, clock)).Exit);   // out-dir in use
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void Import(string output, bool seal) =>
        Assert.Equal(ExitCodes.Success, Run((o, e) => AefCommand.RunImportAssertAi(AssertSample, output, null, null, null, null, null, "on", !seal, null, false, o, e)).Exit);

    // A store v1 workspace with one completed run: one scenario with one assertion.
    private async Task<(string Workspace, string RunDirectory, string RunId)> StoreRunAsync()
    {
        var workspace = Path.Combine(_root, ".agenteval");
        var store = new FileSystemOutputStore(workspace);
        await store.InitializeSolutionAsync("cli-test");
        var subject = new SubjectIdentity(SubjectKind.Agent, "billing");
        var manifest = await store.StartRunAsync(subject, new RunContext("Cli.Tests", "tests", "AefCommandTests", null, null, "benchmark"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult(
            "s1", "Scenario one", "Hi", "Hello", true, 1.0, new Dictionary<string, double>(), [AssertionResult.Pass("HaveRespondedWith(Hello)")], TimeSpan.FromSeconds(1), 0));
        await store.CompleteRunAsync(manifest, new RunSummary("1.0", manifest.Run.RunId, "PASS", new RunStats(1, 1, 0, 0), new Dictionary<string, double>()));
        return (workspace, store.ResolveRunDirectory(subject, manifest.Run.RunId), manifest.Run.RunId);
    }

    private static (int Exit, string Stdout, string Stderr) Run(Func<TextWriter, TextWriter, int> verb)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = verb(stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
