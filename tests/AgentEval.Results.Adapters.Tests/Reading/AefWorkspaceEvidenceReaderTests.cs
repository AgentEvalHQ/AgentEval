// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Adapters.Tests.StoreV1;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Reading;

public sealed class AefWorkspaceEvidenceReaderTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("ae-workspace-evidence");
    private readonly AefWorkspaceEvidenceReader _reader = new();
    public void Dispose() => AefTestRuns.Delete(_root);

    private static string Native(string workspace, string subject = "agent:x", string? alias = null, bool trials = false, string? job = null)
    {
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var leaf = new EvalResult(new("case", "case", "test", "1.0.0"), new(0.8, null, "scored", false, null, "none", null),
            new(null, null, null, null, null), new("atomic-code", null, null, null, null, 0, false), start);
        var output = Path.Combine(workspace, "aef", "runs");
        if (job is not null)
        {
            var parent = Directory.CreateDirectory(Path.Combine(workspace, "aef", "jobs", job));
            File.WriteAllText(Path.Combine(parent.FullName, "runner.json"), "{}"); // Locator only; no parent-job claim.
            File.WriteAllText(Path.Combine(parent.FullName, "seal.json"), "{}"); // Never treated as a child-run seal.
            output = Path.Combine(parent.FullName, "runs");
        }
        return AefCompositeRunWriter.Write(output, leaf, AefCompositeRunWriter.Undescribed,
            new() { SubjectRef = subject, TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "bench:test",
                StoreV1 = alias is null ? null : trials ? new() { TrialRunIds = [alias] } : new() { RunId = alias } }, start, start.AddSeconds(1)).Directory;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_JobChildRun_UsesSameVerificationWithoutInferringParentStatus(bool tamper)
    {
        var directory = Native(_root, subject: "model:job", job: "job-proof");
        if (tamper)
        {
            var path = Path.Combine(directory, "run.json");
            var header = AefTestRuns.Document(directory, "run.json");
            header["subject"]!["ref"] = "model:changed";
            File.WriteAllText(path, header.ToJsonString());
            Assert.Throws<InvalidDataException>(() => _reader.Read(_root));
            return;
        }
        var run = Assert.Single(_reader.Read(_root).Runs);
        Assert.False(run.Evidence.Info.Imported);
        Assert.Equal("intact", run.Evidence.Info.Integrity);
        Assert.Equal("model:job", run.Evidence.Info.SubjectRef);
        Assert.Equal(run.SourceId, Assert.Single(_reader.Read(_root).Runs).SourceId);
    }

    [Fact]
    public void Read_JobChildRuns_ShareTheWorkspaceByteBudget()
    {
        Native(_root);
        Native(_root, job: "job-proof");
        var runs = _reader.Read(_root).Runs;
        Assert.Equal(2, runs.Count);
        Assert.ThrowsAny<IOException>(() => _reader.Read(_root, runs.Sum(r => r.Evidence.Bytes) - 1));
    }

    [Fact]
    public void Read_JobWithNestedRunsButMissingLocator_RefusesPartialDiscovery()
    {
        Native(_root, job: "job-proof");
        File.Delete(Path.Combine(_root, "aef", "jobs", "job-proof", "runner.json"));
        Assert.Throws<InvalidDataException>(() => _reader.Read(_root));
    }

    [Fact]
    public void Read_MissingWorkspace_ReturnsEmptyWithoutInitializingIt()
    {
        var read = _reader.Read(_root);
        Assert.Empty(read.Runs);
        Assert.Null(read.FindRun("unknown"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Read_NativeOnly_DoesNotRequireSolutionAndKeepsStableOpaqueSource()
    {
        Native(_root);
        var first = Assert.Single(_reader.Read(_root).Runs);
        var second = Assert.Single(_reader.Read(_root).Runs);
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.StartsWith("source-", first.SourceId);
        Assert.DoesNotContain(_root, first.SourceId);
        Assert.False(File.Exists(Path.Combine(_root, "solution.json")));
        Assert.Equal(first.Evidence.Info.RunId, second.Evidence.Info.RunId);
        Assert.Equal(first.SourceId, _reader.Read(_root).FindRun(first.Evidence.Info.RunId)!.SourceId);
    }

    [Fact]
    public async Task Read_LegacyOnly_ConvertsExactFolderAndPreservesImportCustody()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var read = _reader.Read(fixture.Root);
        var run = Assert.Single(read.Runs);
        Assert.True(run.Evidence.Info.Imported);
        Assert.Equal("intact", run.Evidence.Info.Integrity);
        Assert.Equal("not_checked", run.Evidence.Info.SignatureTrust);
        Assert.Equal(fixture.RunId, run.Evidence.Info.RunId);
        Assert.NotEmpty(run.Evidence.Results);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "aef")));
        Assert.Equal(run.SourceId, read.FindRun(fixture.RunId)!.SourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_NativeAlias_SuppressesOnlyItsSubjectScopedLegacySibling(bool trials)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var subjectRef = AefEvalWriter.Ref("agent", StoreV1Fixture.Subject.Name);
        Native(fixture.Root, subjectRef, fixture.RunId, trials);
        var read = _reader.Read(fixture.Root);
        var run = Assert.Single(read.Runs);
        Assert.False(run.Evidence.Info.Imported);
        Assert.Equal(run.SourceId, read.FindRun(fixture.RunId)!.SourceId);
        Assert.Equal("off", run.Evidence.Info.ContentCapture);
        Assert.Empty(run.Evidence.Evidence); // Never recover the linked legacy report/content through capture-off native evidence.
    }

    [Fact]
    public async Task Read_AliasOnDifferentSubject_DoesNotSuppressLegacyAndRequiresSourceForSelection()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        Native(fixture.Root, "workflow:other", fixture.RunId);
        var read = _reader.Read(fixture.Root);
        Assert.Equal(2, read.Runs.Count);
        Assert.Throws<InvalidDataException>(() => read.FindRun(fixture.RunId));
        var legacy = Assert.Single(read.Runs, r => r.Evidence.Info.Imported);
        Assert.Same(legacy, read.FindRun(fixture.RunId, legacy.SourceId));
        Assert.Null(read.FindRun(fixture.RunId, "wrong-source"));
    }

    [Fact]
    public void Read_DuplicateNativeExecutionIDs_RequiresExactSource()
    {
        var directory = Native(_root);
        var duplicate = Path.Combine(_root, "aef", "runs", "duplicate");
        Copy(directory, duplicate);
        var read = _reader.Read(_root);
        Assert.Equal(2, read.Runs.Count);
        var id = read.Runs[0].Evidence.Info.RunId;
        Assert.Throws<InvalidDataException>(() => read.FindRun(id));
        Assert.NotEqual(read.Runs[0].SourceId, read.Runs[1].SourceId);
        foreach (var run in read.Runs) Assert.Same(run, read.FindRun(id, run.SourceId));
    }

    [Fact]
    public async Task Read_UnlinkedNativeAndLegacyShareIdAndSubject_PreservesBothSources()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        // Converting an independent copy gives valid AEF with the same recorded ID and subject,
        // but no explicit alias asserting that it supersedes this workspace's legacy source.
        StoreV1Exporter.ExportWorkspaceRun(fixture.Root, fixture.RunDirectory, Path.Combine(fixture.Root, "aef", "runs", "copy"));
        var read = _reader.Read(fixture.Root);
        Assert.Equal(2, read.Runs.Count);
        Assert.Throws<InvalidDataException>(() => read.FindRun(fixture.RunId));
    }

    [Fact]
    public async Task Read_TwoNativeSourcesClaimSameLegacyExecution_Refuses()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var subject = AefEvalWriter.Ref("agent", StoreV1Fixture.Subject.Name);
        Native(fixture.Root, subject, fixture.RunId);
        Native(fixture.Root, subject, fixture.RunId);
        Assert.Throws<InvalidDataException>(() => _reader.Read(fixture.Root));
    }

    [Fact]
    public async Task Read_TamperedNativeWithLegacyAvailable_RefusesBeforeFallback()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var directory = Native(fixture.Root);
        File.AppendAllText(Path.Combine(directory, "run.json"), " ");
        var error = Assert.Throws<InvalidDataException>(() => _reader.Read(fixture.Root));
        Assert.Contains("invalid AEF evidence", error.Message);
    }

    [Fact]
    public void Read_BudgetIsSharedAcrossNativeSources_RefusesPartialCatalog()
    {
        Native(_root); Native(_root);
        var total = _reader.Read(_root).Runs.Sum(r => r.Evidence.Bytes);
        Assert.Equal(2, _reader.Read(_root, total).Runs.Count);
        Assert.ThrowsAny<IOException>(() => _reader.Read(_root, total - 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_LegacyInputLargerThanOutput_ChargesAllSourceBytesBeforeConversion(bool compliance)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var initial = Assert.Single(_reader.Read(fixture.Root).Runs).Evidence.Bytes;
        var directory = compliance
            ? Path.GetDirectoryName(Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "compliance"), "evidence.json", SearchOption.AllDirectories)))!
            : fixture.RunDirectory;
        File.WriteAllBytes(Path.Combine(directory, "padding.bin"), new byte[2 << 20]);
        Assert.ThrowsAny<IOException>(() => _reader.Read(fixture.Root, initial + (1 << 20)));
    }

    [Theory]
    [InlineData("object")]
    [InlineData("runId")]
    [InlineData("trials")]
    [InlineData("trialId")]
    public void Read_MalformedRecordedAlias_RefusesEvenWhenSealed(string defect)
    {
        var directory = Native(_root, alias: "legacy");
        var header = AefTestRuns.Document(directory, "run.json");
        var ext = header["ext"]!;
        ext["agenteval.store-v1"] = defect switch
        {
            "object" => JsonValue.Create(1),
            "runId" => new JsonObject { ["runId"] = 1 },
            "trials" => new JsonObject { ["trialRunIds"] = "x" },
            _ => new JsonObject { ["trialRunIds"] = new JsonArray(1) },
        };
        File.WriteAllText(Path.Combine(directory, "run.json"), header.ToJsonString());
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        Assert.Throws<InvalidDataException>(() => _reader.Read(_root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1073741825)]
    public void Read_InvalidBudget_RefusesBeforeDiscovery(long bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _reader.Read(_root, bytes));

    [Fact]
    public void Read_Cancelled_RefusesBeforeDiscovery() =>
        Assert.Throws<OperationCanceledException>(() => _reader.Read(_root, ct: new(true)));

    private static void Copy(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var output = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output);
        }
    }
}
