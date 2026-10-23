// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.StoreV1;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval compare</c> — ADR-031 <b>S5</b> / plan Phase 7.5. Reads two run
/// directories and either emits deltas or REFUSES with <see cref="ExitCodes.Incomparable"/> (13).
/// </summary>
/// <remarks>
/// <para>
/// Reads verified AEF evidence from two directories. Legacy store results pass through the shared
/// StoreV1 converter into invocation-owned temporary AEF folders. Imported custody and missing
/// comparison facts remain explicit; no network calls or original-source writes are needed.
/// </para>
/// <para>
/// <b>The decision lives in the library</b> (<see cref="RunComparison"/>), not here, so it can be
/// tested without a filesystem. This command does I/O and rendering, and turns the verdict into an
/// exit code.
/// </para>
/// <para>
/// ⚠ <b>Exit 13 is a CI-reader contract addition</b> — see <see cref="ExitCodes.Incomparable"/>.
/// </para>
/// </remarks>
public static class CompareCommand
{
    // Comparison output includes explicit nulls so an absent baseline, candidate or delta remains visible.
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Builds the command.</summary>
    /// <returns>The <c>compare</c> command.</returns>
    public static Command Create()
    {
        var baselineOpt = new Option<string>("--baseline")
        {
            Description = "Path to the BASELINE AEF run directory or legacy store result folder. REQUIRED.",
            Required = true,
        };
        var candidateOpt = new Option<string>("--candidate")
        {
            Description = "Path to the CANDIDATE AEF run directory or legacy store result folder. REQUIRED.",
            Required = true,
        };
        var strictOpt = new Option<bool>("--strict")
        {
            Description =
                "Also refuse when an axis was recorded by NEITHER run. Without it, an axis neither run "
              + "pinned is counted and printed as a blind spot rather than treated as agreement — it is "
              + "never read as a match.",
        };
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Emit the comparison as JSON on stdout instead of a human-readable report.",
        };
        var failOnRegressionOpt = new Option<bool>("--fail-on-regression")
        {
            Description =
                $"Exit {ExitCodes.TestFailure} when the runs are comparable and a scenario the baseline passed fails in the "
              + $"candidate, so a CI step fails on a regression. Without it a comparable result exits 0 whatever it "
              + $"shows. Incomparable runs still exit {ExitCodes.Incomparable}.",
        };

        var cmd = new Command("compare",
            "Compare two run directories. Emits deltas only when the two runs can be SHOWN comparable; "
          + $"otherwise refuses with exit {ExitCodes.Incomparable} and prints what blocked it (ADR-031 S5).");

        cmd.Add(baselineOpt);
        cmd.Add(candidateOpt);
        cmd.Add(strictOpt);
        cmd.Add(jsonOpt);
        cmd.Add(failOnRegressionOpt);

        cmd.SetAction((ParseResult parse, CancellationToken ct) => Task.FromResult(Run(
            parse.GetValue(baselineOpt)!,
            parse.GetValue(candidateOpt)!,
            parse.GetValue(strictOpt),
            parse.GetValue(jsonOpt),
            parse.GetValue(failOnRegressionOpt))));

