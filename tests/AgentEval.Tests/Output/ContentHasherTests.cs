// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class ContentHasherTests : IDisposable
{
    private readonly string _root;

    public ContentHasherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-hasher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteSolutionJson()
    {
        var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "TestSolution" };
        File.WriteAllText(
            Path.Combine(_root, "solution.json"),
            System.Text.Json.JsonSerializer.Serialize(solution,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    private static RunContext DefaultContext() =>
        new("MyProject", "/path/to/project", "xunit", null, null, "eval");

    private static SubjectIdentity DefaultSubject() =>
        new(SubjectKind.Agent, "HashTestAgent");

    [Fact]
    public async Task Hash_SameFiles_ProducesSameHash()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        var updated = await store.GetRunManifestAsync(runId);
        Assert.NotNull(updated);

        var hash1 = updated!.ContentHash;
        var hash2 = updated.ContentHash;

        Assert.Equal(hash1, hash2);
        Assert.Matches(@"^sha256:[a-f0-9]{64}$", hash1);
    }

    [Fact]
    public async Task Hash_ValueEdit_ProducesDifferentHash()
    {
        // T1.7 (v1.1): canonical-JSON projection tolerates whitespace-only edits but
        // catches VALUE edits. Pre-T1.7 this test appended a single space to the scenario
        // file and expected the hash to change. Under canonical projection that's
        // correctly tolerated; we now edit the `passed` boolean (a real value change)
        // and expect the hash to change.
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;

        var scenario = new ScenarioResult(
            "s1", "Scenario", "input", "output", true, 1.0,
            new Dictionary<string, double>(),
            new List<AssertionResult>(),
            TimeSpan.FromSeconds(1), 0.0);
        await store.WriteScenarioResultAsync(runId, scenario);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        var updatedManifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(updatedManifest);
        var hashBefore = updatedManifest!.ContentHash;

        var scenariosDir = Path.Combine(_root, "subjects", "agents", "HashTestAgent", "runs", runId, "scenarios");
        var scenarioFile = Directory.GetFiles(scenariosDir, "*.json").First();
        var json = await File.ReadAllTextAsync(scenarioFile);
        var tampered = json.Replace("\"passed\":true", "\"passed\":false")
                           .Replace("\"passed\": true", "\"passed\": false");
        Assert.NotEqual(json, tampered); // sanity — the replace actually landed
        await File.WriteAllTextAsync(scenarioFile, tampered);

        var layout = new FileSystemLayoutAccessor(_root);
        var hashAfter = await layout.ComputeHashAsync(subject, runId);

        Assert.NotEqual(hashBefore.Replace("sha256:", ""), hashAfter);
    }

    [Fact]
    public async Task Hash_WhitespaceOnlyEdit_ProducesSameHash()
    {
        // T1.7 (v1.1) companion test: whitespace-only edits MUST NOT break the hash.
        // This is the property that frees operators from re-running benchmarks because
        // a JSON formatter touched the on-disk file.
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;

        var scenario = new ScenarioResult(
            "s1", "Scenario", "input", "output", true, 1.0,
            new Dictionary<string, double>(),
            new List<AssertionResult>(),
            TimeSpan.FromSeconds(1), 0.0);
        await store.WriteScenarioResultAsync(runId, scenario);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        var updatedManifest = await store.GetRunManifestAsync(runId);
        var hashBefore = updatedManifest!.ContentHash;

        var scenariosDir = Path.Combine(_root, "subjects", "agents", "HashTestAgent", "runs", runId, "scenarios");
        var scenarioFile = Directory.GetFiles(scenariosDir, "*.json").First();
        await File.AppendAllTextAsync(scenarioFile, "\n   \n"); // trailing whitespace

        var layout = new FileSystemLayoutAccessor(_root);
        var hashAfter = await layout.ComputeHashAsync(subject, runId);

        Assert.Equal(hashBefore.Replace("sha256:", ""), hashAfter);
    }

    [Fact]
    public async Task Verify_MatchingHash_ReturnsTrue()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double>());
        await store.CompleteRunAsync(manifest, summary);

        var updated = await store.GetRunManifestAsync(runId);
        Assert.NotNull(updated);
        var expectedHash = updated!.ContentHash;

        var result = await new FileSystemLayoutAccessor(_root).VerifyAsync(subject, runId, expectedHash);
        Assert.True(result);
    }

    [Fact]
    public async Task Verify_DifferentHash_ReturnsFalse()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double>());
        await store.CompleteRunAsync(manifest, summary);

        var wrongHash = "sha256:" + new string('0', 64);
        var result = await new FileSystemLayoutAccessor(_root).VerifyAsync(subject, runId, wrongHash);
        Assert.False(result);
    }

    // ── Phase 3 / Task 3.1 — trace-files-in-hash-domain regression ───────

    /// <summary>
    /// Pre-pass-3, ContentHasher only hashed agent-trace.json. Per-test trace
    /// files written by TraceArtifactManager (and any other consumer) sat
    /// outside the audit chain. Verify that ANY *.json file under traces/
    /// affects the hash now.
    /// </summary>
    [Fact]
    public async Task HashRunAsync_TimestampedTraceFile_IsCovered()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double>());
        await store.CompleteRunAsync(manifest, summary);

        var tracesDir = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", runId, "traces");
        Directory.CreateDirectory(tracesDir);

        // Hash with no extra trace files
        var accessor = new FileSystemLayoutAccessor(_root);
        var hashBefore = await accessor.ComputeHashAsync(subject, runId);

        // Add a per-test timestamped trace file
        var extraTracePath = Path.Combine(tracesDir, "test01_20260101_120000_trace.json");
        await File.WriteAllTextAsync(extraTracePath, "{\"events\":[]}");
        var hashAfter = await accessor.ComputeHashAsync(subject, runId);

        Assert.NotEqual(hashBefore, hashAfter);
    }

    // ── Phase 3 / Task 3.2 — manifest tamper-detection regression ────────

    /// <summary>
    /// Pre-pass-3, mutating manifest.run.verdict post-write while leaving
    /// contentHash intact silently passed audit. With Task 3.2 (canonical
    /// manifest in hash domain), VerifyAsync should now flag the tamper.
    /// </summary>
    [Fact]
    public async Task HashRunAsync_TamperedManifestVerdict_DetectsCorruption()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double>());
        await store.CompleteRunAsync(manifest, summary);

        // The stored manifest now has the real contentHash. Verify it once
        // to baseline.
        var manifestPath = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", runId, "manifest.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var storedManifest = System.Text.Json.JsonSerializer.Deserialize<RunManifest>(
            manifestJson,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
            })!;
        var accessor = new FileSystemLayoutAccessor(_root);
        Assert.True(await accessor.VerifyAsync(subject, runId, storedManifest.ContentHash),
            "Baseline: the run should verify cleanly before tampering.");

        // Tamper: rewrite manifest.json with run.verdict = "PASS" → "FAIL",
        // keeping the original contentHash field intact (the attack surface).
        var tampered = manifestJson.Replace("\"verdict\": \"PASS\"", "\"verdict\": \"FAIL\"");
        Assert.NotEqual(manifestJson, tampered);  // sanity — replacement actually changed something
        await File.WriteAllTextAsync(manifestPath, tampered);

        // VerifyAsync must now report the tamper.
        Assert.False(await accessor.VerifyAsync(subject, runId, storedManifest.ContentHash),
            "Manifest verdict tamper must be detected by the canonical-manifest hash domain.");
    }

    /// <summary>
    /// Thin accessor to call internal ContentHasher methods via the InternalsVisibleTo grant.
    /// </summary>
    private sealed class FileSystemLayoutAccessor
    {
        private readonly FileSystemLayout _layout;

        public FileSystemLayoutAccessor(string root) =>
            _layout = new FileSystemLayout(root);

        public Task<string> ComputeHashAsync(SubjectIdentity subject, string runId) =>
            ContentHasher.HashRunAsync(_layout, subject, runId, CancellationToken.None);

        public Task<bool> VerifyAsync(SubjectIdentity subject, string runId, string expectedHash) =>
            ContentHasher.VerifyAsync(_layout, subject, runId, expectedHash, CancellationToken.None);
    }
}
