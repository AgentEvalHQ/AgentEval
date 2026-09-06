// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval compare</c> — ADR-031 <b>S5</b> / plan Phase 7.5. Reads two run
/// directories and either emits deltas or REFUSES with <see cref="ExitCodes.Incomparable"/> (13).
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a pure function of two directories.</b> No manifest, no store, no workspace discovery,
/// no network: every fact it reads is on the scenario files themselves, which is finding V1's whole
/// claim. That is why the arguments are two paths and not a subject plus two run ids.
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
    // Matches FileSystemOutputStore's write options. A reader configured differently from the
    // writer is the "asserted against a copy of its settings" defect ADR-031 records for S2, so the
    // round trip is covered by a test that reads a file the real store wrote.
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Builds the command.</summary>
    /// <returns>The <c>compare</c> command.</returns>
    public static Command Create()
    {
        var baselineOpt = new Option<string>("--baseline")
        {
            Description = "Path to the BASELINE run directory (the one containing scenarios/). REQUIRED.",
            Required = true,
        };
        var candidateOpt = new Option<string>("--candidate")
        {
            Description = "Path to the CANDIDATE run directory (the one containing scenarios/). REQUIRED.",
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

        var cmd = new Command("compare",
            "Compare two run directories. Emits deltas only when the two runs can be SHOWN comparable; "
          + $"otherwise refuses with exit {ExitCodes.Incomparable} and prints what blocked it (ADR-031 S5).");

        cmd.Add(baselineOpt);
        cmd.Add(candidateOpt);
        cmd.Add(strictOpt);
        cmd.Add(jsonOpt);

        cmd.SetAction((ParseResult parse, CancellationToken ct) => Task.FromResult(Run(
            parse.GetValue(baselineOpt)!,
            parse.GetValue(candidateOpt)!,
            parse.GetValue(strictOpt),
            parse.GetValue(jsonOpt))));

        return cmd;
    }

    /// <summary>Runs the comparison.</summary>
    /// <param name="baselinePath">Baseline run directory.</param>
    /// <param name="candidatePath">Candidate run directory.</param>
    /// <param name="strict">Treat unpinned axes as blocking.</param>
    /// <param name="asJson">Emit JSON instead of a report.</param>
    /// <returns>
    /// 0 when comparable, <see cref="ExitCodes.Incomparable"/> when refused,
    /// <see cref="ExitCodes.UsageError"/> when a path is unusable.
    /// </returns>
    public static int Run(string baselinePath, string candidatePath, bool strict = false, bool asJson = false)
    {
        if (!TryLoad(baselinePath, "--baseline", out var baseline)) return ExitCodes.UsageError;
        if (!TryLoad(candidatePath, "--candidate", out var candidate)) return ExitCodes.UsageError;

        RunComparison comparison;
        try
        {
            comparison = RunComparison.Of(baseline, candidate, strict);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"✖ {ex.Message}");
            return ExitCodes.UsageError;
        }

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                verdict = comparison.Verdict.ToString(),
                strict,
                matched = comparison.Scenarios.Count,
                baselineOnly = comparison.BaselineOnly,
                candidateOnly = comparison.CandidateOnly,
                refusalReasons = comparison.RefusalReasons,
                unpinnedAxes = comparison.WithUnpinnedAxes
                    .SelectMany(s => s.Unpinned.Select(a => new { scenario = s.ScenarioId, axis = a.Name }))
                    .ToList(),
                scenariosWithoutAFloor = comparison.ScenariosWithoutAFloor.Count,
                judgeGradedItsOwnSubject = comparison.ScenariosWhereTheJudgeGradedItsOwnSubject.Count,
                deltas = comparison.Verdict is ComparisonVerdict.Comparable
                    ? comparison.Scenarios.Select(s => new
                    {
                        scenario = s.ScenarioId,
                        baseline = s.BaselineScore,
                        candidate = s.CandidateScore,
                        delta = s.ScoreDelta,
                    }).ToList()
                    : null,
            }, s_json));

            return comparison.Verdict is ComparisonVerdict.Comparable ? 0 : ExitCodes.Incomparable;
        }

        return Render(comparison, baselinePath, candidatePath, strict);
    }

    private static int Render(RunComparison comparison, string baselinePath, string candidatePath, bool strict)
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
            return ExitCodes.Incomparable;
        }

        Console.WriteLine("✅ COMPARABLE — every gating axis matched on every shared scenario.");
        Console.WriteLine();

        // The blind spots come BEFORE the numbers, on purpose: printed after a delta they read as
        // a footnote to a result the reader has already taken.
        WriteBlindSpots(comparison);

        Console.WriteLine("  scenario                                   baseline  candidate     delta");
        Console.WriteLine("  ---------------------------------------------------------------------");
        foreach (var s in comparison.Scenarios)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Fit(s.ScenarioId, 40),-40}  {s.BaselineScore,8:F4}  {s.CandidateScore,9:F4}  {s.ScoreDelta,+8:F4}"));
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  mean score delta: {comparison.MeanScoreDelta:F4}  ·  recovered {comparison.Recovered}  ·  regressed {comparison.Regressed}"));
        Console.WriteLine();
        return 0;
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

    private static bool TryLoad(string path, string option, out List<ScenarioResult> results)
    {
        results = [];

        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine($"✖ {option} is required.");
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"✖ {option}: '{path}' is not a usable path ({ex.GetType().Name}).");
            return false;
        }

        if (!Directory.Exists(full))
        {
            Console.Error.WriteLine($"✖ {option}: no such directory: {full}");
            return false;
        }

        // A run directory is a directory with a scenarios/ folder in it. Accept the scenarios folder
        // itself too, because that is what a reader who tab-completes will land on.
        string scenarios = Directory.Exists(Path.Combine(full, "scenarios"))
            ? Path.Combine(full, "scenarios")
            : full;

        var files = Directory.GetFiles(scenarios, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine(
                $"✖ {option}: {full} contains no scenario files. Point it at a run directory — the one that "
              + "holds manifest.json, summary.json and scenarios/.");
            return false;
        }

        foreach (string file in files)
        {
            ScenarioResult? result;
            try
            {
                result = JsonSerializer.Deserialize<ScenarioResult>(File.ReadAllText(file), s_json);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"✖ {option}: {file} is not a readable scenario file — {ex.Message}");
                return false;
            }

            if (result is null)
            {
                Console.Error.WriteLine($"✖ {option}: {file} deserialised to null.");
                return false;
            }

            results.Add(result);
        }

        return true;
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
