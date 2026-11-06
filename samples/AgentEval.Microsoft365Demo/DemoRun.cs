// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// One run of the demo after the command line and the run folder are settled. It only orders the steps — the banner
/// (<see cref="DemoBanner"/>), readiness (<see cref="ReadinessStep"/>), the prompt and the benchmark
/// (<see cref="LiveSteps"/>) — and finishes the run: one exit decision (<see cref="ExitPolicy"/>), the reports, the
/// final table and the zip, all from that decision. The cases and their checks are in <see cref="Benchmark"/>.
/// </summary>
internal sealed class DemoRun
{
    private readonly DemoContext _context;
    private readonly RunRecord _record = new();
    private DemoCaseSet? _caseSet;
    private LoadedConfig _config = LoadedConfig.None;
    private JudgeChoice _judge = JudgeChoice.NotUsed;

    /// <summary>Creates the run.</summary>
    /// <param name="context">What the steps share.</param>
    /// <param name="caseSet">The <c>--dataset</c> cases; null uses the embedded default when the benchmark runs.</param>
    public DemoRun(DemoContext context, DemoCaseSet? caseSet)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _caseSet = caseSet;
    }

    private DemoSettings Settings => _context.Settings;

    private bool BenchmarkPlanned => Settings.Consent && !Settings.ReadinessOnly && !Settings.NoBenchmark;

    /// <summary>Runs every stage, then finishes the run (reports, final table, zip) and returns the exit code.</summary>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var startedAt = _context.Now();
        string? runtimeError = null;
        try
        {
            _config = LoadedConfig.Load(Settings);
            if (BenchmarkPlanned)
            {
                _caseSet ??= DemoDataset.LoadDefault();
                _judge = JudgeResolver.Resolve(Settings.NoJudge, _context.Overrides.JudgeClient, _context.Env);
            }

            DemoBanner.Print(_context, _config, _caseSet, _judge);
            await ReadinessStep.RunAsync(_context, _config, _record, cancellationToken).ConfigureAwait(false);
            await LiveSteps.RunAsync(_context, _config.Config, _record, _caseSet, _judge, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runtimeError = $"{ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}";
            _context.Console.Error("m365 demo: runtime error: " + runtimeError + ". The run folder is still written.");
            _context.Console.LogOnly(WorkIqRedaction.Redact(ex.ToString()));
            AddMissingStageRows("not run: the run stopped on a runtime error");
        }
        finally
        {
            _judge.Dispose();
            _context.SignIn.Flush();
        }

        return Finish(startedAt, runtimeError);
    }

    /// <summary>
    /// Decides the exit code and writes everything from that one decision. What can fail and must show in the reports
    /// happens first: the zip's location is checked before the decision. What can only fail later — closing console.log
    /// and filling the zip — makes the run exit 3; the reports are then written again from the new decision, so every
    /// report matches the exit code, and the screen says the result changed. A zip that fails after console.log was
    /// closed also appends that note to console.log, so its last lines never show the earlier result alone.
    /// </summary>
    private int Finish(DateTimeOffset startedAt, string? runtimeError)
    {
        var console = _context.Console;
        var folder = _context.Folder;
        var zipWritable = Settings.Zip && folder.TryCheckZipWritable();

        var (decision, report) = DecideAndWriteReports(startedAt, runtimeError);
        if (_context.IssueSink.RepeatedIssuesText is { } repeated)
        {
            console.Line(DemoVerbosity.Normal, repeated, Tint.Warn);
            console.Blank();
        }

        console.Line(DemoVerbosity.Quiet, Summary.FinalTable(report), decision.Code switch
        {
            DemoExitCodes.Success => Tint.Good,
            DemoExitCodes.NotMeasured => Tint.Warn,
            _ => Tint.Bad,
        });

        if (zipWritable)
        {
            console.Line(DemoVerbosity.Quiet, $"Zip: {folder.ZipFile.FullName} (written after this log is closed; send it back after reading {RunFolder.SummaryName})");
        }
        else if (Settings.Zip)
        {
            console.Line(DemoVerbosity.Quiet, $"Zip: not written ({folder.ZipFile.Name} could not be created, see 'Not written' above); zip the run folder yourself.", Tint.Bad);
        }

        console.Line(DemoVerbosity.Quiet, Summary.NoTokenStatement);
        console.CloseLog();
        if (console.LogError is not null && decision.Code != DemoExitCodes.Runtime)
        {
            (decision, _) = DecideAndWriteReports(startedAt, runtimeError);
            console.Error($"m365 demo: {console.LogError}. {ResultChanged(decision)}");
        }

        if (zipWritable)
        {
            try
            {
                var zip = _context.Overrides.ZipWriter?.Invoke(folder) ?? folder.WriteZip();
                console.Line(DemoVerbosity.Quiet, $"Zip written: {zip.FullName} ({zip.Length.ToString(CultureInfo.InvariantCulture)} bytes).", Tint.Accent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                folder.RecordError($"{folder.ZipFile.Name} (the zip beside the run folder): {ex.GetType().Name}: {ex.Message}");
                folder.TryDeleteZip();
                (decision, _) = DecideAndWriteReports(startedAt, runtimeError);
                console.Error($"m365 demo: the zip could not be written ({ex.GetType().Name}: {ex.Message}); zip the run folder yourself. {ResultChanged(decision)}");
                AppendToClosedLog($"m365 demo: after this log was closed, the zip could not be written ({ex.GetType().Name}); the table above shows the result before that. {ResultChanged(decision)}");
            }
        }

        return decision.Code;
    }

    /// <summary>
    /// Decides the exit code and writes results.json, results.md and summary.md from it. A report file that cannot be
    /// written changes the decision (exit 3), so the three are then written once more from the new one.
    /// </summary>
    private (ExitDecision Decision, DemoReport Report) DecideAndWriteReports(DateTimeOffset startedAt, string? runtimeError)
    {
        var folder = _context.Folder;
        ExitDecision decision;
        DemoReport report;
        var attempt = 0;
        do
        {
            decision = ExitPolicy.Decide(new ExitInputs(
                runtimeError,
                folder.WriteErrors,
                _context.Console.LogError,
                _record.Stages.ToArray(),
                _record.Benchmark,
                Settings.ReadinessOnly));
            report = BuildReport(startedAt, decision, runtimeError);
            var errorsBefore = folder.WriteErrors.Count;
            Summary.WriteAll(report);
            if (folder.WriteErrors.Count == errorsBefore)
            {
                break;
            }
        }
        while (++attempt < 2);

        return (decision, report);
    }

    private static string ResultChanged(ExitDecision decision) =>
        $"The result is now {Summary.ResultWord(decision.Code)} (exit {decision.Code.ToString(CultureInfo.InvariantCulture)}): {decision.Meaning}. " +
        $"{RunFolder.ResultsJsonName}, {RunFolder.ResultsMarkdownName} and {RunFolder.SummaryName} say the same.";

    /// <summary>
    /// Appends one line to console.log after it was closed. Best effort: a failure is ignored, because the screen and the
    /// reports already carry the line.
    /// </summary>
    private void AppendToClosedLog(string line)
    {
        try
        {
            File.AppendAllText(_context.Folder.ConsoleLog.FullName, line + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The screen and the reports say it; console.log keeps the result it showed when it was closed.
        }
    }

    /// <summary>After a runtime error: a row for every stage the run did not reach, so the table never ends silently.</summary>
    private void AddMissingStageRows(string why)
    {
        var stages = _record.Stages;
        if (!stages.Any(r => r.Name.StartsWith(StageNames.ReadinessPrefix, StringComparison.Ordinal)))
        {
            stages.Add(new StageRow(StageNames.Readiness, StageResult.NotMeasured, why));
        }

        foreach (var name in new[] { StageNames.Prompt, StageNames.Benchmark })
        {
            if (!stages.Any(r => r.Name == name))
            {
                stages.Add(new StageRow(name, StageResult.NotMeasured, why));
            }
        }
    }

    private DemoReport BuildReport(DateTimeOffset startedAt, ExitDecision decision, string? runtimeError) => new()
    {
        Settings = Settings,
        Folder = _context.Folder,
        StartedAt = startedAt,
        EndedAt = _context.Now(),
        Config = _config.Config,
        Readiness = _record.Readiness,
        Stages = _record.Stages.ToArray(),
        PromptTurn = _record.PromptTurn,
        Benchmark = _record.Benchmark,
        CaseSet = _caseSet,
        JudgeDescription = _judge.Description,
        Issues = _context.IssueSink.Issues,
        WriteErrors = _context.Console.LogError is null ? _context.Folder.WriteErrors : [.. _context.Folder.WriteErrors, _context.Console.LogError],
        RuntimeError = runtimeError,
        ExitCode = decision.Code,
        ExitMeaning = decision.Meaning,
        EvidenceFileOf = _context.Turns.EvidenceFileOf,
    };
}
