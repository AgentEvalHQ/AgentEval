// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
using AgentEval.Output;

namespace AgentEval.Tests.MissionControl;

/// <summary>Real converter-backed resolver checks: imports never establish original execution custody.</summary>
public sealed class ComplianceEvidenceAuditChainResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-compliance-resolver-");
    private const string Timestamp = "2026-05-13_08-00-00";
    private const string Regulation = "test-reg";
    public void Dispose() => _root.Delete(true);

    private async Task<FileSystemOutputStore> Seed(bool sourceExists, bool mismatch = false, string? originalRunId = null)
    {
        var store = new FileSystemOutputStore(_root.FullName);
        await store.InitializeSolutionAsync("compliance-resolver");
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var hash = new string('a', 64);
        if (sourceExists)
        {
            var run = await store.StartRunAsync(subject, new("tests", ".", "offline", null, null, "eval"));
            await store.CompleteRunAsync(run, new("1.0", run.Run.RunId, "PASS", new(0, 0, 0, 0), new Dictionary<string, double>()));
            var manifest = (await store.GetRunManifestAsync(run.Run.RunId))!;
            originalRunId = manifest.Run.RunId;
            hash = manifest.ContentHash;
        }
        var report = new ComplianceEvidence("1.0", Regulation, subject,
            DateTimeOffset.Parse("2026-05-13T08:00:00Z"), new(originalRunId ?? "missing-source", mismatch ? new string('0', 64) : hash),
            [new("C1", "Control", "pass", null, [], null)], new(1, 1, 0, 0, "pass"), new("1.0", null, "code", "none"));
        var directory = Path.Combine(_root.FullName, "compliance", Regulation, subject.Name, Timestamp);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "evidence.json"), JsonSerializer.SerializeToUtf8Bytes(report,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }));
        return store;
    }
    private static Task<ComplianceEvidenceWithChain?> Read(FileSystemOutputStore store) => new Query().ComplianceEvidence(
        new ComplianceMatrixService(store), Regulation, SubjectKind.Agent, "TestAgent", Timestamp);

    [Fact]
    public async Task ComplianceEvidence_MatchingLegacyHash_RetainsReportWithImportCustodyWarning()
    {
        var result = Assert.IsType<ComplianceEvidenceWithChain>(await Read(await Seed(true)));
        Assert.False(result.ChainValid);
        Assert.Equal("imported-not-sealed-when-run", result.ChainBreakReason);
        Assert.Null(Assert.Single(result.Evidence.Controls).PassRate);
    }

    [Fact]
    public async Task ComplianceEvidence_SourceRunMissing_RetainsOrphanWithWarning()
    {
        var result = Assert.IsType<ComplianceEvidenceWithChain>(await Read(await Seed(false)));
        Assert.False(result.ChainValid);
        Assert.Equal("source-run-not-found", result.ChainBreakReason);
        Assert.Null(result.ContainerRunId);
        Assert.Null(result.SourceId);
        Assert.Equal(Regulation, result.Evidence.Regulation);
    }

    [Fact]
    public async Task ComplianceEvidence_HashMismatch_RefusesConflictingEvidence()
    {
        var store = await Seed(true, mismatch: true);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Read(store));
        Assert.Contains("source hash", error.Message);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\Windows\\System32\\config\\sam")]
    [InlineData("a/b/c")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ComplianceEvidence_UnsafeRecordedRunReference_IsNeverUsedAsAPath(string reference)
    {
        // Only the bounded compliance directory is opened; SourceRun remains an untrusted recorded reference.
        var result = Assert.IsType<ComplianceEvidenceWithChain>(await Read(await Seed(false, originalRunId: reference)));
        Assert.False(result.ChainValid);
        Assert.Equal("source-run-not-found", result.ChainBreakReason);
        Assert.Null(result.ContainerRunId);
        Assert.Null(result.SourceId);
        Assert.Equal(reference, result.Evidence.SourceRun.RunId);
    }
}
#endif
