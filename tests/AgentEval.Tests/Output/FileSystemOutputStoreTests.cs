// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class FileSystemOutputStoreTests : IDisposable
{
    private readonly string _root;

    public FileSystemOutputStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-fsos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteSolutionJson(string? name = "TestSolution")
    {
        var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name };
        File.WriteAllText(
            Path.Combine(_root, "solution.json"),
            JsonSerializer.Serialize(solution, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static RunContext DefaultContext() =>
        new("MyProject", "/path/to/project", "xunit", null, null, "eval");

    private static SubjectIdentity DefaultSubject() =>
        new(SubjectKind.Agent, "TestAgent");

    [Fact]
    public async Task EnsureSolution_Throws_WhenSolutionFileMissing()
    {
        var store = new FileSystemOutputStore(_root);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureSolutionAsync());
    }

    [Fact]
    public async Task EnsureSubject_CreatesSubjectFile()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var info = await store.EnsureSubjectAsync(subject);

        Assert.Equal(subject.Name, info.Identity.Name);
        Assert.Equal(subject.Kind, info.Identity.Kind);

        var subjectFile = Path.Combine(_root, "subjects", "agents", "TestAgent", "subject.json");
        Assert.True(File.Exists(subjectFile));
    }

    [Fact]
    public async Task StartRun_CreatesRunDirAndManifest()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());

        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("TestAgent", manifest.Subject.Name);
        Assert.Equal("PENDING", manifest.Run.Verdict);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_[a-f0-9]{8}$", manifest.Run.RunId);

        var runDir = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", manifest.Run.RunId);
        Assert.True(Directory.Exists(runDir));

        var manifestFile = Path.Combine(runDir, "manifest.json");
        Assert.True(File.Exists(manifestFile));
    }

    [Fact]
    public async Task CompleteRun_WritesSummaryAndUpdatesManifestWithHash()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(5, 4, 1, 0),
            new Dictionary<string, double> { ["accuracy"] = 0.8 });

        await store.CompleteRunAsync(manifest, summary, CancellationToken.None);

        var runDir = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", manifest.Run.RunId);
        var summaryFile = Path.Combine(runDir, "summary.json");
        Assert.True(File.Exists(summaryFile));

        var updatedManifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "manifest.json"));
        Assert.Contains("sha256:", updatedManifestJson);
        Assert.Matches(@"sha256:[a-f0-9]{64}", updatedManifestJson);
        Assert.DoesNotContain("sha256:00000000000000000000000000000000000000000000000000000000000000", updatedManifestJson);
    }

    [Fact]
    public async Task SubjectName_WithPathTraversalChars_StaysContainedUnderRoot()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var evil = new SubjectIdentity(SubjectKind.Agent, "../../etc/passwd");

        await store.EnsureSubjectAsync(evil);

        var written = Directory.EnumerateFiles(_root, "subject.json", SearchOption.AllDirectories).ToList();
        Assert.Single(written);

        var rootFull = Path.GetFullPath(_root);
        var writtenFull = Path.GetFullPath(written[0]);
        Assert.StartsWith(rootFull, writtenFull, StringComparison.OrdinalIgnoreCase);

        var rel = Path.GetRelativePath(_root, writtenFull);
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"subjects{sep}agents", Path.GetDirectoryName(Path.GetDirectoryName(rel)));
    }

    [Fact]
    public async Task ListSubjects_FiltersByKind()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "Alpha"));
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "Beta"));
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Workflow, "Gamma"));

        var agents = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(SubjectKind.Agent))
            agents.Add(s);

        var workflows = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(SubjectKind.Workflow))
            workflows.Add(s);

        Assert.Equal(2, agents.Count);
        Assert.Single(workflows);
        Assert.Contains(agents, a => a.Identity.Name == "Alpha");
        Assert.Contains(agents, a => a.Identity.Name == "Beta");
        Assert.Contains(workflows, w => w.Identity.Name == "Gamma");
    }

    [Fact]
    public async Task RedTeamCampaign_StartAndComplete_PersistsManifestAndFindings()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var ctx = new RedTeamCampaignContext("My Campaign",
            new[] { new SubjectIdentity(SubjectKind.Agent, "TargetAgent") }, "lite");

        var manifest = await store.StartRedTeamCampaignAsync(ctx);
        Assert.Equal("My Campaign", manifest.Name);
        Assert.Matches(@"^My Campaign_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", manifest.CampaignId);

        await store.CompleteRedTeamCampaignAsync(manifest.CampaignId,
            new RedTeamFindings(new List<object> { new { id = "f1", severity = "low" } }));

        var redTeamRoot = Path.Combine(_root, "red-team");
        var findingsFiles = Directory.EnumerateFiles(redTeamRoot, "findings.json", SearchOption.AllDirectories).ToList();
        Assert.Single(findingsFiles);
    }

    [Fact]
    public async Task StartRun_InitialManifest_ValidatesAgainstSchema()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var manifest = await store.StartRunAsync(subject, DefaultContext());

        var manifestPath = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", manifest.Run.RunId, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestPath);

        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".manifest.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var schema = Json.Schema.JsonSchema.FromText(reader.ReadToEnd());

        var result = schema.Evaluate(System.Text.Json.Nodes.JsonNode.Parse(json),
            new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        Assert.True(result.IsValid,
            string.Join("\n", result.Details.Where(d => !d.IsValid)
                .Select(d => $"{d.EvaluationPath}: {string.Join(", ", d.Errors?.Select(e => $"{e.Key}={e.Value}") ?? Array.Empty<string>())}")));
    }

    [Fact]
    public async Task RoundTrip_StartWriteScenarioComplete_PersistsAll()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;

        var scenario = new ScenarioResult(
            "scenario-01", "Test Scenario", "input", "output", true, 1.0,
            new Dictionary<string, double> { ["score"] = 1.0 },
            new List<AssertionResult> { new("assert1", true, null) },
            TimeSpan.FromSeconds(1), 0.001);

        await store.WriteScenarioResultAsync(runId, scenario);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        var retrievedManifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(retrievedManifest);
        Assert.Equal("PASS", retrievedManifest!.Run.Verdict);
        Assert.Equal(1, retrievedManifest.Run.ScenarioCount);

        var retrievedSummary = await store.GetRunSummaryAsync(runId);
        Assert.NotNull(retrievedSummary);
        Assert.Equal("PASS", retrievedSummary!.Verdict);

        var scenarios = new List<ScenarioResult>();
        await foreach (var s in store.GetScenarioResultsAsync(runId))
            scenarios.Add(s);

        Assert.Single(scenarios);
        Assert.Equal("scenario-01", scenarios[0].Id);
    }

    [Fact]
    public async Task GetRunManifestAsync_TraversalRunId_ReturnsNull_DoesNotResolveRealRun()
    {
        // SEC-06: a runId like "foo/../{realRunId}" would, without the safe-segment guard,
        // Path.Combine + normalize back into the real run dir and resolve it. The store must
        // reject non-single-segment runIds before combining (safe-by-construction).
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        await store.CompleteRunAsync(manifest,
            new RunSummary("1.0", runId, "PASS", new RunStats(1, 1, 0, 0), new Dictionary<string, double>()));

        // Sanity: the legitimate single-segment id resolves.
        Assert.NotNull(await store.GetRunManifestAsync(runId));

        // Traversal forms that normalize back to the same dir (or escape) must be rejected.
        Assert.Null(await store.GetRunManifestAsync($"foo/../{runId}"));
        Assert.Null(await store.GetRunManifestAsync(".."));
        Assert.Null(await store.GetRunManifestAsync($"..\\{runId}"));
    }

    // ── Case-insensitive collision (batch-5 surface) ─────────────────────────

    [Fact]
    public async Task EnsureSubject_DifferentCaseSameName_ThrowsCollisionError()
    {
        // Windows / default macOS filesystems are case-insensitive, so
        // "TravelAgent" and "travelagent" resolve to the same directory.
        // The store must surface this explicitly instead of silently
        // overwriting the first subject's `subject.json` with the second
        // identity.
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);

        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "TravelAgent"));

        // On case-insensitive FS (Windows/macOS default) this re-resolves to
        // the same directory but finds a subject.json whose stored name has
        // a different case → throws. On case-sensitive FS (typical Linux)
        // this resolves to a separate directory and succeeds — both behaviours
        // are correct; we only assert when running on the case-insensitive
        // platforms where the collision would otherwise be silent.
        var isCaseInsensitiveFs =
            OperatingSystem.IsWindows() ||
            OperatingSystem.IsMacOS();
        if (!isCaseInsensitiveFs) return;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "travelagent")));
    }

    [Fact]
    public async Task EnsureSubject_SameNameExactCase_DoesNotThrow()
    {
        // The collision guard must not false-positive when the same identity
        // is ensured twice (the common idempotency case).
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);

        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "TravelAgent"));
        // Second call with the SAME case is idempotent.
        var info = await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "TravelAgent"));

        Assert.Equal("TravelAgent", info.Identity.Name);
    }

    // ── Phase 3 / Task 3.5 — EnsureSubjectAsync hardening regressions ────

    /// <summary>
    /// Corrupt subject.json should produce a context-rich exception instead
    /// of an opaque JsonException bubbling up. Pass-3 hardening: catch
    /// JsonException in EnsureSubjectAsync, rethrow as InvalidOperationException
    /// with manual-inspect guidance.
    /// </summary>
    [Fact]
    public async Task EnsureSubject_CorruptJson_ThrowsWithContext()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "BrokenSubject"));

        // Corrupt the subject.json
        var subjectPath = Path.Combine(_root, "subjects", "agents", "BrokenSubject", "subject.json");
        await File.WriteAllTextAsync(subjectPath, "{ this is not valid JSON !!!");

        // Re-ensure should throw with manual-inspect guidance, not a raw JsonException.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "BrokenSubject")));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manually inspect or delete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Partial-init: subject directory exists (from a killed mid-write process)
    /// but no subject.json. A second EnsureSubjectAsync with a case-only-
    /// different name should detect the collision against the bare directory.
    /// </summary>
    [Fact]
    public async Task EnsureSubject_PartialDirCollision_OnCaseInsensitiveFs_ThrowsCollisionError()
    {
        // Same OS skip as the sibling collision test — only meaningful on
        // case-insensitive filesystems.
        var isCaseInsensitiveFs = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        if (!isCaseInsensitiveFs) return;

        WriteSolutionJson();
        // Pre-create a stale subject directory without subject.json
        var staleDir = Path.Combine(_root, "subjects", "agents", "PartialAgent");
        Directory.CreateDirectory(staleDir);
        // Subject.json deliberately missing

        var store = new FileSystemOutputStore(_root);
        // Different-case name maps to the same directory on case-insensitive FS.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "partialagent")));
        Assert.Contains("Partial subject directory", ex.Message);
    }

    /// <summary>
    /// Concurrent same-name EnsureSubjectAsync should serialise via the lock
    /// sentinel — both calls complete with the same identity, neither stomps
    /// the other's subject.json.
    /// </summary>
    [Fact]
    public async Task EnsureSubject_ConcurrentSameNameRace_AllSucceedWithSameIdentity()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);

        // 10 parallel calls with the same identity.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "ConcurrentAgent")))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("ConcurrentAgent", r.Identity.Name));
        // subject.json should be valid JSON (no torn-write corruption).
        var subjectPath = Path.Combine(_root, "subjects", "agents", "ConcurrentAgent", "subject.json");
        Assert.True(File.Exists(subjectPath));
        var json = await File.ReadAllTextAsync(subjectPath);
        var doc = System.Text.Json.JsonDocument.Parse(json);  // throws on corruption
        Assert.Equal("ConcurrentAgent", doc.RootElement.GetProperty("name").GetString());
    }

    // ── Phase 3 / Task 3.3 — cross-process JSONL appender regression ─────

    /// <summary>
    /// 50 parallel in-process appends to the same recent-runs JSONL file must
    /// produce exactly 50 well-formed lines (no interleaving). Cross-process
    /// safety adds a named Mutex on top — that path is harder to test
    /// hermetically; the in-process case is the most common contention.
    /// </summary>
    [Fact]
    public async Task AppendJsonl_50ParallelAppenders_SameProcess_AllLinesIntact()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "AppendTestAgent");
        await store.EnsureSubjectAsync(subject);

        // Drive 50 history-append calls in parallel (history.jsonl flows through
        // the same AppendJsonlLineAsync gate as recent.jsonl).
        var tasks = Enumerable.Range(0, 50)
            .Select(i => store.AppendHistoryEntryAsync(subject, new HistoryEntry(
                RunId: $"run-{i:D3}",
                Timestamp: DateTimeOffset.UtcNow,
                Verdict: "PASS",
                Score: 1.0,
                Notes: null)))
            .ToArray();
        await Task.WhenAll(tasks);

        // Read the file and count well-formed JSON lines.
        var historyPath = Path.Combine(_root, "subjects", "agents", "AppendTestAgent", "history.jsonl");
        Assert.True(File.Exists(historyPath));
        var lines = await File.ReadAllLinesAsync(historyPath);
        Assert.Equal(50, lines.Length);
        // Each line is parseable JSON
        foreach (var line in lines)
        {
            var doc = System.Text.Json.JsonDocument.Parse(line);  // throws on interleaved bytes
            Assert.True(doc.RootElement.TryGetProperty("runId", out _));
        }
    }

    // ── Phase 3 / Task 3.7 — schema-validate writes a .invalid.json sidecar ─

    /// <summary>
    /// On schema-validation failure the producer must dump the offending JSON
    /// to a sibling `.invalid.json` so the failure can be debugged without
    /// re-running the workload. The 24h ctor sweep cleans up stale sidecars.
    /// </summary>
    [Fact]
    public async Task WriteJson_SchemaValidationFails_InvalidJsonSidecarIsWritten()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "ValidationTestAgent"));

        // Force a schema-validate failure by passing an invalid RunContext kind.
        // The schema's run.kind enum is {eval,benchmark,memory-benchmark,stochastic-eval,compliance};
        // a value like "invalid-kind-name" will be rejected by manifest.schema.json.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.StartRunAsync(
                new SubjectIdentity(SubjectKind.Agent, "ValidationTestAgent"),
                new RunContext("TestProject", "/path", "xunit", null, null, "INVALID_KIND_VALUE")));
        Assert.Contains("Schema validation failed", ex.Message);
        Assert.Contains(".invalid.json", ex.Message);

        // The sidecar must exist on disk.
        var runsDir = Path.Combine(_root, "subjects", "agents", "ValidationTestAgent", "runs");
        Assert.True(Directory.Exists(runsDir));
        var sidecars = Directory.GetFiles(runsDir, "manifest.json.invalid.json", SearchOption.AllDirectories);
        Assert.NotEmpty(sidecars);
        var sidecarJson = await File.ReadAllTextAsync(sidecars[0]);
        var doc = System.Text.Json.JsonDocument.Parse(sidecarJson);
        Assert.Equal("INVALID_KIND_VALUE", doc.RootElement.GetProperty("run").GetProperty("kind").GetString());
    }
}
