// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Locks down the MC1.1 contract: <see cref="IOutputStoreReader"/> is the read-only
/// subset of <see cref="IOutputStore"/>; <see cref="RunPointer"/> v1.7 fields are
/// additive and round-trip null when absent. Plan-08 MC1.1.1-3.
/// </summary>
public class IOutputStoreReaderContractTests
{
    [Fact]
    public void IOutputStore_Inherits_IOutputStoreReader()
    {
        Assert.True(typeof(IOutputStoreReader).IsAssignableFrom(typeof(IOutputStore)),
            "IOutputStore must inherit from IOutputStoreReader so existing read calls keep compiling.");
    }

    [Fact]
    public void IOutputStoreReader_Has_NoWriteMethods()
    {
        // Sanity check: the reader interface must not declare any of the write-side methods
        // that live on IOutputStore. Catching a stray write declaration here prevents the
        // portal from accidentally getting write access via the reader interface.
        var readerMethodNames = typeof(IOutputStoreReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        string[] writeOnlyMethods =
        [
            nameof(IOutputStore.EnsureSubjectAsync),
            nameof(IOutputStore.StartRunAsync),
            nameof(IOutputStore.WriteScenarioResultAsync),
            nameof(IOutputStore.CompleteRunAsync),
            nameof(IOutputStore.AppendTraceAsync),
            nameof(IOutputStore.SaveBaselineAsync),
            nameof(IOutputStore.AppendHistoryEntryAsync),
            nameof(IOutputStore.SaveComplianceEvidenceAsync),
            nameof(IOutputStore.StartRedTeamCampaignAsync),
            nameof(IOutputStore.CompleteRedTeamCampaignAsync),
        ];

        foreach (var write in writeOnlyMethods)
        {
            Assert.DoesNotContain(write, readerMethodNames);
        }
    }

    [Fact]
    public void IOutputStoreReader_HasExpectedReadMethods()
    {
        // The 12 read members (10 methods + 2 properties) listed in plan-07 §4 must all
        // be declared on IOutputStoreReader directly (not inherited).
        var readerMembers = typeof(IOutputStoreReader)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        string[] expected =
        [
            nameof(IOutputStoreReader.EnsureSolutionAsync),
            nameof(IOutputStoreReader.ListSubjectsAsync),
            nameof(IOutputStoreReader.GetRunManifestAsync),
            nameof(IOutputStoreReader.GetRunSummaryAsync),
            nameof(IOutputStoreReader.GetScenarioResultsAsync),
            nameof(IOutputStoreReader.GetTraceAsync),
            nameof(IOutputStoreReader.ListRunsAsync),
            nameof(IOutputStoreReader.GetRecentRunsAsync),
            nameof(IOutputStoreReader.LoadBaselineAsync),
            nameof(IOutputStoreReader.CompareToBaselineAsync),
            nameof(IOutputStoreReader.GetHistoryAsync),
            nameof(IOutputStoreReader.ListComplianceEvidenceAsync),
            nameof(IOutputStoreReader.GetComplianceEvidenceAsync),
            nameof(IOutputStoreReader.IsAvailable),
            nameof(IOutputStoreReader.WorkspaceRoot),
        ];

        foreach (var name in expected)
        {
            Assert.Contains(name, readerMembers);
        }
    }

    [Fact]
    public void RunPointer_LegacyConstruction_StillCompiles()
    {
        // Pre-v1.7 callers used the 4-arg positional ctor. Must keep working.
        var pointer = new RunPointer(
            RunId: "run-1",
            SubjectName: "TravelAgent",
            Verdict: "PASS",
            Timestamp: DateTimeOffset.UtcNow);

        Assert.Equal("run-1", pointer.RunId);
        Assert.Null(pointer.Kind);
        Assert.Null(pointer.Score);
        Assert.Null(pointer.DurationMs);
        Assert.Null(pointer.EstimatedCost);
    }

    [Fact]
    public void RunPointer_FullConstruction_RoundTripsThroughJson()
    {
        var ts = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var original = new RunPointer(
            RunId: "run-1",
            SubjectName: "TravelAgent",
            Verdict: "PASS",
            Timestamp: ts,
            Kind: SubjectKind.Agent,
            Score: 0.87,
            DurationMs: 4200,
            EstimatedCost: 0.0124);

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var json = JsonSerializer.Serialize(original, opts);
        var roundTripped = JsonSerializer.Deserialize<RunPointer>(json, opts);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.RunId, roundTripped!.RunId);
        Assert.Equal(SubjectKind.Agent, roundTripped.Kind);
        Assert.Equal(0.87, roundTripped.Score);
        Assert.Equal(4200L, roundTripped.DurationMs);
        Assert.Equal(0.0124, roundTripped.EstimatedCost);
    }

    [Fact]
    public void RunPointer_LegacyJson_RoundTripsWithNullExtensionFields()
    {
        // Legacy history.jsonl entries (pre-v1.7) have only the 4 original fields. Verify
        // they deserialise cleanly with the v1.7 nullable extensions defaulting to null.
        const string legacyJson =
            """{"runId":"run-old","subjectName":"TravelAgent","verdict":"FAIL","timestamp":"2026-05-09T12:00:00+00:00"}""";

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var pointer = JsonSerializer.Deserialize<RunPointer>(legacyJson, opts);

        Assert.NotNull(pointer);
        Assert.Equal("run-old", pointer!.RunId);
        Assert.Equal("FAIL", pointer.Verdict);
        Assert.Null(pointer.Kind);
        Assert.Null(pointer.Score);
        Assert.Null(pointer.DurationMs);
        Assert.Null(pointer.EstimatedCost);
    }
}
