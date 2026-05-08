// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;

namespace AgentEval.Tests.Cli;

public class MigrateCommandTests : IDisposable
{
    private readonly string _root;

    public MigrateCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-migrate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SetupTraceArtifact(string agentName = "foo")
    {
        // Create TestResults/traces/foo_2026-01-01_trace.json
        var tracesDir = Path.Combine(_root, "TestResults", "traces");
        Directory.CreateDirectory(tracesDir);
        var traceFile = Path.Combine(tracesDir, $"{agentName}_2026-01-01_trace.json");
        File.WriteAllText(traceFile, """{"runId":"test","events":[]}""");

        // Create a matching subject under .agenteval/
        var subjectDir = Path.Combine(_root, ".agenteval", "subjects", "agents", agentName);
        Directory.CreateDirectory(subjectDir);

        return traceFile;
    }

    [Fact]
    public async Task Migrate_DryRun_DoesNotMoveFiles()
    {
        var traceFile = SetupTraceArtifact("foo");

        // Run dry-run (apply = false)
        var result = await MigrateCommand.RunAsync(apply: false, root: null, rootOverride: _root);

        Assert.Equal(0, result);
        // File must still exist at original location
        Assert.True(File.Exists(traceFile), $"Trace file was unexpectedly moved: {traceFile}");
    }

    [Fact]
    public async Task Migrate_Apply_MovesFile()
    {
        var traceFile = SetupTraceArtifact("bar");

        // Run with --apply
        var result = await MigrateCommand.RunAsync(apply: true, root: null, rootOverride: _root);

        Assert.Equal(0, result);

        // File must no longer exist at original location
        Assert.False(File.Exists(traceFile), $"Trace file was not moved from: {traceFile}");

        // File should have been moved into .agenteval/subjects/agents/bar/runs/.../traces/
        var subjectDir = Path.Combine(_root, ".agenteval", "subjects", "agents", "bar");
        var runsDir = Path.Combine(subjectDir, "runs");
        Assert.True(Directory.Exists(runsDir), "runs/ directory was not created under subject.");

        // Find the moved file anywhere under runsDir
        var movedFiles = Directory.GetFiles(runsDir, "bar_2026-01-01_trace.json", SearchOption.AllDirectories);
        Assert.Single(movedFiles);
    }
}
