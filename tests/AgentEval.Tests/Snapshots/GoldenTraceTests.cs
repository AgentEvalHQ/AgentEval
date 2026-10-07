// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Snapshots;
using Xunit;

namespace AgentEval.Tests.Snapshots;

/// <summary>
/// <see cref="GoldenTrace"/> and <see cref="GoldenTraceComparer"/>: a saved run, and what a rerun changed against it.
/// </summary>
public class GoldenTraceTests
{
    private static GoldenTraceCase Case(string name, bool passed, string? output = "ok", params GoldenToolCall[] tools) =>
        new(name, passed, passed ? 100 : 0, output, tools);

    private static GoldenTrace Trace(params GoldenTraceCase[] cases) =>
        new(GoldenTrace.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, "m", cases);

    private static GoldenToolCall Tool(string name, string? args = null) => new(name, args);

    [Fact]
    public void FromResults_KeepsToolCallsInCallOrder_WithCanonicalArguments()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        {
            Name = "book", CallId = "2", Order = 2,
            Arguments = new Dictionary<string, object?> { ["seat"] = "12A", ["flight"] = new Dictionary<string, object?> { ["to"] = "LHR", ["from"] = "JFK" } },
        });
        usage.AddCall(new ToolCallRecord { Name = "search", CallId = "1", Order = 1, Arguments = new Dictionary<string, object?> { ["q"] = "JFK-LHR" } });
        var result = new TestResult { TestName = "t", Passed = true, Score = 100, ActualOutput = "Booked.", ToolUsage = usage };

        var trace = GoldenTrace.FromResults([result], model: "gpt-4o", createdAt: DateTimeOffset.UnixEpoch);

        var calls = Assert.Single(trace.Cases).ToolCalls!;
        Assert.Equal(["search", "book"], calls.Select(c => c.Name));
        Assert.Equal("""{"flight":{"from":"JFK","to":"LHR"},"seat":"12A"}""", calls[1].Arguments);   // keys sorted at every level
        Assert.Equal("gpt-4o", trace.Model);
    }

    [Fact]
    public void FromResults_NoToolData_IsNull_NotAnEmptyList()
    {
        var trace = GoldenTrace.FromResults([new TestResult { TestName = "t", Passed = true, ToolUsage = null }]);

        Assert.Null(Assert.Single(trace.Cases).ToolCalls);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "golden-" + Guid.NewGuid().ToString("N"), "trace.json");
        try
        {
            var trace = Trace(Case("a", true, "hello", Tool("search", """{"q":"x"}""")), Case("b", false, null));

            await trace.SaveAsync(path);
            var loaded = await GoldenTrace.LoadAsync(path);

            Assert.Equal(2, loaded.Cases.Count);
            Assert.Equal(trace.Cases[0].ToolCalls, loaded.Cases[0].ToolCalls);
            Assert.Equal("hello", loaded.Cases[0].Output);
            Assert.Null(loaded.Cases[1].Output);
            Assert.Equal(GoldenTraceComparer.Compare(trace, loaded).Cases.Select(c => c.Change), [TraceChange.Unchanged, TraceChange.Unchanged]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task Load_RefusesANewerSchemaAndAFileThatIsNotATrace()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "golden-" + Guid.NewGuid().ToString("N")));
        try
        {
            var newer = Path.Combine(dir.FullName, "newer.json");
            await File.WriteAllTextAsync(newer, """{"schemaVersion":99,"createdAt":"2026-01-01T00:00:00Z","cases":[]}""");
            var garbage = Path.Combine(dir.FullName, "garbage.json");
            await File.WriteAllTextAsync(garbage, "not json");

            Assert.Contains("schema 99", (await Assert.ThrowsAsync<InvalidDataException>(() => GoldenTrace.LoadAsync(newer))).Message);
            await Assert.ThrowsAsync<InvalidDataException>(() => GoldenTrace.LoadAsync(garbage));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Compare_ClassifiesEachKindOfChange()
    {
        var golden = Trace(
            Case("regressed", true),
            Case("improved", false),
            Case("tools", true, "ok", Tool("search"), Tool("book")),
            Case("args", true, "ok", Tool("search", """{"q":"a"}""")),
            Case("output", true, "Paris."),
            Case("same", true, "ok\r\n", Tool("search")),
            Case("removed", true));
        var current = Trace(
            Case("regressed", false),
            Case("improved", true),
            Case("tools", true, "ok", Tool("book"), Tool("search")),
            Case("args", true, "ok", Tool("search", """{"q":"b"}""")),
            Case("output", true, "Lyon."),
            Case("same", true, "  ok\n", Tool("search")),   // line endings and surrounding space are not a change
            Case("added", true));

        var comparison = GoldenTraceComparer.Compare(golden, current);
        var byName = comparison.Cases.ToDictionary(c => c.Name);

        Assert.Equal(TraceChange.Regressed, byName["regressed"].Change);
        Assert.Equal(TraceChange.Improved, byName["improved"].Change);
        Assert.Equal(TraceChange.ToolsChanged, byName["tools"].Change);
        Assert.Contains("[search, book] → [book, search]", byName["tools"].Detail);
        Assert.Equal(TraceChange.ToolsChanged, byName["args"].Change);
        Assert.Contains("(arguments differ)", byName["args"].Detail);
        Assert.Equal(TraceChange.OutputChanged, byName["output"].Change);
        Assert.Equal(TraceChange.Unchanged, byName["same"].Change);
        Assert.Null(byName["same"].Detail);
        Assert.Equal(TraceChange.Added, byName["added"].Change);
        Assert.Equal(TraceChange.Removed, byName["removed"].Change);
        Assert.True(comparison.HasRegression);
        Assert.True(comparison.HasToolChange);
    }

    [Fact]
    public void Compare_AVerdictChange_StillReportsWhatElseChanged()
    {
        var comparison = GoldenTraceComparer.Compare(
            Trace(Case("t", true, "ok", Tool("search"))),
            Trace(Case("t", false, "no", Tool("refund"))));

        var c = Assert.Single(comparison.Cases);
        Assert.Equal(TraceChange.Regressed, c.Change);
        Assert.True(c.ToolsChanged);
        Assert.True(c.OutputChanged);
        Assert.Equal("passed → failed (score 100 → 0); tools [search] → [refund]; output changed", c.Detail);
    }

    [Fact]
    public void Compare_WhenEitherRunRecordedNoToolData_DoesNotCompareTools()
    {
        // No tool data is not "no tools called": a run whose adapter returned no messages must not read as dropping them.
        var comparison = GoldenTraceComparer.Compare(
            Trace(Case("t", true, "ok", Tool("search"))),
            Trace(new GoldenTraceCase("t", true, 100, "ok", ToolCalls: null)));

        Assert.Equal(TraceChange.Unchanged, Assert.Single(comparison.Cases).Change);
        Assert.False(comparison.HasToolChange);
    }

    [Fact]
    public void Compare_MatchesARepeatedNameByItsOccurrence()
    {
        var comparison = GoldenTraceComparer.Compare(
            Trace(Case("refund", true), Case("refund", true)),
            Trace(Case("refund", true), Case("refund", false)));

        Assert.Equal([TraceChange.Unchanged, TraceChange.Regressed], comparison.Cases.Select(c => c.Change));
    }
}
