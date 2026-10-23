// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Adapters.Tests.StoreV1;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Reading;

public sealed class AefRunEvidenceReaderTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("ae-read-all");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_root);

    private string Native(bool seal = true, bool aborted = false)
    {
        EvalResult Leaf(string key) => new(new(key, key, "test", "1.0.0"),
            new(0.8, null, "pass", true, null, "none", null),
            new(null, null, null, null, null), new("atomic-code", null, null, null, null, 0, false), Start);
        EvalResult Group(string key, params EvalResult[] children) => new(new(key, key, "test", "1.0.0"),
            new(0.8, null, "pass", true, 0.7, "none", null),
            new(null, null, null, children, "WeightedSum") { Lineage = new("threshold", []) },
            new("composite", null, null, null, null, 0, false), Start);
        var options = new AefCompositeRunOptions
        {
            SubjectRef = "model:model-x", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "bench:test",
            Seal = seal, AbortReason = aborted ? "cancelled" : null,
        };
        return (aborted
            ? AefCompositeRunWriter.WriteAbortedEmpty(_root, Leaf("preset"), options, Start, Start.AddSeconds(1))
            : AefCompositeRunWriter.Write(_root, Group("preset", Group("group", Leaf("a"), Leaf("b"))),
                AefCompositeRunWriter.Undescribed, options, Start, Start.AddSeconds(1))).Directory;
    }

    [Theory]
    [InlineData(true, "intact")]
    [InlineData(false, "unsealed")]
    public void Read_NativeTree_PreservesEveryDocumentAndObservedBoundary(bool seal, string integrity)
    {
        var directory = Native(seal);
        var before = Digest(directory);
        var read = new AefRunEvidenceReader().Read(directory);
        Assert.Equal(integrity, read.Info.Integrity);
        Assert.Equal("not_checked", read.Info.SignatureTrust);
        Assert.Equal("model:model-x", read.Info.SubjectRef);
        Assert.False(read.Info.Imported);
        Assert.Equal(Start, read.StartedAt);
        Assert.Equal(Start.AddSeconds(1), read.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(1), read.Duration);
        Assert.Equal(3, read.Results.Count);
        Assert.Equal(2, read.Results.Count(r => r.TryGetProperty("parentResultId", out _)));
        AssertJson(File.ReadAllText(Path.Combine(directory, "run.json")), read.Header);
        AssertJson(File.ReadAllText(Path.Combine(directory, "metrics.json")), read.Metrics);
        AssertJson(File.ReadAllText(Path.Combine(directory, "summary.json")), read.Summary!.Value);
        var lines = File.ReadAllLines(Path.Combine(directory, "results.ndjson"));
        Assert.Equal(lines.Length, read.Results.Count);
        for (var i = 0; i < lines.Length; i++) AssertJson(lines[i], read.Results[i]);
        Assert.Equal(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), read.Bytes);
        Assert.Equal(before, Digest(directory));
        Directory.Delete(directory, recursive: true);
        Assert.Equal("model:model-x", read.Header.GetProperty("subject").GetProperty("ref").GetString());
        Assert.Equal(3, read.Results.Count); // Detached data survives source and snapshot disposal.
    }

    [Fact]
    public void Read_AbortedEmptyRun_DoesNotInventResultsOrCompletion()
    {
        var read = new AefRunEvidenceReader().Read(Native(aborted: true));
        Assert.Equal("aborted", read.Info.Status);
        Assert.Equal("cancelled", read.Info.AbortReason);
        Assert.Empty(read.Results);
        Assert.Empty(read.Gates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_ConvertedLegacyRun_PreservesObservedStartAndOnlyRecordedEnd(bool complete)
    {
        using var fixture = await StoreV1Fixture.CreateAsync(complete);
        var conversion = StoreV1Exporter.ExportWorkspaceRun(fixture.Root, fixture.RunDirectory, _root);
        var read = new AefRunEvidenceReader().Read(conversion.Directory);
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        Assert.True(read.Info.Imported);
        Assert.Equal(manifest.Run.Timestamp.ToUnixTimeMilliseconds(), read.StartedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(complete, read.EndedAt.HasValue);
        Assert.Equal(complete, read.Duration.HasValue);
        Assert.Equal(complete ? "completed" : "aborted", read.Info.Status);
        Assert.Equal(4, read.Results.Count(r => !r.TryGetProperty("parentResultId", out _)));
    }

    [Fact]
    public async Task Read_ReportOnlyImport_KeepsPlaceholderTimesOutOfExecutionObservations()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var report = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "compliance"), "evidence.json", SearchOption.AllDirectories));
        StoreV1Exporter.ExportComplianceDirectory(Path.GetDirectoryName(report)!, _root);
        var read = new AefRunEvidenceReader().Read(_root);
        Assert.True(read.Info.Imported);
        Assert.Contains("startedAt", read.Info.Asserted);
        Assert.Null(read.StartedAt);
        Assert.Null(read.EndedAt);
        Assert.Null(read.Duration);
        Assert.NotEmpty(read.Evidence);
        Assert.Empty(read.Results);
    }

    [Theory]
    [InlineData("run.json")]
    [InlineData("results.ndjson")]
    [InlineData("metrics.json")]
    [InlineData("summary.json")]
    public void Read_TamperedDocument_Refuses(string file)
    {
        var directory = Native();
        File.AppendAllText(Path.Combine(directory, file), " ");
        Assert.Throws<InvalidDataException>(() => new AefRunEvidenceReader().Read(directory));
    }

    [Fact]
    public void Read_ByteBudget_UsesAllCapturedBytesAndRefusesOneByteLess()
    {
        var directory = Native();
        var reader = new AefRunEvidenceReader();
        var read = reader.Read(directory);
        Assert.Equal(read.Bytes, reader.Read(directory, read.Bytes).Bytes);
        Assert.ThrowsAny<IOException>(() => reader.Read(directory, read.Bytes - 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1073741825)]
    public void Read_InvalidBudget_Refuses(long budget) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AefRunEvidenceReader().Read(_root, budget));

    [Fact]
    public void Read_Cancelled_ThrowsBeforeOpeningSource() =>
        Assert.Throws<OperationCanceledException>(() => new AefRunEvidenceReader().Read(_root, ct: new(true)));

    [Theory]
    [InlineData("run.json")]
    [InlineData("metrics.json")]
    [InlineData("results.ndjson")]
    [InlineData("seal.json")]
    public void Discover_DamagedRunWithOnlyOneMarker_IsStillAReaderCandidate(string marker)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, marker), "{}");
        Assert.Equal(_root, Assert.Single(AefEvidenceDiscovery.Discover(_root)));
        Assert.Throws<InvalidDataException>(() => new AefRunEvidenceReader().Read(_root));
    }

    [Fact]
    public void Discover_MultipleMarkers_DeduplicatesOneRun() =>
        Assert.Equal(Native(), Assert.Single(AefEvidenceDiscovery.Discover(_root)));

    [Fact]
    public void Discover_MissingRoot_ReturnsEmptyWithoutCreatingIt()
    {
        Assert.Empty(AefEvidenceDiscovery.Discover(_root));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Discover_CancelledMissingRoot_Throws() =>
        Assert.Throws<OperationCanceledException>(() => AefEvidenceDiscovery.Discover(_root, ct: new(true)));

    [Theory]
    [InlineData("depth")]
    [InlineData("entries")]
    [InlineData("runs")]
    public void Discover_IncompleteScan_Refuses(string bound)
    {
        Native(); Native();
        var options = bound switch
        {
            "depth" => new AefDiscoveryOptions { MaxDepth = 0 },
            "entries" => new AefDiscoveryOptions { MaxEntries = 1 },
            _ => new AefDiscoveryOptions { MaxRuns = 1 },
        };
        Assert.Throws<InvalidDataException>(() => AefEvidenceDiscovery.Discover(_root, options));
    }

    [Fact]
    public void Discover_CombinedMarkerCandidates_EnforcesSharedRunLimit()
    {
        foreach (var name in new[] { "run.json", "seal.json" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(directory.FullName, name), "{}");
        }
        Assert.Throws<InvalidDataException>(() => AefEvidenceDiscovery.Discover(_root, new() { MaxRuns = 1 }));
    }

    [Fact]
    public void Discover_InvalidBoundsEvenWhenMissing_Refuses() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AefEvidenceDiscovery.Discover(_root, new() { MaxRuns = 0 }));

    private static void AssertJson(string expected, JsonElement actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())));

    private static string Digest(string directory) => string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(directory, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
}
