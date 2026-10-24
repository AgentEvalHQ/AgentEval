// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Adapters.Tests.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Reading;

public sealed class AefComplianceReadBudgetTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("compliance-budget");
    private string Runs => Path.Combine(_root, "runs");
    public void Dispose() => AefTestRuns.Delete(_root);

    private string Native()
    {
        var start = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        return AefProbeRunWriter.Write(Runs, [new() { CaseId = "probe", State = AefState.Passed }],
            new() { SubjectRef = "agent:Other", SuiteRef = "redteam:budget", TargetMode = AefTargetMode.Mocked },
            start, start.AddSeconds(1)).Directory;
    }

    [Fact]
    public void Read_MultipleIrrelevantNativeCandidates_ConsumeOneBudgetBeforeFiltering()
    {
        Native(); Native();
        var before = Digest(Runs);
        var bytes = Size(Runs);
        var reader = new AefComplianceReportReader();
        Assert.Null(reader.ReadBounded(Runs, [], "Support Agent", "gdpr", new(bytes)));
        Assert.ThrowsAny<IOException>(() => reader.ReadBounded(Runs, [], "Support Agent", "gdpr", new(bytes - 1)));
        Assert.Equal(before, Digest(Runs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_LegacyCaptureIncludingDiscardedFiles_SharesBudgetWithNative(bool native)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var directory = Legacy(source);
        File.WriteAllBytes(Path.Combine(directory, "ignored.bin"), new byte[128 << 10]);
        var before = Digest(directory);
        var nativeBytes = native ? Size(Native()) : 0;
        var limit = nativeBytes + Size(directory) - 1;
        Assert.ThrowsAny<IOException>(() => new AefComplianceReportReader()
            .ReadBounded(Runs, [directory], "Support Agent", "gdpr", new(limit)));
        Assert.Equal(before, Digest(directory));
    }

    [Fact]
    public async Task Read_ConversionWritesSealAndVerifiedReread_ConsumeCapacityOncePerOperation()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var directory = Legacy(source);
        const long capacity = 1 << 20;
        var control = new AefReadBudget(capacity);
        var conversion = StoreV1Exporter.ExportComplianceDirectoryBounded(directory, Path.Combine(_root, "control"), null, default, control);
        var conversionWork = capacity - control.Remaining;
        var outputBytes = Size(conversion.Directory);
        var limited = conversionWork - new FileInfo(Path.Combine(conversion.Directory, "seal.json")).Length / 2;
        Assert.True(conversionWork > Size(directory));
        var reader = new AefComplianceReportReader();
        Assert.Throws<InvalidDataException>(() => reader.ReadBounded(Runs, [directory], "Support Agent", "gdpr", new(limited)));
        // A small allowance covers timestamp spelling; a second charge for the same reread would exceed it.
        var complete = new AefReadBudget(conversionWork + outputBytes + 128);
        Assert.Null(reader.ReadBounded(Runs, [directory], "Support Agent", "gdpr", complete));
        Assert.InRange(complete.Remaining, 0, 256);
        var refused = Path.Combine(_root, "refused");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportComplianceDirectoryBounded(directory, refused, null, default, new(limited)));
        Assert.False(Directory.Exists(refused));
    }

    private static string Legacy(StoreV1Fixture source) => Path.GetDirectoryName(Assert.Single(
        Directory.EnumerateFiles(Path.Combine(source.Root, "compliance"), "evidence.json", SearchOption.AllDirectories)))!;
    private static long Size(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    private static string Digest(string directory) => string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(f => f, StringComparer.Ordinal).Select(f => Path.GetRelativePath(directory, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
}
