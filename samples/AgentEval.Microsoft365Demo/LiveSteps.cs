// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Microsoft365;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The live steps after readiness. They run only when readiness passed and the consent was given; otherwise their stage
/// rows say why not. When they run: the live agent is built once (fresh context per turn, the turn cap, the run's sinks
/// and token source), then the prompt step (<see cref="PromptStep"/>) and the benchmark (<see cref="Benchmark"/>) run
/// through it. The benchmark is then written as a sealed AEF run (<see cref="BenchmarkAef"/>).
/// </summary>
internal static class LiveSteps
{
    /// <summary>Runs the prompt and the benchmark when they may run, and records their stage rows.</summary>
    /// <param name="context">The run's context.</param>
    /// <param name="config">The validated config; null when it did not load (readiness has then failed).</param>
    /// <param name="record">The run's record (it holds the readiness report); the rows, the prompt turn and the benchmark go here.</param>
    /// <param name="caseSet">The benchmark's cases (loaded when the benchmark is planned).</param>
    /// <param name="judge">The judge of the criteria.</param>
    /// <param name="cancellationToken">Stops the run: the turn in progress ends, the rest are not sent.</param>
    public static async Task RunAsync(DemoContext context, Microsoft365Config? config, RunRecord record, DemoCaseSet? caseSet, JudgeChoice judge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(judge);
        var settings = context.Settings;
        var stages = record.Stages;
        var readiness = record.Readiness ?? throw new InvalidOperationException("The live steps run after the readiness check.");
        if (settings.ReadinessOnly)
        {
            stages.Add(new StageRow(StageNames.Prompt, StageResult.Skipped, "not requested (--readiness-only)"));
            stages.Add(new StageRow(StageNames.Benchmark, StageResult.Skipped, "not requested (--readiness-only)"));
            return;
        }

        if (readiness.AnyFailed || !readiness.AllPassed)
        {
            var why = readiness.AnyFailed
                ? "not run: readiness failed (fix it first; the table above says what to do)"
                : "not run: readiness did not complete";
            var result = readiness.AnyFailed ? StageResult.Skipped : StageResult.NotMeasured;
            stages.Add(new StageRow(StageNames.Prompt, result, why));
            stages.Add(BenchmarkRow(settings, result, why));
            return;
        }

        if (!settings.Consent)
        {
            var why = $"not run: needs {DemoCommandLine.ConsentFlag} (or {DemoCommandLine.ConsentVariable}=true) — it runs as the signed-in user";
            stages.Add(new StageRow(StageNames.Prompt, StageResult.NotMeasured, why));
            stages.Add(BenchmarkRow(settings, StageResult.NotMeasured, why));
            context.Console.Line(DemoVerbosity.Normal, $"Readiness passed. The prompt and the benchmark need {DemoCommandLine.ConsentFlag}: nothing was sent to the agent.", Tint.Warn);
            context.Console.Blank();
            return;
        }

        var options = new WorkIqOptions
        {
            SendTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            TurnDeadline = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            RecoveryBudget = TimeSpan.FromSeconds(Math.Min(120, settings.TimeoutSeconds)),
            ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,
            MaxTurns = settings.MaxTurns,
            ContentPolicy = settings.ContentPolicy,
            RawCaptureDirectory = settings.CaptureRaw ? context.Folder.RawFolder : null,
            ProfileOverride = settings.Profile,
            Verbose = false,
            TraceFile = context.Folder.Trace,
            IssueFile = context.Folder.Issues,
            RunId = context.Folder.RunId,
            TraceSinks = [context.TraceSink],
            IssueSinks = [context.IssueSink],
        };

        var loaded = config ?? throw new InvalidOperationException("The config passed readiness but did not load in the demo.");
        // The readiness check's token source: a sign-in it completed serves these turns too, and a new device-code prompt
        // (a token that can no longer be refreshed) goes through the demo's prompt writer and waits for the code's expiry.
        await using var agent = Microsoft365AgentFactory.BuildLive(
            loaded,
            iUnderstandLiveSideEffects: true,
            options,
            context.TokenProviderFor(loaded),
            context.Turns.OnTurn,
            context.NewInnerHandler());

        var promptResult = await PromptStep.RunAsync(context, agent, record, cancellationToken).ConfigureAwait(false);
        if (settings.NoBenchmark)
        {
            stages.Add(new StageRow(StageNames.Benchmark, StageResult.Skipped, "not requested (--no-benchmark)"));
            return;
        }

        if (promptResult == StageResult.Fail)
        {
            stages.Add(new StageRow(StageNames.Benchmark, StageResult.Skipped, "not run: the prompt failed (the benchmark would send its turns into the same failure)"));
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            stages.Add(new StageRow(StageNames.Benchmark, StageResult.NotMeasured, "not run: the run was canceled"));
            return;
        }

        var cases = caseSet ?? throw new InvalidOperationException("The benchmark runs without its cases.");
        await RunBenchmarkAsync(context, agent, loaded, cases, judge, record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The benchmark's row when it does not run: skipped with <c>--no-benchmark</c>, else <paramref name="result"/> with <paramref name="why"/>.</summary>
    private static StageRow BenchmarkRow(DemoSettings settings, StageResult result, string why) => settings.NoBenchmark
        ? new StageRow(StageNames.Benchmark, StageResult.Skipped, "not requested (--no-benchmark)")
        : new StageRow(StageNames.Benchmark, result, why);

    private static async Task RunBenchmarkAsync(DemoContext context, WorkIqEvaluableAgent agent, Microsoft365Config config, DemoCaseSet caseSet, JudgeChoice judge, RunRecord record, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var cases = caseSet.Cases;
        console.Line(DemoVerbosity.Normal, $"Benchmark ({cases.Count.ToString(CultureInfo.InvariantCulture)} cases, {caseSet.Source})", Tint.Accent);

        // Before any judge call, at every verbosity: which judge grades the criteria (provider, model and the variables
        // that chose it), or that none does. A judge call costs money on the provider the environment selects.
        console.Line(DemoVerbosity.Quiet, "  judge: " + judge.Description, judge.IsAvailable ? Tint.Accent : Tint.Warn);

        var startedAt = context.Now();
        var benchmark = await Benchmark.RunAsync(agent, cases, judge, context.Turns, console, cancellationToken).ConfigureAwait(false);
        record.Benchmark = benchmark;
        record.Aef = BenchmarkAef.Write(context, config, caseSet, judge, benchmark, startedAt);
        var fail = benchmark.Count(CaseVerdict.Fail);
        var error = benchmark.Count(CaseVerdict.Error);
        var notMeasured = benchmark.Count(CaseVerdict.NotMeasured);
        var detail = $"{benchmark.Cases.Count.ToString(CultureInfo.InvariantCulture)} cases: {benchmark.Count(CaseVerdict.Pass).ToString(CultureInfo.InvariantCulture)} PASS, " +
                     $"{fail.ToString(CultureInfo.InvariantCulture)} FAIL, {error.ToString(CultureInfo.InvariantCulture)} ERROR, {notMeasured.ToString(CultureInfo.InvariantCulture)} NOT MEASURED";
        if (benchmark.CriteriaNotGraded is { } notGraded)
        {
            detail += "; criteria not graded: " + notGraded;
        }

        if (benchmark.StopReason is not null)
        {
            detail += "; stopped early: " + benchmark.StopReason;
        }

        var result = fail > 0 ? StageResult.Fail : error + notMeasured > 0 ? StageResult.NotMeasured : StageResult.Pass;
        record.Stages.Add(new StageRow(StageNames.Benchmark, result, detail));
        console.Line(DemoVerbosity.Normal, $"  {Summary.Symbol(result)} {StageNames.Benchmark.PadRight(22)} {detail}",
            result switch { StageResult.Pass => Tint.Good, StageResult.Fail => Tint.Bad, _ => Tint.Warn });
        console.Blank();
    }
}
