// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Plan-13 T1.7 (v1.1) end-to-end tampering test matrix. Exercises
/// <see cref="ContentHasher.VerifyAsync"/> over a real <c>FileSystemOutputStore</c>-written
/// run and confirms the v1.1 canonical-JSON projection has the right semantics:
/// <list type="bullet">
///   <item><b>Tolerated</b>: whitespace-only edits, property reorder — VerifyAsync passes.</item>
///   <item><b>Rejected</b>: value edit, property add, property remove — VerifyAsync fails.</item>
/// </list>
/// </summary>
public class ContentHasherCanonicalTamperingTests : IDisposable
{
    private readonly string _workspace;

    public ContentHasherCanonicalTamperingTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ch-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task VerifyAsync_BaselineRun_Passes()
    {
        var (layout, subject, runId, storedHash) = await SeedRunAsync();
        var matches = await ContentHasher.VerifyAsync(layout, subject, runId, storedHash, default);
        Assert.True(matches, "baseline (un-tampered) run must verify");
    }

    [Fact]
    public async Task VerifyAsync_WhitespaceOnlyEditToScenario_StillVerifies()
    {
        var (layout, subject, runId, storedHash) = await SeedRunAsync();

        // Re-format the first scenario file with extra whitespace.
        var scenarioFile = ScenarioFiles(layout, subject, runId).First();
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(scenarioFile))!;
        var reformatted = JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(scenarioFile, reformatted);

        var matches = await ContentHasher.VerifyAsync(layout, subject, runId, storedHash, default);
        Assert.True(matches, "whitespace-only re-format must NOT break the audit hash");
    }

    [Fact]
    public async Task VerifyAsync_ValueEditInScenario_BreaksHash()
    {
        var (layout, subject, runId, storedHash) = await SeedRunAsync();

        var scenarioFile = ScenarioFiles(layout, subject, runId).First();
        var json = await File.ReadAllTextAsync(scenarioFile);
        // Pre-condition: the scenario was written with passed=true.
        Assert.Contains("\"passed\":true", json.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        var tampered = json.Replace("\"passed\":true", "\"passed\":false")
                           .Replace("\"passed\": true", "\"passed\": false");
        await File.WriteAllTextAsync(scenarioFile, tampered);

        var matches = await ContentHasher.VerifyAsync(layout, subject, runId, storedHash, default);
        Assert.False(matches, "value edit (passed=true → false) must break the audit hash");
    }

    [Fact]
    public async Task VerifyAsync_PropertyAddInSummary_BreaksHash()
    {
        var (layout, subject, runId, storedHash) = await SeedRunAsync();

        var summaryFile = layout.SummaryFile(subject, runId);
        var json = await File.ReadAllTextAsync(summaryFile);
        var tampered = json.TrimEnd().TrimEnd('}') + ", \"injectedProperty\": 999 }";
        await File.WriteAllTextAsync(summaryFile, tampered);

        var matches = await ContentHasher.VerifyAsync(layout, subject, runId, storedHash, default);
        Assert.False(matches, "property add must break the audit hash");
    }

    [Fact]
    public async Task VerifyAsync_PropertyRemoveFromSummary_BreaksHash()
    {
        var (layout, subject, runId, storedHash) = await SeedRunAsync();

        var summaryFile = layout.SummaryFile(subject, runId);
        var node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(summaryFile))!;
        Assert.True(node.ContainsKey("metrics"), "test fixture must include metrics — adjust the seed if this fails");
        node.Remove("metrics");
        await File.WriteAllTextAsync(summaryFile, node.ToJsonString());

        // Whether the schema-validation step rejects it or the hash mismatch does — either way
        // this run no longer verifies. Use TryCatch since some shape changes might raise via
        // a different path inside HashRunAsync's manifest deserialisation; both are acceptable
        // negatives for the test contract ("removed property is detected").
        bool matches;
        try
        {
            matches = await ContentHasher.VerifyAsync(layout, subject, runId, storedHash, default);
        }
        catch
        {
            matches = false;
        }
        Assert.False(matches, "property remove must break the audit hash");
    }

    [Fact]
    public async Task VerifyAsync_PropertyReorderInSummary_StillVerifies()
    {
        var (layout, subject, runId, storedHash) = await SeedRunAsync();

        var summaryFile = layout.SummaryFile(subject, runId);
        var orig = System.Text.Json.Nodes.JsonObject.Parse(await File.ReadAllTextAsync(summaryFile))!.AsObject();
        // Rebuild with keys in reverse order to force a non-trivial reorder on disk.
        var reordered = new System.Text.Json.Nodes.JsonObject();
        foreach (var kvp in orig.OrderByDescending(k => k.Key, StringComparer.Ordinal))
            reordered[kvp.Key] = kvp.Value?.DeepClone();
        await File.WriteAllTextAsync(summaryFile, reordered.ToJsonString());

        var matches = await ContentHasher.VerifyAsync(layout, subject, runId, storedHash, default);
        Assert.True(matches, "property reorder must NOT break the audit hash under canonical projection");
    }

    // ── Seed helper ───────────────────────────────────────────────────────────

    private async Task<(FileSystemLayout Layout, SubjectIdentity Subject, string RunId, string StoredHash)> SeedRunAsync()
    {
        var agentEvalDir = Path.Combine(_workspace, ".agenteval");
        Directory.CreateDirectory(agentEvalDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentEvalDir, "solution.json"),
            """{"schemaVersion":"1.0","id":"00000000-0000-0000-0000-000000000099","name":"ch-tamper"}""");

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TamperTestAgent");
        await store.EnsureSubjectAsync(subject);

        var manifest = await store.StartRunAsync(subject, new RunContext("TP", "/p", "xunit", null, null, "eval"));
        var scenario = new ScenarioResult(
            Id: "sc-0001",
            Name: "Test scenario",
            Input: "What is 2+2?",
            Output: "4",
            Passed: true,
            Score: 1.0,
            Metrics: new Dictionary<string, double> { ["score"] = 1.0 },
            Assertions: Array.Empty<AssertionResult>(),
            Duration: TimeSpan.FromMilliseconds(50),
            EstimatedCost: 0.0);
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);

        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        var sealedManifest = (await store.GetRunManifestAsync(manifest.Run.RunId))!;
        var layout = new FileSystemLayout(agentEvalDir);
        return (layout, subject, manifest.Run.RunId, sealedManifest.ContentHash);
    }

    private static IEnumerable<string> ScenarioFiles(FileSystemLayout layout, SubjectIdentity subject, string runId)
        => Directory.EnumerateFiles(layout.ScenariosDir(subject, runId), "*.json");
}
