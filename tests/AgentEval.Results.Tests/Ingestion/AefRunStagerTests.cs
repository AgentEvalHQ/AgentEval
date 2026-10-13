// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentEval.Results.Ingestion;

namespace AgentEval.Results.Tests.Ingestion;

public sealed class AefRunStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-stager-" + Guid.NewGuid().ToString("N"));
    private readonly AefIngestionWorkspace _workspace;
    public AefRunStagerTests() => _workspace = new(new() { Root = _root });

    private static async IAsyncEnumerable<AefUploadFile> Files(params (string Path, string Text)[] files)
    {
        await Task.CompletedTask;
        foreach (var file in files)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file.Text));
            yield return new(file.Path, stream);
        }
    }

    private string State()
    {
        var stage = Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, ".staging")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "state.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.RunsDirectory));
        return json.RootElement.GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task StageAsync_CompleteUpload_PreservesBytesAndKeepsControlOutsideEvidence()
    {
        var stager = new AefRunStager(_workspace);
        var result = await stager.StageAsync(Files(("run.json", "raw\n"), ("ext/vendor/info.json", "{}")));
        Assert.Equal(2, result.FileCount);
        Assert.Equal(6, result.Bytes);
        Assert.Equal("raw\n", File.ReadAllText(Path.Combine(result.Directory, "run.json")));
        Assert.False(File.Exists(Path.Combine(result.Directory, "state.json")));
        Assert.Equal("staged", State());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    [InlineData("C:/file")]
    [InlineData("a/../file")]
    [InlineData("con.txt")]
    public async Task StageAsync_InvalidPath_RetainsFailedStageWithoutWritingFile(string path)
    {
        var error = await Assert.ThrowsAsync<AefStagingException>(() => new AefRunStager(_workspace).StageAsync(Files((path, "secret"))));
        Assert.Equal("path", error.Code);
        Assert.Equal("failed", State());
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("run.json", "run.json")]
    [InlineData("Ext/a", "ext/b")]
    [InlineData("file", "file/child")]
    [InlineData("file/child", "file")]
    public async Task StageAsync_ConflictingPaths_RefusesOverwrite(string first, string second)
    {
        var error = await Assert.ThrowsAsync<AefStagingException>(() => new AefRunStager(_workspace).StageAsync(Files((first, "original"), (second, "replacement"))));
        Assert.Equal("conflict", error.Code);
        var stage = Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, ".staging")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(stage, "content", first)));
        Assert.Equal("failed", State());
    }

    [Fact]
    public async Task StageAsync_ExactLimits_Completes()
    {
        var result = await new AefRunStager(_workspace, new() { MaxFiles = 1, MaxBytes = 3, MaxFileBytes = 3 })
            .StageAsync(Files(("file", "abc")));
        Assert.Equal(3, result.Bytes);
        Assert.Equal("staged", State());
    }

    [Theory]
    [InlineData(1, 100, 100)]
    [InlineData(2, 3, 3)]
    [InlineData(2, 100, 1)]
    public async Task StageAsync_ExceededLimits_RetainsOnlyBoundedBytes(int maxFiles, long total, long perFile)
    {
        var error = await Assert.ThrowsAsync<AefStagingException>(() => new AefRunStager(_workspace,
            new() { MaxFiles = maxFiles, MaxBytes = total, MaxFileBytes = perFile }).StageAsync(Files(("a", "ab"), ("b", "cd"))));
        Assert.Equal("limit", error.Code);
        Assert.Equal("failed", State());
        var stage = Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, ".staging")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(stage, "content"), "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) <= total);
    }

    [Fact]
    public async Task StageAsync_EmptyUpload_Refuses()
    {
        var error = await Assert.ThrowsAsync<AefStagingException>(() => new AefRunStager(_workspace).StageAsync(Files()));
        Assert.Equal("empty", error.Code);
        Assert.Equal("failed", State());
    }

    [Fact]
    public async Task StageAsync_CancelledEnumeration_RetainsInterruptedStageAndReleasesCapacity()
    {
        using var cancellation = new CancellationTokenSource();
        var stager = new AefRunStager(_workspace, new() { MaxConcurrentUploads = 1 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stager.StageAsync(Interrupted(cancellation), cancellation.Token));
        Assert.Equal("interrupted", State());
        var result = await stager.StageAsync(Files(("run.json", "next")));
        Assert.Equal(1, result.FileCount);
    }

    private static async IAsyncEnumerable<AefUploadFile> Interrupted(CancellationTokenSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream("first"u8.ToArray());
        yield return new("run.json", stream);
        source.Cancel();
        await Task.Delay(1, cancellationToken);
    }

    [Fact]
    public async Task StageAsync_ConcurrentCall_RefusesWithoutReservingAnotherStage()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stager = new AefRunStager(_workspace, new() { MaxConcurrentUploads = 1 });
        var first = stager.StageAsync(Paused());
        await entered.Task;
        try
        {
            var error = await Assert.ThrowsAsync<AefStagingException>(() => stager.StageAsync(Files(("run.json", "other"))));
            Assert.Equal("busy", error.Code);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, ".staging")));
        }
        finally { release.TrySetResult(); }
        await first;
        async IAsyncEnumerable<AefUploadFile> Paused()
        {
            entered.SetResult();
            await release.Task;
            using var stream = new MemoryStream();
            yield return new("run.json", stream);
        }
    }

    [Fact]
    public void Constructor_InvalidBounds_RefusesBeforeReservation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AefRunStager(_workspace, new() { MaxBytes = 0 }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".staging")));
    }

    public void Dispose()
    {
        _workspace.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
