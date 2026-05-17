// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

public class TraceArtifactManagerStoreTests : IDisposable
{
    private readonly string _workspaceRoot;

    public TraceArtifactManagerStoreTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"AgentEval_TraceStore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_WithFileSystemStore_UsesCanonicalTracesDir()
    {
        var store = new FileSystemOutputStore(_workspaceRoot);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var runId = "2026-01-01_00-00-00_abcd1234";

        var manager = new TraceArtifactManager(store, subject, runId);

        var expectedSegments = new[] { "subjects", "agents", "testagent", "runs", runId, "traces" };
        foreach (var segment in expectedSegments)
            Assert.Contains(segment, manager.OutputDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithFileSystemStore_CreatesTracesDirectory()
    {
        var store = new FileSystemOutputStore(_workspaceRoot);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var runId = "2026-01-01_00-00-00_abcd5678";

        var manager = new TraceArtifactManager(store, subject, runId);

        Assert.True(Directory.Exists(manager.OutputDirectory));
    }

    [Fact]
    public void Constructor_WithInMemoryStore_FallsBackToDefault()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var runId = "2026-01-01_00-00-00_abcdefab";

        var manager = new TraceArtifactManager(store, subject, runId);

        Assert.Equal(VerbosityConfiguration.TraceDirectory, manager.OutputDirectory);
    }

    [Fact]
    public void Constructor_WithNullStore_Throws()
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        Assert.Throws<ArgumentNullException>(() => new TraceArtifactManager(null!, subject, "run1"));
    }

    [Fact]
    public void Constructor_WithNullSubject_Throws()
    {
        var store = new InMemoryOutputStore();
        Assert.Throws<ArgumentNullException>(() => new TraceArtifactManager(store, null!, "run1"));
    }

    [Fact]
    public void Constructor_WithEmptyRunId_Throws()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        Assert.Throws<ArgumentException>(() => new TraceArtifactManager(store, subject, ""));
    }

    [Fact]
    public void GetTracesDirectory_ReturnsPath_UnderWorkspaceRoot()
    {
        var store = new FileSystemOutputStore(_workspaceRoot);
        var subject = new SubjectIdentity(SubjectKind.Agent, "MyAgent");
        var runId = "2026-01-01_12-00-00_deadbeef";

        var path = store.GetTracesDirectory(subject, runId);

        Assert.StartsWith(_workspaceRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(path));
    }
}