        return cmd;
    }

    /// <summary>Runs the comparison.</summary>
    /// <param name="baselinePath">Baseline run directory.</param>
    /// <param name="candidatePath">Candidate run directory.</param>
    /// <param name="strict">Treat unpinned axes as blocking.</param>
    /// <param name="asJson">Emit JSON instead of a report.</param>
    /// <param name="failOnRegression">Return <see cref="ExitCodes.TestFailure"/> when a comparable result regressed.</param>
    /// <returns>
    /// 0 when comparable, <see cref="ExitCodes.TestFailure"/> when comparable with a regression and
    /// <paramref name="failOnRegression"/> is set, <see cref="ExitCodes.Incomparable"/> when refused,
    /// <see cref="ExitCodes.UsageError"/> when a path is unusable.
    /// </returns>
    public static int Run(
        string baselinePath, string candidatePath, bool strict = false, bool asJson = false, bool failOnRegression = false)
    {
        if (!TryLoad(baselinePath, "--baseline", out var baseline)) return ExitCodes.UsageError;
        if (!TryLoad(candidatePath, "--candidate", out var candidate)) return ExitCodes.UsageError;

        RunComparison comparison;
        try
        {
            comparison = baseline.CompareTo(candidate, strict);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"✖ {ex.Message}");
            return ExitCodes.UsageError;
        }

        bool comparable = comparison.Verdict is ComparisonVerdict.Comparable;

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                verdict = comparison.Verdict.ToString(),
                strict,
                inputs = new { baseline = Provenance(baseline), candidate = Provenance(candidate) },
                matched = comparison.Scenarios.Count,
                baselineOnly = comparison.BaselineOnly,
                candidateOnly = comparison.CandidateOnly,
                refusalReasons = comparison.RefusalReasons,
                unpinnedAxes = comparison.WithUnpinnedAxes
                    .SelectMany(s => s.Unpinned.Select(a => new { scenario = s.ScenarioId, axis = a.Name }))
                    .ToList(),
                scenariosWithoutAFloor = comparison.ScenariosWithoutAFloor.Count,
                judgeGradedItsOwnSubject = comparison.ScenariosWhereTheJudgeGradedItsOwnSubject.Count,
                recovered = comparable ? comparison.Recovered : (int?)null,
                regressed = comparable ? comparison.Regressed : (int?)null,
                regressedScenarios = comparable ? RegressedIds(comparison) : null,
                // A scenario either run did not measure has no delta: its 0 is a placeholder.
                notMeasuredScenarios = comparable ? comparison.NotMeasuredScenarios : null,
                deltas = comparable
                    ? comparison.Scenarios.Select(s => new
                    {
                        scenario = s.ScenarioId,
                        baseline = s.BaselineScore,
                        candidate = s.CandidateScore,
                        delta = s.BothMeasured ? s.ScoreDelta : (double?)null,
                    }).ToList()
                    : null,
            }, s_json));

            return ExitCode(comparison, failOnRegression);
        }

        WriteProvenance("baseline", baseline);
        WriteProvenance("candidate", candidate);
        Render(comparison, baselinePath, candidatePath, strict, failOnRegression);
        return ExitCode(comparison, failOnRegression);
    }

    // One decision for both outputs, so --json and the report cannot disagree on the exit code.
    private static int ExitCode(RunComparison comparison, bool failOnRegression) =>
        comparison.Verdict is not ComparisonVerdict.Comparable ? ExitCodes.Incomparable
        : failOnRegression && comparison.Regressed > 0 ? ExitCodes.TestFailure
        : 0;

    private static List<string> RegressedIds(RunComparison comparison) =>
        comparison.Scenarios
            .Where(s => s is { BaselinePassed: true, CandidatePassed: false })
            .Select(s => s.ScenarioId)
            .ToList();

    private static void Render(
        RunComparison comparison, string baselinePath, string candidatePath, bool strict, bool failOnRegression)
    {
        Console.WriteLine();
        Console.WriteLine("agenteval compare — ADR-031 S5");
        Console.WriteLine($"  baseline : {baselinePath}");
        Console.WriteLine($"  candidate: {candidatePath}");
        Console.WriteLine($"  mode     : {(strict ? "STRICT (an axis neither run pinned blocks the comparison)" : "default (unpinned axes are declared, not gated)")}");
        Console.WriteLine($"  matched  : {comparison.Scenarios.Count} scenario(s) present in both runs");
        Console.WriteLine();

        if (comparison.Verdict is ComparisonVerdict.Incomparable)
        {
            Console.WriteLine($"❌ INCOMPARABLE — NO DELTA IS EMITTED (exit {ExitCodes.Incomparable}).");
            Console.WriteLine("   S5 refuses rather than warning: a delta that should not have been printed");
            Console.WriteLine("   outlives the warning printed beside it.");
            Console.WriteLine();

            foreach (string reason in comparison.RefusalReasons.Take(40))
                Console.WriteLine($"   • {reason}");

            if (comparison.RefusalReasons.Count > 40)
                Console.WriteLine($"   • … and {comparison.RefusalReasons.Count - 40} more");

            Console.WriteLine();
            return;
        }

        Console.WriteLine("✅ COMPARABLE — every gating axis matched on every shared scenario.");
        Console.WriteLine();

        // The blind spots come BEFORE the numbers, on purpose: printed after a delta they read as
        // a footnote to a result the reader has already taken.
        WriteBlindSpots(comparison);

        Console.WriteLine("  scenario                                   baseline  candidate     delta");
        Console.WriteLine("  ------------------------------------------------------------------------");
        foreach (var s in comparison.Scenarios)
        {
            // A side that did not measure the scenario has a placeholder score: no number, no delta.
            string baselineCell = s.BaselineScore?.ToString("F4", CultureInfo.InvariantCulture) ?? "n/m";
            string candidateCell = s.CandidateScore?.ToString("F4", CultureInfo.InvariantCulture) ?? "n/m";
            string deltaCell = s.BothMeasured ? FormatDelta(s.ScoreDelta) : "n/a";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Fit(s.ScenarioId, 40),-40}  {baselineCell,8}  {candidateCell,9}  {deltaCell,9}"));
        }

        int subPrecision = comparison.Scenarios.Count(s => s.BothMeasured && IsBelowDisplayPrecision(s.ScoreDelta));
        bool meanIsSubPrecision = IsBelowDisplayPrecision(comparison.MeanScoreDelta);
        if (subPrecision > 0 || meanIsSubPrecision)
        {
            // ⚠ The legend must count what it is pointing at. It is triggered by the scenario rows
            // OR by the mean, and a count that only ever tallies the rows printed
            // "0 delta(s) are NON-ZERO but smaller than four decimal places" directly above a mean
            // rendered in scientific notation — telling the reader there is nothing to see beside
            // the one thing this legend exists to explain. Reproduced on two scenarios differing by
            // +1.0e-04 and -9.0e-05: neither row is sub-precision, their mean is.
            string subject = (subPrecision, meanIsSubPrecision) switch
            {
                (0, true)  => "The mean below is",
                (1, false) => "1 delta above is",
                (_, false) => $"{subPrecision} deltas above are",
                (1, true)  => "1 delta above, and the mean below, are",
                _          => $"{subPrecision} deltas above, and the mean below, are",
            };

            Console.WriteLine();
            Console.WriteLine($"  ⚠ {subject} NON-ZERO but smaller than four decimal places, and shown");
            Console.WriteLine("    in scientific notation. Smaller than the column can print is not the same");
            Console.WriteLine("    thing as zero; --json carries the full value.");
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  mean score delta: {FormatDelta(comparison.MeanScoreDelta)}  ·  recovered {comparison.Recovered}  ·  regressed {comparison.Regressed}"));
        if (comparison.NotMeasuredScenarios.Count > 0)
        {
            // n/m: not measured (errored, skipped, or no judge verdict) on that side.
            Console.WriteLine($"  ⚠ {comparison.NotMeasuredScenarios.Count} scenario(s) were not measured in one run or both " +
                              "(errored, skipped, or no judge verdict; n/m above) and are not in the mean:");
            foreach (string id in comparison.NotMeasuredScenarios.Take(40))
                Console.WriteLine($"   • {id}");
            if (comparison.NotMeasuredScenarios.Count > 40)
                Console.WriteLine($"   • … and {comparison.NotMeasuredScenarios.Count - 40} more");
        }
        Console.WriteLine();

        if (failOnRegression && comparison.Regressed > 0)
        {
            Console.WriteLine($"❌ REGRESSED — passed in the baseline, did not pass in the candidate (failed, errored or not run) (exit {ExitCodes.TestFailure}, --fail-on-regression):");
            foreach (string id in RegressedIds(comparison).Take(40))
                Console.WriteLine($"   • {id}");
            if (comparison.Regressed > 40)
                Console.WriteLine($"   • … and {comparison.Regressed - 40} more");
            Console.WriteLine();
        }
    }

    private static void WriteBlindSpots(RunComparison comparison)
    {
        var unpinned = comparison.WithUnpinnedAxes
            .SelectMany(s => s.Unpinned.Select(a => a.Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (unpinned.Count > 0)
        {
            Console.WriteLine($"  ⚠ {unpinned.Count} axis/axes were recorded by NEITHER run and are therefore NOT verified:");
            foreach (string axis in unpinned)
            {
                var example = comparison.WithUnpinnedAxes
                    .SelectMany(s => s.Unpinned)
                    .First(a => string.Equals(a.Name, axis, StringComparison.Ordinal));
                Console.WriteLine($"      {axis} — {example.Detail}");
            }

            Console.WriteLine("    These are blind spots, not agreements. Re-run with --strict to refuse on them.");
            Console.WriteLine();
        }

        if (comparison.ScenariosWithoutAFloor.Count > 0)
        {
            Console.WriteLine($"  ⚠ {comparison.ScenariosWithoutAFloor.Count} of {comparison.Scenarios.Count} matched scenario(s) recorded NO usable chance floor.");
            Console.WriteLine("    Their deltas cannot be read against chance: nothing here says what an arm that");
            Console.WriteLine("    understood nothing would have scored. The floor is reported and never gated —");
            Console.WriteLine("    it decides whether a delta MEANS anything, not whether two runs are comparable.");
            Console.WriteLine();
        }

        if (comparison.ScenariosWhereTheJudgeGradedItsOwnSubject.Count > 0)
        {
            Console.WriteLine($"  🔴 {comparison.ScenariosWhereTheJudgeGradedItsOwnSubject.Count} matched scenario(s) were graded by the SUBJECT'S OWN MODEL.");
            Console.WriteLine("    The thing being measured supplied the measurement. Read every delta below with that.");
            Console.WriteLine();
        }
    }

    private static object Provenance(AefComparisonRun run) => new
    {
        integrity = run.Integrity, runHash = run.RunHash, status = run.Status, imported = run.Imported,
        asserted = run.Asserted, abortReason = run.AbortReason,
        custody = run.Imported ? "imported: not sealed when it ran" : run.Integrity == "unsealed" ? "unsealed: integrity not established" : "intact; signature not checked",
    };

    private static void WriteProvenance(string side, AefComparisonRun run)
    {
        Console.WriteLine($"  {side}: {run.Integrity}; status {run.Status}; " +
            (run.Imported ? "imported: not sealed when it ran" : run.Integrity == "unsealed" ? "integrity not established" : "intact; signature not checked"));
        if (run.Asserted.Count > 0) Console.WriteLine("    Importer-supplied fields: " + string.Join(", ", run.Asserted));
        if (run.AbortReason is not null) Console.WriteLine("    " + run.AbortReason);
    }

    private static bool TryLoad(string path, string option, out AefComparisonRun results)
    {
        results = null!;
        DirectoryInfo? converted = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{option} is required.");
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"No such directory: {full}");
            // Preserve tab-completion of the scenarios directory, including recognition of an AEF parent.
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(full)) == "scenarios")
                full = Directory.GetParent(Path.TrimEndingDirectorySeparator(full))!.FullName;
            // AEF markers always take precedence. A damaged AEF run must never fall back to legacy files.
            var isAef = new[] { "run.json", "results.ndjson", "seal.json", "metrics.json" }
                .Any(name => Path.Exists(Path.Combine(full, name)));
            if (!isAef)
            {
                converted = Directory.CreateTempSubdirectory("ae-compare-import-");
                var destination = Path.Combine(converted.FullName, "run");
                // Bind to the chosen directory. Run IDs can be duplicated in copied workspace trees.
                StoreV1Exporter.ExportDirectory(full, destination);
                full = destination;
            }
            results = new AefComparisonReader().Read(full);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or JsonException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"✖ {option}: {error.Message}");
            return false;
        }
        finally
        {
            // Only this invocation's newly reserved temporary directory is removed.
            if (converted is not null) Directory.Delete(converted.FullName, recursive: true);
        }
    }

    /// <summary>
    /// Renders one score delta for the human report. The JSON form carries the raw <see cref="double"/>
    /// and is unaffected by anything here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This exists because <c>F4</c> alone printed a MEASURED DIFFERENCE as an exact zero.</b>
    /// MEASUREMENT_STATUS §72.11: two runs scoring 0.9958026933333333 and 0.9958143866666666 differ
    /// by 1.1693333333284706e-05, the <c>--json</c> output said so, and the report line read
    /// <c>0.0000</c> — which a reader takes as "the two runs are identical", the one thing it does
    /// not mean.
    /// </para>
    /// <para>
    /// <b>Three states, three renderings, and the third is the whole point.</b> An ABSENCE and a
    /// ZERO must not look alike (this record has four sightings of that confusion), and neither must
    /// a zero and a number too small to show:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>absent</b> — <c>MeanScoreDelta</c> is null when there is nothing to average → <c>n/a</c>.
    ///   Live in the report since T13-5: a comparable run whose every shared scenario was not measured on one side
    ///   has no measured pair to average. See the remark below.</item>
    ///   <item><b>exactly zero</b> — the runs scored identically → <c>0.0000</c>, unchanged.</item>
    ///   <item><b>non-zero, below four decimal places</b> → scientific, e.g. <c>1.17e-05</c>, keeping
    ///   both the sign and the magnitude. "Smaller than the display can show" is not "zero".</item>
    /// </list>
    /// <para>
    /// An empty comparison refuses before rendering a table. A comparable pair with no jointly measured scenarios
    /// can still reach the table and displays n/a for its mean; neither case invents a delta.
    /// </para>
    /// <para>
    /// The sub-precision case is decided by FORMATTING FIRST and asking whether the result reads as a
    /// zero — not by comparing against a hand-picked 5e-05 threshold, which would be a second guess
    /// about where <c>F4</c> rounds and could disagree with it at the boundary.
    /// </para>
    /// </remarks>
    /// <param name="reading">The delta to render, or null when absent.</param>
    /// <returns>The rendered delta.</returns>
    internal static string FormatDelta(double? reading)
    {
        if (reading is not { } delta) return "n/a";
        // ABSENCE. Not a small number, not a zero: there was nothing to average.
        if (double.IsNaN(delta)) return "n/a";

        // A TRUE zero, including a negative zero — which `F4` renders as "-0.0000", a minus sign in
        // front of a quantity that has no direction.
        if (delta == 0.0) return "0.0000";

        string fixedPoint = delta.ToString("F4", CultureInfo.InvariantCulture);

        // Non-zero, but the fixed-point form would claim otherwise.
        return fixedPoint is "0.0000" or "-0.0000"
            ? delta.ToString("0.00e+00", CultureInfo.InvariantCulture)
            : fixedPoint;
    }

    /// <summary>True when <paramref name="reading"/> is real but too small for the fixed-point column.</summary>
    private static bool IsBelowDisplayPrecision(double? reading) =>
        reading is { } delta && !double.IsNaN(delta) && delta != 0.0
        && delta.ToString("F4", CultureInfo.InvariantCulture) is "0.0000" or "-0.0000";

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
