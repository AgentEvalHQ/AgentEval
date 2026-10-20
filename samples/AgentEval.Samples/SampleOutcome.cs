// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;

namespace AgentEval.Samples;

/// <summary>
/// What a sample run found, for whatever ran it. A sample that prints ❌ FAILED, a below-threshold warning or
/// "skipped" also records it here; the runner turns the worst record into the process exit code and closes the
/// run with one <c>Sample result:</c> line. Until 2026-10 a sample exited 0 whatever it printed unless it threw,
/// so a sweep or a CI job read a printed ❌ FAILED as a pass.
/// </summary>
/// <remarks>
/// <para>
/// The codes follow the CLI's contract (<c>docs/cli.md</c>, <c>AgentEval.Cli.ExitCodes</c>):
/// <c>0</c> the sample ran and recorded no failure, warning or skip ("completed", not "passed": many samples
/// demonstrate an API and make no check); <c>1</c> a check the sample makes failed; <c>2</c> the sample number
/// or id does not exist; <c>3</c> the sample threw, or caught an error that stopped it (<see cref="Error"/>);
/// <c>10</c> a quality score fell below its threshold (WARN);
/// <c>11</c> the sample could not measure what it demonstrates — no provider, a capability the provider lacks,
/// a model it does not serve. Not measured is never 0.
/// </para>
/// <para>
/// When a run records several outcomes the worst wins: error, failed, then not measured, then warning. Record
/// <see cref="Fail"/> when the sample's own check did not hold. A sample that frames a scan or a gate as the check on
/// the agent under test (the red-team samples E2/E3 and L3, a Gatekeeper gate) fails when that check fails: an attack
/// that gets through is FAIL there. A failure the sample plants on purpose to show its detection (a planted regression
/// a snapshot test catches, a scripted attack the gate blocks) is the sample working, and records nothing.
/// </para>
/// </remarks>
internal static class SampleOutcome
{
    public const int Completed = 0;
    public const int Failed = 1;
    public const int UsageError = 2;
    public const int RuntimeError = 3;
    public const int Warning = 10;
    public const int NotMeasured = 11;

    private static readonly object s_gate = new();
    private static readonly List<(int Code, string Reason)> s_records = new();

    /// <summary>A check the sample makes did not hold (accuracy, structure, policy, a wrong tool).</summary>
    public static void Fail(string reason) => Record(Failed, reason);

    /// <summary>A quality score fell below its threshold; everything the sample checks was measured.</summary>
    public static void Warn(string reason) => Record(Warning, reason);

    /// <summary>The sample could not measure what it demonstrates; say what is missing.</summary>
    public static void Skip(string reason) => Record(NotMeasured, reason);

    /// <summary>
    /// The sample caught an exception that stopped its main point (a provider call threw mid-run). The same code as
    /// a sample that throws: the run neither passed nor failed a check, and it is not a missing provider either.
    /// </summary>
    public static void Error(string reason) => Record(RuntimeError, reason);

    /// <summary>
    /// Records a benchmark's root verdict the way every report reads it (<see cref="EvalScoreExtensions.ReportStatus"/>):
    /// FAIL fails, WARN warns, and a root that errored or measured nothing (ERROR, SKIPPED) is not measured. PASS
    /// records nothing.
    /// </summary>
    /// <param name="what">The benchmark, for the reason line.</param>
    /// <param name="root">The root score of the benchmark's result tree.</param>
    public static void RecordVerdict(string what, EvalScore root)
    {
        ArgumentNullException.ThrowIfNull(root);
        switch (root.ReportStatus())
        {
            case "FAIL":
                Fail($"{what}: FAIL (score {root.Value:P1})");
                break;
            case "WARN":
                Warn($"{what}: WARN (score {root.Value:P1})");
                break;
            case "ERROR":
            case "SKIPPED":
                Skip($"{what}: the run measured nothing (root {root.Label})");
                break;
        }
    }

