// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Pins plan-13 T2.6 (v1.1), revised by GAP-16 — <see cref="FileSystemOutputStore.WriteScenarioResultAsync"/>
/// rejects <see cref="ScenarioResult.Output"/> that is clearly intended to be structured (starts
/// with <c>{</c>/<c>[</c>) but is not well-formed JSON, while remaining permissive for free-form
/// text Output (the common code-eval / agent-raw-response case) and for schema-loose-but-well-formed
/// JSON (the doctor read-path carries strict schema validation).
/// </summary>
[Collection("ConsoleTests")]
public class FileSystemOutputStoreScenarioOutputSchemaTests : IDisposable
{
    private readonly string _workspace;

    public FileSystemOutputStoreScenarioOutputSchemaTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "fsos-t26-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteScenarioResultAsync_RawTextOutput_Persists()
    {
        // Arrange — free-form text Output is the legacy / code-eval path; must not
        // be rejected by T2.6.
        var (store, subject) = await SeedStoreAsync();
        var manifest = await store.StartRunAsync(subject, new RunContext("TP", "/p", "xunit", null, null, "eval"));
        var scenario = new ScenarioResult(
            Id: "sc-text",
            Name: "Free-form output",
            Input: "What's 2+2?",
            Output: "The answer is 4.", // raw text, not JSON
            Passed: true,
            Score: 1.0,
            Metrics: new Dictionary<string, double> { ["score"] = 1.0 },
            Assertions: Array.Empty<AssertionResult>(),
            Duration: TimeSpan.FromMilliseconds(50),
            EstimatedCost: 0.0);

        // Act — must not throw
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);
    }

    [Fact]
    public async Task WriteScenarioResultAsync_MalformedJsonShapedOutput_Throws()
    {
        // GAP-16 (revises the original T2.6 warn-only stance): Output that is clearly INTENDED to be
        // structured (starts with '{' or '[') but is not even well-formed JSON is genuine corruption
        // — a truncated write or injected garbage — not the schema-looseness T2.6 tolerates. Such a
        // write now fails fast rather than silently persisting a broken artifact. (Schema-loose-but-
        // well-formed JSON still persists; the doctor read-path carries strict schema validation.)
        var (store, subject) = await SeedStoreAsync();
        var manifest = await store.StartRunAsync(subject, new RunContext("TP", "/p", "xunit", null, null, "eval"));
        var scenario = new ScenarioResult(
            Id: "sc-malformed",
            Name: "Malformed JSON",
            Input: "x",
            Output: "{ \"this is\": not-valid-json }",
            Passed: true,
            Score: 1.0,
            Metrics: new Dictionary<string, double>(),
            Assertions: Array.Empty<AssertionResult>(),
            Duration: TimeSpan.FromMilliseconds(10),
            EstimatedCost: 0.0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.WriteScenarioResultAsync(manifest.Run.RunId, scenario));
        Assert.Contains("sc-malformed", ex.Message);
        Assert.Contains("JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteScenarioResultAsync_EmptyOutput_Persists()
    {
        // Arrange — empty Output is valid (scoring still happens via the wrapper fields).
        var (store, subject) = await SeedStoreAsync();
        var manifest = await store.StartRunAsync(subject, new RunContext("TP", "/p", "xunit", null, null, "eval"));
        var scenario = new ScenarioResult(
            Id: "sc-empty",
            Name: "Empty output",
            Input: "y",
            Output: "",
            Passed: true,
            Score: 1.0,
            Metrics: new Dictionary<string, double>(),
            Assertions: Array.Empty<AssertionResult>(),
            Duration: TimeSpan.FromMilliseconds(5),
            EstimatedCost: 0.0);

        // Act — must not throw
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);
    }

    private async Task<(FileSystemOutputStore Store, SubjectIdentity Subject)> SeedStoreAsync()
    {
        var agentEvalDir = Path.Combine(_workspace, ".agenteval");
        Directory.CreateDirectory(agentEvalDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentEvalDir, "solution.json"),
            """{"schemaVersion":"1.0","id":"00000000-0000-0000-0000-000000000099","name":"t26-test"}""");
        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "T26Agent");
        await store.EnsureSubjectAsync(subject);
        return (store, subject);
    }
}
