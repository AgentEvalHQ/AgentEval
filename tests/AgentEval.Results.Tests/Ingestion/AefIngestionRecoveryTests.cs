// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;
using AgentEval.Results.Tests.Integrity;

namespace AgentEval.Results.Tests.Ingestion;

public sealed class AefIngestionRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly AefIngestionWorkspace _workspace;
    public AefIngestionRecoveryTests() => _workspace = new(new() { Root = _root, MaxPendingStages = 2, MaxArchivedReceipts = 2 });
    private AefIngestionRecovery Recovery => new(_workspace, new AefRunFinalizer(_workspace));

    private AefIngestionStage Pending(string status)
    {
        var stage = _workspace.BeginStage();
        var content = Directory.CreateDirectory(Path.Combine(stage.Directory, "content")).FullName;
        File.WriteAllText(Path.Combine(content, "partial"), "scratch");
        AefStageState.Write(stage, status, 0, 0);
        return stage;
    }

    private Task<AefStagedRun> Stage(TestRun source) => new AefLocalRunImporter(new AefRunStager(_workspace)).StageDirectoryAsync(source.Dir);

    [Fact]
    public async Task Finalize_ArchivedReceipt_ReleasesPendingCapacityAndSurvivesRestart()
    {
        using var source = new TestRun().Write().Seal();
        var first = await Stage(source);
        var accepted = new AefRunFinalizer(_workspace).FinalizeStage(first.Id);
        Assert.True(accepted.ReceiptArchived);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".staging")));
        Assert.Equal(Path.Combine(_root, ".receipts", first.Id), _workspace.OpenStage(first.Id).Directory);
        _workspace.Dispose();
        using var reopened = new AefIngestionWorkspace(new() { Root = _root });
        var retry = new AefRunFinalizer(reopened).FinalizeStage(first.Id);
        Assert.True(retry.AlreadyAccepted);
        Assert.True(retry.ReceiptArchived);
    }

    [Fact]
    public async Task Finalize_ExactDuplicate_ArchivesControlOnlyAndHonorsReceiptCapacity()
    {
        using var source = new TestRun().Write().Seal();
        var finalizer = new AefRunFinalizer(_workspace);
        finalizer.FinalizeStage((await Stage(source)).Id);
        var duplicate = await Stage(source);
        Assert.True(finalizer.FinalizeStage(duplicate.Id).ReceiptArchived);
        Assert.False(Directory.Exists(Path.Combine(_workspace.OpenStage(duplicate.Id).Directory, "content")));
        var third = await Stage(source);
        var result = finalizer.FinalizeStage(third.Id);
        Assert.True(result.ReceiptPersisted);
        Assert.False(result.ReceiptArchived);
        Assert.Equal(Path.Combine(_root, ".staging", third.Id), _workspace.OpenStage(third.Id).Directory);
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(_root, ".receipts")).Count());
        Assert.Single(Directory.EnumerateDirectories(_workspace.RunsDirectory));
    }

    [Fact]
    public void Recover_AbandonedReceivingStage_MarksInterruptedWithoutDeletingBytes()
    {
        var stage = Pending("receiving");
        var result = Recovery.Recover(stage.Id);
        Assert.Equal("interrupted", result.Status);
        Assert.Equal("scratch", File.ReadAllText(Path.Combine(stage.Directory, "content", "partial")));
        Assert.Equal("interrupted", AefStageState.Read(stage).Status);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("interrupted")]
    [InlineData("staged")]
    [InlineData("discarding")]
    public void DiscardStage_EligibleStage_RemovesScratchAndKeepsRetryTombstone(string status)
    {
        var stage = Pending(status);
        var result = Recovery.DiscardStage(stage.Id);
        Assert.Equal("discarded", result.Status);
        Assert.True(result.ReceiptArchived);
        Assert.False(Directory.Exists(stage.Directory));
        var archived = _workspace.OpenStage(stage.Id);
        Assert.False(Directory.Exists(Path.Combine(archived.Directory, "content")));
        Assert.Equal("discarded", AefStageState.Read(archived).Status);
        Assert.True(Recovery.DiscardStage(stage.Id).ReceiptArchived);
        Assert.True(Recovery.Recover(stage.Id).ReceiptArchived);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.RunsDirectory));
    }

    [Theory]
    [InlineData("receiving")]
    [InlineData("publishing")]
    [InlineData("accepted")]
    public void DiscardStage_IneligibleStage_DoesNotDelete(string status)
    {
        var stage = Pending(status);
        Assert.Equal("state", Assert.Throws<AefFinalizationException>(() => Recovery.DiscardStage(stage.Id)).Code);
        Assert.True(File.Exists(Path.Combine(stage.Directory, "content", "partial")));
        Assert.Equal(status, AefStageState.Read(stage).Status);
    }

    [Fact]
    public void Recover_UnknownNewStage_DoesNotClassifyOrDelete()
    {
        var stage = _workspace.BeginStage();
        Assert.Throws<FileNotFoundException>(() => Recovery.Recover(stage.Id));
        Assert.Throws<FileNotFoundException>(() => Recovery.DiscardStage(stage.Id));
        Assert.True(Directory.Exists(stage.Directory));
        Assert.False(File.Exists(Path.Combine(stage.Directory, "state.json")));
    }

    [Fact]
    public void Inspect_BoundedAndCancelled_DoesNotMutateStages()
    {
        Pending("failed"); Pending("interrupted");
        var scan = Recovery.Inspect(1);
        Assert.False(scan.Complete);
        Assert.Single(scan.Stages);
        Assert.True(Recovery.Inspect(2).Complete);
        Assert.Throws<ArgumentOutOfRangeException>(() => Recovery.Inspect(1025));
        Assert.Throws<OperationCanceledException>(() => Recovery.Inspect(cancellationToken: new(true)));
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(_root, ".staging")).Count());
    }

    [Fact]
    public async Task RecoverAndDiscard_ActiveUpload_CannotAcquireLease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new AefRunStager(_workspace).StageAsync(Paused());
        await entered.Task;
        try
        {
            var id = Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, ".staging"))));
            Assert.Equal("unavailable", Assert.Single(Recovery.Inspect().Stages).Status);
            Assert.Throws<IOException>(() => Recovery.Recover(id));
            Assert.Throws<IOException>(() => Recovery.DiscardStage(id));
        }
        finally { release.TrySetResult(); }
        var staged = await task;
        Assert.Equal("staged", AefStageState.Read(_workspace.OpenStage(staged.Id)).Status);
        async IAsyncEnumerable<AefUploadFile> Paused()
        {
            entered.TrySetResult();
            await release.Task;
            using var input = new MemoryStream("{}"u8.ToArray());
            yield return new("run.json", input);
        }
    }

    [Fact]
    public void DiscardStage_LinkedScratch_RefusesWithoutTouchingTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        var stage = Pending("failed");
        var external = Path.Combine(_root, "kept");
        File.WriteAllText(external, "keep");
        File.CreateSymbolicLink(Path.Combine(stage.Directory, "content", "link"), external);
        Assert.Throws<AefStagingException>(() => Recovery.DiscardStage(stage.Id));
        Assert.Equal("keep", File.ReadAllText(external));
        Assert.Equal("failed", AefStageState.Read(stage).Status);
    }

    public void Dispose()
    {
        _workspace.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
