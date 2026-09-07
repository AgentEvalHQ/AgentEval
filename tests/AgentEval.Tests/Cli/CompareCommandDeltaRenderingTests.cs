// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Wave 10 / d-5 — `agenteval compare` rendered a NON-ZERO delta as `0.0000`.
//
// MEASUREMENT_STATUS §72.11 found it while refuting §71.5: two `bench perf latency` runs scored
// 0.9958026933333333 and 0.9958143866666666, `--json` carried delta 1.1693333333284706e-05, and
// the human report printed
//
//     perf-latency                                0.9958     0.9958    0.0000
//     mean score delta: 0.0000  ·  recovered 0  ·  regressed 0
//
// A measured difference shown as an exact zero, and "the two runs are identical" is the reading a
// reader takes. `F4` cannot separate that from "they differ below the fourth decimal".
//
// ⚠ THE FIX MUST NOT CREATE THE OPPOSITE CONFUSION. This record has four sightings of an ABSENCE
// and a ZERO rendering the same; making every zero look non-zero would be the same defect wearing
// the other coat. Three states, three renderings, and the tests below pin all three:
//
//     absent (no scenarios to average, MeanScoreDelta is NaN) -> "n/a"
//     an exact zero (the runs scored identically)             -> "0.0000"
//     non-zero but below F4's precision                       -> scientific, e.g. "1.17e-05"

using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.Tests.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

[Collection("ConsoleTests")]
public class CompareCommandDeltaRenderingTests
{
    // ── The formatter, directly ──────────────────────────────────────────────

    [Fact] // The defect: a real difference must never be shown as an exact zero.
    public void ADeltaBelowFourDecimalPlaces_IsNeverRenderedAsZero()
    {
        // The number §72.11 measured, and two smaller ones.
        foreach (double d in new[] { 1.1693333333284706e-05, 4.9e-05, 1e-09, -1.1693333333284706e-05, -1e-12 })
        {
            string rendered = CompareCommand.FormatDelta(d);
            Assert.NotEqual("0.0000", rendered);
            Assert.NotEqual("-0.0000", rendered);
        }
    }

    [Fact] // The other direction. A genuine zero is still a zero, and must look like one.
    public void AnExactlyZeroDelta_IsRenderedAsZero()
    {
        Assert.Equal("0.0000", CompareCommand.FormatDelta(0.0));
        Assert.Equal("0.0000", CompareCommand.FormatDelta(-0.0));
        Assert.Equal("0.0000", CompareCommand.FormatDelta(0.5 - 0.5));
    }

    [Fact] // ABSENCE is not zero. MeanScoreDelta is NaN when there is nothing to average.
    public void AnAbsentDelta_IsRenderedAsNeitherZeroNorANumber()
    {
        string rendered = CompareCommand.FormatDelta(double.NaN);

        Assert.Equal("n/a", rendered);
        Assert.NotEqual("0.0000", rendered);
        Assert.NotEqual(CompareCommand.FormatDelta(0.0), rendered);
    }

    [Fact] // Everything F4 could already show, it still shows the same way.
    public void ADeltaLargeEnoughForFourDecimalPlaces_StillRendersAsBefore()
    {
        Assert.Equal("0.5000", CompareCommand.FormatDelta(0.5));
        Assert.Equal("-0.5000", CompareCommand.FormatDelta(-0.5));
        Assert.Equal("-0.0893", CompareCommand.FormatDelta(-0.0893));
        Assert.Equal("0.0001", CompareCommand.FormatDelta(0.0001));
    }

    [Fact] // The rendering must still say WHICH WAY, and by how much, not merely "not zero".
    public void ASubPrecisionDelta_KeepsItsSignAndItsMagnitude()
    {
        Assert.StartsWith("-", CompareCommand.FormatDelta(-1.1693333333284706e-05));
        Assert.DoesNotContain("-", CompareCommand.FormatDelta(1.1693333333284706e-05)[..1]);
        Assert.Contains("e-05", CompareCommand.FormatDelta(1.1693333333284706e-05));
        Assert.Contains("e-09", CompareCommand.FormatDelta(1e-09));
    }

    // ── End to end, over files the real store wrote ──────────────────────────