    /// <summary>
    /// Records a benchmark's verdict from its whole result tree: the root's verdict as <see cref="RecordVerdict(string, EvalScore)"/>
    /// does, and NOT MEASURED when any check below it was skipped or errored. A weighted root skips such leaves, so a
    /// provider or judge that rejected some probes still left a PASS root, and the run exited 0 over probes it never
    /// tested. A leaf labelled <c>inapplicable</c> (not applicable by design, e.g. NIST's governance functions) is not a
    /// gap and is not counted.
    /// </summary>
    /// <param name="what">The benchmark, for the reason line.</param>
    /// <param name="result">The benchmark's result tree.</param>
    public static void RecordVerdict(string what, EvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RecordVerdict(what, result.Score);
        if (result.Score.ReportStatus() is "ERROR" or "SKIPPED")
            return;

        var leaves = Leaves(result).ToList();
        var unmeasured = leaves
            .Where(l => l.Score.Label.ToLowerInvariant() is "skipped" or "error")
            .Select(l => l.Metric.Name)
            .ToList();
        if (unmeasured.Count > 0)
        {
            var named = string.Join(", ", unmeasured.Take(5)) + (unmeasured.Count > 5 ? $" and {unmeasured.Count - 5} more" : "");
            Skip($"{what}: {unmeasured.Count} of {leaves.Count} checks were not measured (skipped or errored): {named}");
        }

        static IEnumerable<EvalResult> Leaves(EvalResult node) =>
            node.Details.SubResults is { Count: > 0 } children ? children.SelectMany(Leaves) : [node];
    }

    /// <summary>The exit code for what this run recorded so far.</summary>
    public static int ExitCode
    {
        get
        {
            lock (s_gate)
                return s_records.Aggregate(Completed, (worst, r) => Worse(worst, r.Code));
        }
    }

    /// <summary>The records of this run, in the order the sample made them.</summary>
    public static IReadOnlyList<(int Code, string Reason)> Records
    {
        get
        {
            lock (s_gate)
                return s_records.ToArray();
        }
    }

    /// <summary>Forget the previous run's records. The runner calls it before each sample.</summary>
    internal static void Reset()
    {
        lock (s_gate)
            s_records.Clear();
    }

    /// <summary>The more severe of two exit codes: runtime error, failed, not measured, warning, completed.</summary>
    internal static int Worse(int a, int b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>The closing line(s) of a run: the result, its exit code and each recorded reason.</summary>
    /// <param name="exitCode">The run's exit code.</param>
    /// <param name="mock">True for a <c>--mock</c> run, which the line labels so a canned reply never reads as a result.</param>
    internal static IReadOnlyList<string> Describe(int exitCode, bool mock = false)
    {
        var label = exitCode switch
        {
            Completed => "COMPLETED (no failed check, warning or skip recorded)",
            Failed => "FAIL",
            RuntimeError => "ERROR",
            Warning => "WARN",
            NotMeasured => "NOT MEASURED",
            _ => $"exit {exitCode}",
        };
        var mockLabel = mock ? " — mock run: canned replies, not a measurement" : "";
        var lines = new List<string> { $"Sample result: {label}{mockLabel} (exit {exitCode})" };
        lines.AddRange(Records.Select(r => $"  {Tag(r.Code)} {r.Reason}"));
        return lines;
    }

    private static void Record(int code, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (s_gate)
            s_records.Add((code, OneLine(reason)));
    }

    // A reason is one line of the closing summary: an exception or assertion message often spans several, so
    // join them, and cap a runaway one.
    private const int MaxReasonLength = 300;

    private static string OneLine(string reason)
    {
        var line = string.Join(" ", reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= MaxReasonLength ? line : line[..(MaxReasonLength - 1)] + "…";
    }

    private static int Rank(int code) => code switch
    {
        RuntimeError => 5,
        Failed => 4,
        NotMeasured => 3,
        Warning => 2,
        Completed => 0,
        _ => 1,
    };

    private static string Tag(int code) => code switch
    {
        Failed => "FAIL:",
        Warning => "WARN:",
        NotMeasured => "NOT MEASURED:",
        RuntimeError => "ERROR:",
        _ => "-",
    };
}

/// <summary>
/// Thrown by a sample whose own check failed and that cannot sensibly go on (a Gatekeeper invariant, a gate that let the
/// attack's effect happen). The runner records it as FAIL (exit 1), not as an error: the sample ran and its check did not
/// hold. It is an <see cref="InvalidOperationException"/>, so every catch that handled the old throw still does.
/// </summary>
internal sealed class SampleCheckFailedException(string message) : InvalidOperationException(message);
