// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefObservedDurationTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("observed-duration");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_out);
    private static EvalResult Node(string id, double? duration = null, params EvalResult[] children) => new(
        new(id, id, "test", "1.0"), new(0.8, null, "pass", true, .7, "none", null),
        new(null, null, null, children, "Own"), new("code", null, null, null, null, 0, false), Start)
        { ObservedDuration = duration is { } value ? TimeSpan.FromMilliseconds(value) : null };
    private static AefCompositeRunOptions Options() => new()
    { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "test:duration", Lane = "test" };

    [Fact]
    public void ObservedDuration_ZeroAndUnknown_AreDistinctButDoNotChangeLegacyJson()
    {
        var unknown = Node("case");
        var zero = unknown with { ObservedDuration = TimeSpan.Zero };
        Assert.Null(unknown.ObservedDuration);
        Assert.Equal(TimeSpan.Zero, zero.ObservedDuration);
        Assert.Equal(JsonSerializer.Serialize(unknown), JsonSerializer.Serialize(zero));
        Assert.Null(JsonSerializer.Deserialize<EvalResult>(JsonSerializer.Serialize(zero))!.ObservedDuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedDuration_NegativeInterval_RefusesInitializerAndCopy(bool copy)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => copy
            ? Node("case") with { ObservedDuration = TimeSpan.FromTicks(-1) }
            : Node("case", -1));
    }

    [Fact]
    public void Write_NestedDuplicateEvaluatorKeys_PreservesOnlyEachNodesOwnInterval()
    {
        var root = Node("preset", 999, Node("case", 42, Node("same", 11, Node("same")), Node("same", 0)));
        var directory = AefCompositeRunWriter.Write(_out, root, AefCompositeRunWriter.Undescribed,
            Options(), Start, Start.AddSeconds(1)).Directory;
        var times = AefTestRuns.Results(directory).ToDictionary(r => (string)r["path"]!, r => (double?)r["durationMs"]);
        Assert.Equal(42, times["case"]);
        Assert.Equal(11, times["case/1"]);
        Assert.Null(times["case/1/same"]);
        Assert.Equal(0, times["case/2"]);
    }

    [Fact]
    public void WriteInterruptedTrials_SyntheticRollups_DoNotBorrowTrialDuration()
    {
        var trials = new[] { Node("preset", 900, Node("case", 5, Node("leaf", 2))),
            Node("preset", 901, Node("case", 8, Node("leaf", 3))) };
        var directory = AefCompositeRunWriter.WriteAbortedTrials(_out, trials, AefCompositeRunWriter.Undescribed,
            Options() with { AbortReason = "cancelled" }, Start, Start.AddSeconds(1)).Directory;
        var rows = AefTestRuns.Results(directory);
        Assert.Equal(new double?[] { 2, 3, 5, 8 }, rows.Where(r => r["trial"] is not null).Select(r => (double?)r["durationMs"]).Order());
        var rollups = rows.Where(r => r["trial"] is null).ToArray();
        Assert.Equal(2, rollups.Length);
        Assert.All(rollups, r => Assert.Null(r["durationMs"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteTrials_FinalObservation_KeepsItsOwnIntervalOrUnknown(bool observed)
    {
        var trials = new[] { Node("preset", null, Node("case", 5)), Node("preset", null, Node("case", 8)) };
        var rollup = Node("preset", null, Node("case", observed ? 20 : null));
        var directory = AefCompositeRunWriter.WriteTrials(_out, rollup, trials, AefCompositeRunWriter.Undescribed,
            Options(), Start, Start.AddSeconds(1)).Directory;
        var final = Assert.Single(AefTestRuns.Results(directory), r => r["trial"] is null);
        Assert.Equal(observed ? 20 : (double?)null, (double?)final["durationMs"]);
    }
}
