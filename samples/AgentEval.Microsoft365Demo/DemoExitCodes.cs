// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Microsoft365Demo;

/// <summary>The demo's exit codes.</summary>
internal static class DemoExitCodes
{
    /// <summary>Every stage that ran passed and everything requested was measured.</summary>
    public const int Success = 0;

    /// <summary>A stage failed (a readiness stage, or the prompt did not complete) or a case FAILED a check.</summary>
    public const int Failure = 1;

    /// <summary>A command-line mistake; nothing was created or sent.</summary>
    public const int Usage = 2;

    /// <summary>A runtime error, or a file could not be written; the run folder is still written.</summary>
    public const int Runtime = 3;

    /// <summary>Nothing failed, but something was not measured (no consent past readiness, no judge, a turn that did not complete, the turn cap, no config).</summary>
    public const int NotMeasured = 11;
}

/// <summary>Everything the exit code of a run that reached its run folder depends on.</summary>
/// <param name="RuntimeError">The unhandled runtime error, when the run stopped on one.</param>
/// <param name="WriteErrors">Every write of the run that failed (the run folder's files and the zip beside it).</param>
/// <param name="LogError">The error writing or closing console.log, when there was one.</param>
/// <param name="Stages">Every stage row, in order.</param>
/// <param name="Benchmark">The benchmark's outcome; null when it did not run.</param>
/// <param name="ReadinessOnly">Whether the run was <c>--readiness-only</c>.</param>
internal sealed record ExitInputs(
    string? RuntimeError,
    IReadOnlyList<string> WriteErrors,
    string? LogError,
    IReadOnlyList<StageRow> Stages,
    BenchmarkOutcome? Benchmark,
    bool ReadinessOnly);

/// <summary>The exit code of a run and what it means for that run, in one sentence.</summary>
/// <param name="Code">The exit code (<see cref="DemoExitCodes"/>).</param>
/// <param name="Meaning">What it means for this run.</param>
internal sealed record ExitDecision(int Code, string Meaning);

/// <summary>
/// The one place the exit code of a run is decided. Pure: the same inputs give the same decision. The run writes
/// results.json, results.md, summary.md and the final table from the decision it returns, so they never disagree with
/// the process exit. Order of precedence: a runtime error or a failed write (3), then a failed stage or case (1), then
/// anything not measured (11), else 0.
/// </summary>
internal static class ExitPolicy
{
    /// <summary>Decides the exit code of a run.</summary>
    /// <param name="inputs">The run's state.</param>
    public static ExitDecision Decide(ExitInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.RuntimeError is not null)
        {
            return new ExitDecision(DemoExitCodes.Runtime, "the run stopped on a runtime error: " + inputs.RuntimeError);
        }

        if (inputs.LogError is not null || inputs.WriteErrors.Count > 0)
        {
            return new ExitDecision(DemoExitCodes.Runtime, "a file of the run could not be written: " + (inputs.LogError ?? inputs.WriteErrors[0]));
        }

        var benchmark = inputs.Benchmark;
        var failed = inputs.Stages.Where(r => r.Result == StageResult.Fail).Select(r => r.Name)
            .Concat((benchmark?.IdsOf(CaseVerdict.Fail) ?? []).Select(id => "case " + id))
            .ToArray();
        if (failed.Length > 0)
        {
            return new ExitDecision(DemoExitCodes.Failure, "failed: " + string.Join(", ", failed));
        }

        var notMeasured = new List<string>();
        foreach (var row in inputs.Stages.Where(r => r.Result == StageResult.NotMeasured && r.Name != StageNames.Benchmark))
        {
            notMeasured.Add($"{row.Name} ({Short(row.Detail)})");
        }

        var errorCases = benchmark?.IdsOf(CaseVerdict.Error) ?? [];
        if (errorCases.Length > 0)
        {
            // Each ERROR row's reason says which: the turn did not complete, the agent failed before a turn result, or the
            // case could not be evaluated.
            notMeasured.Add($"{errorCases.Length.ToString(CultureInfo.InvariantCulture)} case(s) ERROR, not evaluated ({string.Join(", ", errorCases)}; results.md gives each reason)");
        }

        var notMeasuredCases = benchmark?.IdsOf(CaseVerdict.NotMeasured) ?? [];
        if (notMeasuredCases.Length > 0)
        {
            var why = benchmark!.StopReason is { } stop ? "; stopped early: " + stop : "";
            if (benchmark.CriteriaNotGraded is { } notGraded)
            {
                why += "; criteria not graded: " + notGraded;
            }

            notMeasured.Add($"{notMeasuredCases.Length.ToString(CultureInfo.InvariantCulture)} case(s) NOT MEASURED ({string.Join(", ", notMeasuredCases)}{why})");
        }

        if (notMeasured.Count == 0 && inputs.Stages.FirstOrDefault(r => r.Result == StageResult.NotMeasured) is { } stage)
        {
            // Only the benchmark row is not measured and no case says why (it did not start): its own detail does.
            notMeasured.Add($"{stage.Name} ({Short(stage.Detail)})");
        }

        if (notMeasured.Count > 0)
        {
            return new ExitDecision(DemoExitCodes.NotMeasured, "nothing failed, but not measured: " + string.Join("; ", notMeasured));
        }

        return new ExitDecision(DemoExitCodes.Success, inputs.ReadinessOnly
            ? "readiness passed (--readiness-only)"
            : "every stage that ran passed and everything requested was measured");
    }

    private static string Short(string detail) => IssueText.Truncate(IssueText.Flatten(detail), 160);
}