    private static async Task<string> WriteRunAsync(TempWorkspace temp, string subjectName, double score)
    {
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, subjectName);
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(
            subject, new RunContext("Evals", ".", "TestHarness", null, null, "eval"));

        var facts = new ComparabilityFacts("eval.k", "1.0.0")
        {
            EffectiveBar = 0.7,
            Judge = new JudgeFingerprint("gpt-5.5", "sha256:rubric"),
        };

        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult(
            "perf-latency", "perf-latency", "in", "out", true, score,
            new Dictionary<string, double>(), [], TimeSpan.Zero, 0.0)
        {
            StimulusHash = "sha256:aaa",
            Comparability = facts,
        });

        string dir = Directory
            .GetDirectories(temp.Path, "*", SearchOption.AllDirectories)
            .Single(d => Path.GetFileName(d) == manifest.Run.RunId);

        Assert.Single(Directory.GetFiles(Path.Combine(dir, "scenarios"), "*.json"));
        return dir;
    }

    private static (string Output, int ExitCode) CaptureCompare(string baseline, string candidate)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            int exit = CompareCommand.Run(baseline, candidate);
            return (sw.ToString(), exit);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact] // §72.11's pair, reproduced through the shipped store and the shipped command.
    public async Task TheReportOfAPairThatDiffersBelowFourDecimals_DoesNotSayZero()
    {
        using var temp = TempWorkspace.Create("CompareTinyDelta");
        string a = await WriteRunAsync(temp, "PerfA", 0.9958026933333333);
        string b = await WriteRunAsync(temp, "PerfC", 0.9958143866666666);

        var (output, exit) = CaptureCompare(a, b);

        Assert.Equal(0, exit);

        // Fixture guard: if the two scores had collapsed to the same double the delta WOULD be
        // zero and the assertions below would be checking the wrong thing.
        Assert.NotEqual(0.9958026933333333, 0.9958143866666666);

        string deltaRow = Assert.Single(output.Split('\n').Where(l => l.Contains("perf-latency", StringComparison.Ordinal)));
        string meanRow = Assert.Single(output.Split('\n').Where(l => l.Contains("mean score delta", StringComparison.Ordinal)));

        Assert.DoesNotContain("0.0000", deltaRow.Replace("0.9958", "", StringComparison.Ordinal));
        Assert.DoesNotContain("mean score delta: 0.0000", meanRow);
        Assert.Contains("e-05", deltaRow);
        Assert.Contains("e-05", meanRow);
    }

    [Fact] // The control fails the OTHER way too: identical runs must still print a plain zero.
    public async Task TheReportOfAPairThatIsGenuinelyIdentical_StillSaysZero()
    {
        using var temp = TempWorkspace.Create("CompareTrueZero");
        string a = await WriteRunAsync(temp, "PerfA", 0.9958026933333333);
        string b = await WriteRunAsync(temp, "PerfB", 0.9958026933333333);

        var (output, exit) = CaptureCompare(a, b);

        Assert.Equal(0, exit);

        string deltaRow = Assert.Single(output.Split('\n').Where(l => l.Contains("perf-latency", StringComparison.Ordinal)));
        string meanRow = Assert.Single(output.Split('\n').Where(l => l.Contains("mean score delta", StringComparison.Ordinal)));

        Assert.Contains("0.0000", deltaRow);
        Assert.Contains("mean score delta: 0.0000", meanRow);
        Assert.DoesNotContain("e-", deltaRow);
        Assert.DoesNotContain("e-", meanRow);
    }

    // ── The legend must count what it is pointing at ─────────────────────────
    //
    // Wave 11. The legend is triggered by the scenario rows OR by the mean, and it tallied only
    // the rows. Two scenarios differing by +1.0e-04 and -9.0e-05 print as 0.0001 and -0.0001 —
    // neither is sub-precision — while their mean, 5.0e-06, is. The report then said
    //
    //     ⚠ 0 delta(s) are NON-ZERO but smaller than four decimal places, and are
    //       shown in scientific notation.
    //     mean score delta: 5.00e-06
    //
    // "0 of them" printed directly above the one thing the legend exists to explain. Reproduced on
    // files the real store wrote before the wording was changed.

    private static async Task<string> WriteMultiScenarioRunAsync(
        TempWorkspace temp, string subjectName, params (string Id, double Score)[] scenarios)
    {
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, subjectName);
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(
            subject, new RunContext("Evals", ".", "TestHarness", null, null, "eval"));

        var facts = new ComparabilityFacts("eval.k", "1.0.0")
        {
            EffectiveBar = 0.7,
            Judge = new JudgeFingerprint("gpt-5.5", "sha256:rubric"),
        };

        foreach (var (id, score) in scenarios)
        {
            await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult(
                id, id, "in", "out", true, score,
                new Dictionary<string, double>(), [], TimeSpan.Zero, 0.0)
            {
                StimulusHash = "sha256:aaa",
                Comparability = facts,
            });
        }

        string dir = Directory
            .GetDirectories(temp.Path, "*", SearchOption.AllDirectories)
            .Single(d => Path.GetFileName(d) == manifest.Run.RunId);

        Assert.Equal(scenarios.Length, Directory.GetFiles(Path.Combine(dir, "scenarios"), "*.json").Length);
        return dir;
    }

    private static string LegendLine(string output) =>
        Assert.Single(output.Split('\n').Where(l => l.Contains("smaller than four decimal places", StringComparison.Ordinal)));

    [Fact] // The defect: the legend fired on the MEAN and then counted only the rows.
    public async Task TheLegend_CountsTheMean_WhenOnlyTheMeanIsBelowPrecision()
    {
        using var temp = TempWorkspace.Create("CompareLegendMeanOnly");
        string a = await WriteMultiScenarioRunAsync(temp, "LegBase", ("s1", 0.5), ("s2", 0.5));
        string b = await WriteMultiScenarioRunAsync(temp, "LegCand", ("s1", 0.5001), ("s2", 0.49991));

        var (output, exit) = CaptureCompare(a, b);
        Assert.Equal(0, exit);

        // Fixture guard: the whole point is that NEITHER row is sub-precision while the mean is.
        Assert.Equal("0.0001", CompareCommand.FormatDelta(0.5001 - 0.5));
        Assert.Equal("-0.0001", CompareCommand.FormatDelta(0.49991 - 0.5));
        Assert.Contains("e-06", CompareCommand.FormatDelta(((0.5001 - 0.5) + (0.49991 - 0.5)) / 2));

        string legend = LegendLine(output);
        string meanRow = Assert.Single(output.Split('\n').Where(l => l.Contains("mean score delta", StringComparison.Ordinal)));

        Assert.Contains("e-06", meanRow);
        Assert.Contains("The mean below is", legend);
        Assert.DoesNotContain("0 delta", legend);
    }

    [Fact] // The other side: a sub-precision ROW with a mean large enough to print, counted as a row.
    public async Task TheLegend_NamesTheRowsAlone_WhenTheMeanIsLargeEnoughToPrint()
    {
        using var temp = TempWorkspace.Create("CompareLegendRowOnly");
        string a = await WriteMultiScenarioRunAsync(temp, "LegBase2", ("s1", 0.5), ("s2", 0.2));
        string b = await WriteMultiScenarioRunAsync(temp, "LegCand2", ("s1", 0.50001), ("s2", 0.7));

        var (output, exit) = CaptureCompare(a, b);
        Assert.Equal(0, exit);

        string legend = LegendLine(output);

        Assert.Contains("1 delta above is", legend);
        Assert.DoesNotContain("mean below", legend);
        Assert.DoesNotContain("0 delta", legend);
    }

    [Fact] // Nothing sub-precision at all: no legend, so nothing can be miscounted.
    public async Task TheLegend_IsAbsent_WhenNothingIsBelowPrecision()
    {
        using var temp = TempWorkspace.Create("CompareLegendNone");
        string a = await WriteMultiScenarioRunAsync(temp, "LegBase3", ("s1", 0.5), ("s2", 0.2));
        string b = await WriteMultiScenarioRunAsync(temp, "LegCand3", ("s1", 0.6), ("s2", 0.7));

        var (output, exit) = CaptureCompare(a, b);
        Assert.Equal(0, exit);

        Assert.DoesNotContain("smaller than four decimal places", output);
    }
}
