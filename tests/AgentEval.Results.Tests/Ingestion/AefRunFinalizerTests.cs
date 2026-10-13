// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Integrity;

namespace AgentEval.Results.Tests.Ingestion;

public sealed class AefRunFinalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-finalize-" + Guid.NewGuid().ToString("N"));
    private readonly AefIngestionWorkspace _workspace;
    public AefRunFinalizerTests() => _workspace = new(new() { Root = _root, MaxAcceptedRuns = 1 });

    private Task<AefStagedRun> Stage(TestRun run) => new AefRunStager(_workspace).StageAsync(Files(run.Dir));
    private static async IAsyncEnumerable<AefUploadFile> Files(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            await using var input = File.OpenRead(path);
            yield return new(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), input);
        }
    }
    private string State(AefStagedRun stage)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(stage.Directory)!, "state.json")));
        return json.RootElement.GetProperty("status").GetString()!;
    }
    private AefFinalizationException Refused(AefStagedRun stage) =>
        Assert.Throws<AefFinalizationException>(() => new AefRunFinalizer(_workspace).FinalizeStage(stage.Id));

    [Fact]
    public async Task FinalizeStage_IntactSignedRun_PublishesOriginalBytesWithVerifiedHash()
    {
        using var signer = EcdsaP256Signer.Generate();
        using var source = new TestRun().Write().Seal().Attest(signer);
        source.AppendBatch([TestRun.Event("ov_1", "annotate", new())], signer);
        var stage = await Stage(source);
        var result = new AefRunFinalizer(_workspace).FinalizeStage(stage.Id);
        Assert.Equal(source.Verify().RunHash!.Value.Value, result.RunHash);
        Assert.Equal(Path.Combine(_workspace.RunsDirectory, result.RunHash), result.Directory);
        Assert.True(result.ReceiptPersisted);
        Assert.False(Directory.Exists(stage.Directory));
        Assert.Equal("accepted", State(stage));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(result.Directory).Outcome);
        Assert.Null(AefRunVerifier.Verify(result.Directory).SignedBy); // integrity is not signer trust
        foreach (var file in Directory.EnumerateFiles(source.Dir, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(result.Directory, Path.GetRelativePath(source.Dir, file))));
        Assert.False(File.Exists(Path.Combine(result.Directory, "state.json")));
    }

    [Fact]
    public async Task FinalizeStage_AbortedButIntactRun_PreservesItsStatus()
    {
        using var source = new TestRun();
        source.Run["status"] = "aborted";
        source.Run["abortReason"] = "cancelled";
        source.Write().Seal();
        var stage = await Stage(source);
        var result = new AefRunFinalizer(_workspace).FinalizeStage(stage.Id);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "run.json")));
        Assert.Equal("aborted", run.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FinalizeStage_UnsealedRun_DoesNotPublish()
    {
        using var source = new TestRun().Write();
        Assert.Equal("unsealed", Refused(await Stage(source)).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.RunsDirectory));
    }

    [Fact]
    public async Task FinalizeStage_SameSizeTampering_FailsProtocolVerification()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        var path = Path.Combine(stage.Directory, "results.ndjson");
        var bytes = File.ReadAllBytes(path);
        bytes[0] = (byte)'x';
        File.WriteAllBytes(path, bytes);
        Assert.Equal("invalid", Refused(stage).Code);
        Assert.Equal("staged", State(stage));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.RunsDirectory));
    }

    [Fact]
    public async Task FinalizeStage_AddedFile_RejectsInventoryMismatch()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        File.WriteAllText(Path.Combine(stage.Directory, "extra"), "x");
        Assert.Equal("state", Refused(stage).Code);
    }

    [Fact]
    public async Task FinalizeStage_InvalidOverlay_RejectsEvenWhenBaseSealIsIntact()
    {
        using var source = new TestRun().Write().Seal();
        source.WriteText("overlays/unexpected", "not an overlay");
        Assert.Equal(AefOutcome.Intact, source.Verify().Outcome);
        Assert.Equal("invalid", Refused(await Stage(source)).Code);
    }

    [Theory]
    [InlineData("receiving")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    [InlineData("publishing")]
    [InlineData("accepted")]
    public async Task FinalizeStage_IncompleteOrTerminalState_Refuses(string status)
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        AefStageState.Write(_workspace.OpenStage(stage.Id), status, stage.FileCount, stage.Bytes);
        Assert.Equal("state", Refused(stage).Code);
    }

    [Fact]
    public async Task FinalizeStage_DuplicateHash_DoesNotOverwriteAndRestoresStagedState()
    {
        using var source = new TestRun().Write().Seal();
        var first = await Stage(source);
        var finalizer = new AefRunFinalizer(_workspace);
        var accepted = finalizer.FinalizeStage(first.Id);
        var seal = File.ReadAllBytes(Path.Combine(accepted.Directory, "seal.json"));
        var duplicate = await Stage(source);
        Assert.Equal("conflict", Refused(duplicate).Code);
        Assert.Equal("staged", State(duplicate));
        Assert.True(Directory.Exists(duplicate.Directory));
        Assert.Equal(seal, File.ReadAllBytes(Path.Combine(accepted.Directory, "seal.json")));
    }

    [Fact]
    public async Task FinalizeStage_AcceptedCapacity_LeavesNextStageUnpublished()
    {
        using var first = new TestRun().Write().Seal();
        new AefRunFinalizer(_workspace).FinalizeStage((await Stage(first)).Id);
        using var second = new TestRun();
        second.Run["producer"]!["version"] = "2.0";
        second.Write().Seal();
        var stage = await Stage(second);
        Assert.Equal("capacity", Refused(stage).Code);
        Assert.Equal("staged", State(stage));
        Assert.Single(Directory.EnumerateDirectories(_workspace.RunsDirectory));
    }

    [Fact]
    public async Task FinalizeStage_PreCancelled_DoesNotChangeState()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        Assert.Throws<OperationCanceledException>(() => new AefRunFinalizer(_workspace).FinalizeStage(stage.Id, new(true)));
        Assert.Equal("staged", State(stage));
    }

    [Fact]
    public async Task FinalizeStage_ChangedCurrentLimits_RefusesBeforeVerification()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        var finalizer = new AefRunFinalizer(_workspace, new() { MaxBytes = 1, MaxFileBytes = 1 });
        Assert.Equal("state", Assert.Throws<AefFinalizationException>(() => finalizer.FinalizeStage(stage.Id)).Code);
    }

    [Fact]
    public async Task FinalizeStage_StageLockHeld_RefusesConcurrentPublication()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(stage.Directory)!, "finalize.lock"), FileMode.Create, FileAccess.Write, FileShare.None);
        Assert.Throws<IOException>(() => new AefRunFinalizer(_workspace).FinalizeStage(stage.Id));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.RunsDirectory));
    }

    [Fact]
    public async Task FinalizeStage_LinkedContent_Refuses()
    {
        if (OperatingSystem.IsWindows()) return;
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        var path = Path.Combine(stage.Directory, "run.json");
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(source.Dir, "run.json"));
        Assert.Equal("path", Refused(stage).Code);
    }

    [Fact]
    public async Task FinalizeStage_OversizedState_RefusesBoundedRead()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(stage.Directory)!, "state.json"), new string('x', 4097));
        Assert.Equal("state", Refused(stage).Code);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void FinalizeStage_InvalidId_Refuses(string id)
    {
        Assert.Equal("path", Assert.Throws<AefFinalizationException>(() => new AefRunFinalizer(_workspace).FinalizeStage(id)).Code);
    }

    [Fact]
    public async Task FinalizeStage_CancelledAtCommit_DoesNotPublish()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        using var cancellation = new CancellationTokenSource();
        var workspace = new CommitHook(_workspace, before: cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() => new AefRunFinalizer(workspace).FinalizeStage(stage.Id, cancellation.Token));
        Assert.Equal("staged", State(stage));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.RunsDirectory));
    }

    [Fact]
    public async Task FinalizeStage_ReceiptFailureAfterCommit_ReportsPublishedEvidence()
    {
        using var source = new TestRun().Write().Seal();
        var stage = await Stage(source);
        using var cancellation = new CancellationTokenSource();
        var workspace = new CommitHook(_workspace, after: () =>
        {
            cancellation.Cancel();
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(stage.Directory)!, "state.tmp"));
        });
        var result = new AefRunFinalizer(workspace).FinalizeStage(stage.Id, cancellation.Token);
        Assert.False(result.ReceiptPersisted);
        Assert.Equal("publishing", State(stage));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(result.Directory).Outcome);
    }

    [Fact]
    public async Task LocalImporter_DiscoveredRun_StagesAndFinalizesWithoutMutatingSource()
    {
        using var source = new TestRun().Write().Seal();
        var candidate = Assert.Single(new AefRunDiscovery().Discover(source.Dir).Candidates);
        var importer = new AefLocalRunImporter(new AefRunStager(_workspace));
        var stage = await importer.StageDirectoryAsync(candidate.Directory);
        var accepted = new AefRunFinalizer(_workspace).FinalizeStage(stage.Id);
        Assert.True(Directory.Exists(source.Dir));
        Assert.Equal(source.Verify().RunHash!.Value.Value, accepted.RunHash);
    }

    [Fact]
    public async Task LocalImporter_LinkedSource_RefusesBeforeReservation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var source = new TestRun().Write().Seal();
        var path = Path.Combine(source.Dir, "run.json");
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(source.Dir, "summary.json"));
        await Assert.ThrowsAsync<AefStagingException>(() => new AefLocalRunImporter(new AefRunStager(_workspace)).StageDirectoryAsync(source.Dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".staging")));
    }

    [Fact]
    public async Task LocalImporter_UnavailableNativeClassifier_RefusesRatherThanOpeningUnknownTypes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var source = new TestRun().Write().Seal();
        using var fallback = AgentEval.Results.Runs.AefFolder.UseFallbackForTesting();
        await Assert.ThrowsAsync<IOException>(() => new AefLocalRunImporter(new AefRunStager(_workspace)).StageDirectoryAsync(source.Dir));
    }

    private sealed class CommitHook(IAefIngestionWorkspace inner, Action? before = null, Action? after = null) : IAefIngestionWorkspace
    {
        public string Root => inner.Root;
        public string RunsDirectory => inner.RunsDirectory;
        public AefIngestionStage BeginStage(CancellationToken cancellationToken = default) => inner.BeginStage(cancellationToken);
        public AefIngestionStage OpenStage(string id) => inner.OpenStage(id);
        public string PublishVerifiedStage(string id, string hash, CancellationToken cancellationToken = default)
        {
            before?.Invoke();
            var result = inner.PublishVerifiedStage(id, hash, cancellationToken);
            after?.Invoke();
            return result;
        }
        public void Dispose() { }
    }

    public void Dispose()
    {
        _workspace.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
