// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Output;

namespace AgentEval.Tests.Cli;

// Joins the `ConsoleTests` collection because several tests in this class
// mutate the global `Console.Out` to capture diagnostic output (see lines
// ~114). Without the collection annotation xUnit may parallelise this
// class with other Console-mutating fixtures (e.g.
// Core/AgentEvalLoggerTests), corrupting captured streams.
[Collection("ConsoleTests")]
public class DoctorCommandTests : IDisposable
{
    private readonly string _root;

    public DoctorCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-doctor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task Doctor_CleanStore_ReturnsZero()
    {
        // Init a clean store
        await InitCommand.RunAsync(name: "DoctorTestSolution", rootOverride: _root);

        var result = await DoctorCommand.RunAsync(rootOverride: _root);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Doctor_MissingSolutionJson_ReturnsError()
    {
        // Create .agenteval/ without solution.json
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);

        var result = await DoctorCommand.RunAsync(rootOverride: _root);

        Assert.NotEqual(0, result);
    }

    [Fact]
    public async Task Doctor_TamperedScenarioFile_ReportsHashMismatch()
    {
        // Set up a full run using FileSystemOutputStore
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        WriteSolutionJson(agentEvalDir);

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "DoctorTestAgent");
        var context = new RunContext("TestProject", "/path", "xunit", null, null, "eval");

        var manifest = await store.StartRunAsync(subject, context);
        var runId = manifest.Run.RunId;

        // Write a scenario
        var scenario = new ScenarioResult(
            "sc1", "Scenario 1", "input", "output", true, 1.0,
            new Dictionary<string, double> { ["score"] = 1.0 },
            new List<AssertionResult> { new("score >= 0.5", true, null) },
            TimeSpan.FromMilliseconds(100), 0.0);
        await store.WriteScenarioResultAsync(runId, scenario);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        // Tamper: append garbage to the scenario file to invalidate the hash
        var scenariosDir = Path.Combine(agentEvalDir, "subjects", "agents", "DoctorTestAgent", "runs", runId, "scenarios");
        var scenarioFile = Directory.GetFiles(scenariosDir, "*.json").First();
        await File.AppendAllTextAsync(scenarioFile, " /* tampered */");

        // Doctor should report a hash mismatch (exit code 2)
        var result = await DoctorCommand.RunAsync(rootOverride: _root);

        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Doctor_OrphanRunWithoutManifest_ReportsWarning()
    {
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        WriteSolutionJson(agentEvalDir);

        // Create a subject + a run dir WITHOUT manifest.json (orphan)
        var subjectDir = Path.Combine(agentEvalDir, "subjects", "agents", "TestAgent");
        Directory.CreateDirectory(subjectDir);
        var subjectFile = Path.Combine(subjectDir, "subject.json");
        var subjectDoc = new { schemaVersion = "1.0", kind = "agent", name = "TestAgent" };
        await File.WriteAllTextAsync(subjectFile, JsonSerializer.Serialize(subjectDoc, s_json));

        var orphanRunDir = Path.Combine(subjectDir, "runs", "2026-01-01_12-00-00_orphan01");
        Directory.CreateDirectory(orphanRunDir);

        // Capture stdout to confirm the warning surfaces; also confirm exit code is 0
        // (orphan is a warning, not an error).
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        int result;
        try
        {
            result = await DoctorCommand.RunAsync(rootOverride: _root);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, result);
        Assert.Contains("Orphan run", captured.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_SubjectNameDoesNotMatchFolder_ReportsError()
    {
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        WriteSolutionJson(agentEvalDir);

        // Create a subject whose subject.json#name disagrees with its folder
        var folderName = "Renamed";
        var subjectDir = Path.Combine(agentEvalDir, "subjects", "agents", folderName);
        Directory.CreateDirectory(subjectDir);
        var subjectFile = Path.Combine(subjectDir, "subject.json");
        var subjectDoc = new { schemaVersion = "1.0", kind = "agent", name = "OriginalName" };
        await File.WriteAllTextAsync(subjectFile, JsonSerializer.Serialize(subjectDoc, s_json));

        var result = await DoctorCommand.RunAsync(rootOverride: _root);

        // Expect non-zero exit because of the name/folder mismatch
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Doctor_CompliantEvidence_PassesAuditChain()
    {
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        WriteSolutionJson(agentEvalDir);

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComplianceAgent");
        var context = new RunContext("TestProject", "/path", "xunit", null, null, "eval");

        var manifest = await store.StartRunAsync(subject, context);
        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);
        var updated = (await store.GetRunManifestAsync(manifest.Run.RunId))!;

        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "SOC2",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(updated.Run.RunId, updated.ContentHash),
            Controls: new[] { new EvidenceControl("CC6.1", "Logical Access", "PASS", 1.0, new[] { "sc1" }, null) },
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.8.1", null, "AgentEval", "internal"));
        await store.SaveComplianceEvidenceAsync("SOC2", subject, evidence);

        var result = await DoctorCommand.RunAsync(rootOverride: _root);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Doctor_TamperedRunAfterEvidence_ReportsAuditChainBreak()
    {
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        WriteSolutionJson(agentEvalDir);

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComplianceAgent");
        var context = new RunContext("TestProject", "/path", "xunit", null, null, "eval");

        var manifest = await store.StartRunAsync(subject, context);
        var scenario = new ScenarioResult("sc1", "Scenario", "in", "out", true, 1.0,
            new Dictionary<string, double>(), new List<AssertionResult>(),
            TimeSpan.FromMilliseconds(10), 0.0);
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);
        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);
        var updated = (await store.GetRunManifestAsync(manifest.Run.RunId))!;

        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "SOC2",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(updated.Run.RunId, updated.ContentHash),
            Controls: new[] { new EvidenceControl("CC6.1", "Logical Access", "PASS", 1.0, new[] { "sc1" }, null) },
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.8.1", null, "AgentEval", "internal"));
        await store.SaveComplianceEvidenceAsync("SOC2", subject, evidence);

        // Tamper the manifest's stored ContentHash so it disagrees with the evidence
        var manifestFile = Path.Combine(agentEvalDir, "subjects", "agents", "ComplianceAgent", "runs", manifest.Run.RunId, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestFile);
        var tampered = json.Replace(updated.ContentHash, "sha256:" + new string('f', 64));
        await File.WriteAllTextAsync(manifestFile, tampered);

        var result = await DoctorCommand.RunAsync(rootOverride: _root);

        Assert.Equal(2, result);
    }

    private static void WriteSolutionJson(string dir)
    {
        Directory.CreateDirectory(dir);
        var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "DoctorTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solution, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    [Fact]
    public async Task Doctor_RootOverride_NonExistent_ReturnsExitOne()
    {
        // Defense-in-depth: --root pointing at a non-existent directory must
        // surface as exit 1 from the validator rather than ".agenteval/ not found".
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid().ToString("N"));
        var result = await DoctorCommand.RunAsync(rootOverride: bogus);
        Assert.Equal(1, result);
    }
}
