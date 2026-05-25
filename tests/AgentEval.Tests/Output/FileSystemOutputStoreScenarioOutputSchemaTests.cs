// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Pins plan-13 T2.6 (v1.1) — <see cref="FileSystemOutputStore.WriteScenarioResultAsync"/>
/// rejects JSON-shaped <see cref="ScenarioResult.Output"/> that doesn't validate against
/// <c>eval-result.schema.json</c>, while remaining permissive for free-form text Output
/// (the common case for code-eval / agent-raw-response scenarios).
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
    public async Task WriteScenarioResultAsync_MalformedJsonShapedOutput_LogsWarningAndPersists()
    {
        // T2.6 pragmatic stance (post-Phase-3 review): the EvalResult JSON shape that the
        // production writer emits is intentionally looser than eval-result.schema.json, so
        // T2.6 is a WARN-only path at write time. Malformed JSON-shaped Output writes a
        // warning to stderr but persists — the run still completes; the doctor read-path
        // (T1.6) carries the strict gate for files that must validate.
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

        // Act — must not throw; should log a warning
        var stderr = new StringWriter();
        var prev = Console.Error;
        Console.SetError(stderr);
        try
        {
            await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);
        }
        finally
        {
            Console.SetError(prev);
        }

        // Assert — warning surfaced with scenario id + JSON-shape note
        var stderrText = stderr.ToString();
        Assert.Contains("sc-malformed", stderrText);
        Assert.Contains("JSON", stderrText, StringComparison.OrdinalIgnoreCase);
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
