// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;

namespace AgentEval.Results.Tests.Ingestion;

public sealed class AefIngestionWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-ingest-" + Guid.NewGuid().ToString("N"));
    private AefIngestionWorkspace Open(int capacity = 64) => new(new() { Root = _root, MaxPendingStages = capacity });

    [Fact]
    public void BeginStage_NewRoot_SeparatesStagingFromAcceptedEvidence()
    {
        using var workspace = Open();
        var first = workspace.BeginStage();
        var second = workspace.BeginStage();
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(Guid.TryParseExact(first.Id, "N", out _));
        Assert.True(Directory.Exists(first.Directory));
        Assert.Equal(Path.Combine(_root, ".staging", first.Id), first.Directory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.RunsDirectory));
    }

    [Fact]
    public void Dispose_InterruptedStage_RetainsBytesAndCapacityOnReopen()
    {
        string stage;
        using (var workspace = Open(1))
        {
            stage = workspace.BeginStage().Directory;
            File.WriteAllText(Path.Combine(stage, "partial"), "retained");
        }
        using var reopened = Open(1);
        Assert.Equal("retained", File.ReadAllText(Path.Combine(stage, "partial")));
        Assert.Throws<IOException>(() => reopened.BeginStage());
        Assert.Empty(Directory.EnumerateFileSystemEntries(reopened.RunsDirectory));
    }

    [Fact]
    public void Constructor_SecondOwner_RefusesUntilFirstDisposes()
    {
        var first = Open();
        try { Assert.Throws<IOException>(() => Open()); }
        finally { first.Dispose(); }
        using var second = Open();
        Assert.NotNull(second.BeginStage());
    }

    [Fact]
    public void BeginStage_ConcurrentReservations_NeverExceedsCapacity()
    {
        using var workspace = Open(3);
        var accepted = 0;
        Parallel.For(0, 24, _ =>
        {
            try { workspace.BeginStage(); Interlocked.Increment(ref accepted); }
            catch (IOException) { }
        });
        Assert.Equal(3, accepted);
        Assert.Equal(3, Directory.EnumerateDirectories(Path.Combine(_root, ".staging")).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void Constructor_InvalidCapacity_DoesNotCreateRoot(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(capacity));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-root")]
    public void Constructor_NonAbsoluteRoot_Refuses(string root)
    {
        Assert.Throws<ArgumentException>(() => new AefIngestionWorkspace(new() { Root = root }));
    }

    [Fact]
    public void Constructor_ExistingEvidence_RefusesWithoutOverwritingIt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "run.json"), "source evidence");
        Assert.Throws<IOException>(() => Open());
        Assert.Equal("source evidence", File.ReadAllText(Path.Combine(_root, "run.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "runs")));
    }

    [Fact]
    public void Constructor_UnknownVersion_RefusesAndReleasesLease()
    {
        using (Open()) { }
        var marker = Path.Combine(_root, ".ingestion-version");
        File.WriteAllText(marker, "2\n");
        Assert.Throws<IOException>(() => Open());
        File.WriteAllText(marker, "1\n");
        using var workspace = Open();
        Assert.NotNull(workspace.BeginStage());
    }

    [Fact]
    public void BeginStage_CancelledOrDisposed_DoesNotReserve()
    {
        var workspace = Open();
        Assert.Throws<OperationCanceledException>(() => workspace.BeginStage(new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".staging")));
        workspace.Dispose();
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.BeginStage());
    }

    [Fact]
    public void Constructor_LinkedAncestor_RefusesWithoutWritingThroughLink()
    {
        // Linux CI exercises links without requiring Windows developer-mode privileges.
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var actual = Directory.CreateDirectory(Path.Combine(_root, "actual")).FullName;
        var link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, actual);
        Assert.Throws<IOException>(() => new AefIngestionWorkspace(new() { Root = Path.Combine(link, "workspace") }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(actual));
    }

    [Fact]
    public void BeginStage_ReplacedStagingWithLink_Refuses()
    {
        if (OperatingSystem.IsWindows()) return;
        using var workspace = Open();
        var actual = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var staging = Path.Combine(_root, ".staging");
        Directory.Delete(staging);
        Directory.CreateSymbolicLink(staging, actual);
        Assert.Throws<IOException>(() => workspace.BeginStage());
        Assert.Empty(Directory.EnumerateFileSystemEntries(actual));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
