// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli;

// T3.3 (2026-05-25): the idempotency + locked-workspace tests below redirect
// Console.Out / Console.Error to capture output. Console redirection is
// process-global; running these tests in parallel with other Console-capturing
// tests (e.g. AzureChatAgentFactoryTests) silently swallows the other test's
// output. Pin into the "ConsoleIO" xUnit collection so the runner serialises
// every test class that touches the Console streams.
[Collection("ConsoleTests")]
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

    [Fact]
    public async Task Migrate_RootOverride_NonExistent_ReturnsExitOne()
    {
        // Defense-in-depth: --root pointing at a non-existent directory must
        // surface as exit 1 (validator rejects), not crash deep in the pipeline.
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid().ToString("N"));
        var result = await MigrateCommand.RunAsync(apply: false, root: null, rootOverride: bogus);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Migrate_Root_NonExistent_ReturnsExitOne()
    {
        // Same path-traversal defense applied to the --root positional too.
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid().ToString("N"));
        var result = await MigrateCommand.RunAsync(apply: false, root: bogus, rootOverride: null);
        Assert.Equal(1, result);
    }

    // ─── Phase 10 T3.3 follow-ups (2026-05-25) ───────────────────────────────

    [Fact]
    public async Task Migrate_IsIdempotent_AfterApply()
    {
        // T3.3 idempotency contract: a second --apply invocation against an
        // already-migrated workspace must NOT re-move, NOT crash, and exit 0.
        // The current LegacyPathScanner still sees `TestResults/traces/` (it
        // is dir-existence-based, not file-count-based), so the second run
        // re-enters HandleTraceArtifactsAsync — but the inner GetFiles loop
        // is empty (the JSON moved on the first apply) so nothing is moved
        // and stderr stays clean.
        //
        // This guards against future refactors that adopt destructive
        // "always re-walk + rewrite" semantics that would corrupt or
        // duplicate already-migrated files.
        var traceFile = SetupTraceArtifact("idem");

        // First apply: actually moves the trace.
        var first = await MigrateCommand.RunAsync(apply: true, root: null, rootOverride: _root);
        Assert.Equal(0, first);
        Assert.False(File.Exists(traceFile), "First apply should have moved the trace.");

        // Locate the moved file so we can verify it is NOT duplicated /
        // re-touched by the second invocation.
        var movedFiles = Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "subjects", "agents", "idem"),
            "idem_2026-01-01_trace.json",
            SearchOption.AllDirectories);
        var movedFile = Assert.Single(movedFiles);
        var lastWriteBefore = File.GetLastWriteTimeUtc(movedFile);
        var sizeBefore = new FileInfo(movedFile).Length;

        // Second apply: must not crash, must exit 0, must NOT touch the
        // already-migrated file (no duplication, no rewrite, no error
        // on stderr beyond best-effort sentinel sweep).
        var origErr = Console.Error;
        try
        {
            using var errSw = new StringWriter();
            Console.SetError(errSw);
            var second = await MigrateCommand.RunAsync(apply: true, root: null, rootOverride: _root);
            Assert.Equal(0, second);
            Assert.DoesNotContain("Move failed", errSw.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(origErr);
        }

        // The migrated file is unchanged (size + mtime), AND no duplicate
        // copies were created in the destination tree.
        var movedFilesAfter = Directory.GetFiles(
            Path.Combine(_root, ".agenteval", "subjects", "agents", "idem"),
            "idem_2026-01-01_trace.json",
            SearchOption.AllDirectories);
        Assert.Single(movedFilesAfter);
        Assert.Equal(sizeBefore, new FileInfo(movedFile).Length);
        Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(movedFile));
    }

    [Fact]
    public async Task Migrate_RejectsConcurrentAccess_OnLocked()
    {
        // T3.3 locked-workspace contract: when one of the to-be-moved files
        // is held open by another process (FileShare.None), the SafeMove
        // helper logs an error to stderr and continues with the rest of the
        // migration. The command must still exit 0 (migration is best-effort
        // per-file; the locked file is reported but other findings proceed),
        // and the locked file must remain on disk unchanged so the operator
        // can retry after the lock releases.
        //
        // This guards against a regression where the future implementation
        // adopts a transactional "rollback everything if any file fails"
        // semantic that would lose work on partial locks.
        var lockedTrace = SetupTraceArtifact("locked");
        var freeTrace = SetupTraceArtifact("free");

        // Open the locked trace exclusively for the duration of the test.
        using var lockHandle = new FileStream(
            lockedTrace,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var origErr = Console.Error;
        var origOut = Console.Out;
        try
        {
            using var errSw = new StringWriter();
            using var outSw = new StringWriter();
            Console.SetError(errSw);
            Console.SetOut(outSw);

            var result = await MigrateCommand.RunAsync(apply: true, root: null, rootOverride: _root);

            // Command did not crash the process.
            Assert.Equal(0, result);

            // The locked file was reported as a failure (on stderr) AND remains
            // at its original location — the migration must NOT silently drop
            // data when it can't acquire the file.
            var errText = errSw.ToString();
            Assert.True(
                errText.Contains("Move failed", StringComparison.OrdinalIgnoreCase),
                $"Expected 'Move failed' on stderr for the locked file; got:\n{errText}");
            Assert.True(File.Exists(lockedTrace),
                "Locked trace must remain at its original location when the move fails.");

            // The OTHER (unlocked) trace was migrated successfully — the
            // command does not abort the entire run on a single failure.
            Assert.False(File.Exists(freeTrace),
                "Unlocked trace should have been moved despite the sibling lock.");
        }
        finally
        {
            Console.SetError(origErr);
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task Migrate_PreservesSentinels()
    {
        // T3.3 sentinel-preservation contract: marker files like `.skip`,
        // `.invalid`, `.lock` that operators or tooling drop into the legacy
        // layout must survive the migration. The current migrate command does
        // not move them (they are not JSON traces); this test pins that
        // behavior so a future refactor that broadens the move pattern
        // (e.g. "move everything under TestResults/traces/") doesn't
        // accidentally vacuum the sentinels.
        var traceFile = SetupTraceArtifact("sentinel-host");

        var tracesDir = Path.Combine(_root, "TestResults", "traces");
        var skipFile = Path.Combine(tracesDir, ".skip");
        var invalidFile = Path.Combine(tracesDir, ".invalid");
        var lockFile = Path.Combine(tracesDir, ".lock");
        File.WriteAllText(skipFile, "");
        File.WriteAllText(invalidFile, "regression-marker");
        File.WriteAllText(lockFile, "pid=12345");

        var result = await MigrateCommand.RunAsync(apply: true, root: null, rootOverride: _root);
        Assert.Equal(0, result);

        // The trace itself was moved as expected.
        Assert.False(File.Exists(traceFile), "Trace file should have been migrated.");

        // The 3 sentinels survive at their original locations with original
        // content (no truncation, no move). This is the regression we want
        // to pin: future scanners must explicitly opt-in to touching them.
        Assert.True(File.Exists(skipFile), ".skip sentinel was unexpectedly removed.");
        Assert.True(File.Exists(invalidFile), ".invalid sentinel was unexpectedly removed.");
        Assert.True(File.Exists(lockFile), ".lock sentinel was unexpectedly removed.");
        Assert.Equal("regression-marker", File.ReadAllText(invalidFile));
        Assert.Equal("pid=12345", File.ReadAllText(lockFile));
    }
}
