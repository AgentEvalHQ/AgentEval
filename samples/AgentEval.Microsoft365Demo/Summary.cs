// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;

namespace AgentEval.Microsoft365Demo;

/// <summary>The result of one stage of the demo run.</summary>
internal enum StageResult
{
    /// <summary>The stage ran and passed (✓).</summary>
    Pass,

    /// <summary>The stage ran and failed (✗); the run exits 1.</summary>
    Fail,

    /// <summary>The stage was not requested, or an earlier stage failed (–); it does not change the exit code.</summary>
    Skipped,

    /// <summary>The stage was requested but not measured (–): no consent, no answer selected, a case not measured; exit 11 when nothing failed.</summary>
    NotMeasured,
}

/// <summary>One row of the stage table.</summary>
/// <param name="Name">The stage (<c>readiness: config</c>, <c>prompt</c>, <c>benchmark</c>, …).</param>
/// <param name="Result">Its result.</param>
/// <param name="Detail">What happened, in one line.</param>
internal sealed record StageRow(string Name, StageResult Result, string Detail);

/// <summary>The names of the demo's stages in the stage table.</summary>
internal static class StageNames
{
    /// <summary>The prompt step.</summary>
    public const string Prompt = "prompt";

    /// <summary>The benchmark.</summary>
    public const string Benchmark = "benchmark";

    /// <summary>The prefix of the readiness stages' names (<c>readiness: config</c>, …).</summary>
    public const string ReadinessPrefix = "readiness: ";

    /// <summary>The row that stands for the whole readiness check when it did not run.</summary>
    public const string Readiness = "readiness";
}

/// <summary>Everything the reports of one run are written from.</summary>
internal sealed record DemoReport
{
    /// <summary>The settings.</summary>
    public required DemoSettings Settings { get; init; }

    /// <summary>The run folder.</summary>
    public required RunFolder Folder { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run ended.</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>The config as loaded (with the <c>--agent-id</c> override); null when it did not load.</summary>
    public Microsoft365Config? Config { get; init; }

    /// <summary>The readiness report; null when the check did not run.</summary>
    public ReadinessReport? Readiness { get; init; }

    /// <summary>Every stage, in order.</summary>
    public required IReadOnlyList<StageRow> Stages { get; init; }

    /// <summary>The prompt step's turn, when it ran.</summary>
    public WorkIqTurnResult? PromptTurn { get; init; }

    /// <summary>The benchmark's outcome; null when it did not run.</summary>
    public BenchmarkOutcome? Benchmark { get; init; }

    /// <summary>The benchmark's AEF run; null when the benchmark did not run or its AEF run could not be written.</summary>
    public AefRunRecord? Aef { get; init; }

    /// <summary>The cases and where they came from.</summary>
    public DemoCaseSet? CaseSet { get; init; }

    /// <summary>What the judge was.</summary>
    public required string JudgeDescription { get; init; }

    /// <summary>Every issue the provider registered (readiness and the agent's run).</summary>
    public required IReadOnlyList<WorkIqIssue> Issues { get; init; }

    /// <summary>Every write that failed.</summary>
    public required IReadOnlyList<string> WriteErrors { get; init; }

    /// <summary>The unhandled runtime error, when the run stopped on one.</summary>
    public string? RuntimeError { get; init; }

    /// <summary>The exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>What the exit code means for this run.</summary>
    public required string ExitMeaning { get; init; }

    /// <summary>The run-folder-relative evidence file of a turn.</summary>
    public Func<WorkIqTurnResult, string?> EvidenceFileOf { get; init; } = _ => null;
}

/// <summary>Writes <c>summary.md</c>, <c>results.md</c> and <c>results.json</c> and builds the final screen table.</summary>
internal static class Summary
{
    /// <summary>The schema id of <c>results.json</c>.</summary>
    public const string ResultsSchema = "agenteval.m365.demo-results/v1";

    /// <summary>The statement every report ends its file list with.</summary>
    public const string NoTokenStatement = "No file in the run folder contains a token, an auth code or the Authorization header.";

    /// <summary>
    /// The command that checks the AEF run, in the form a tester who follows only the demo can run: the CLI from this
    /// repository, from the repository root. The folder follows it, quoted.
    /// </summary>
    public const string VerifyCommand = "dotnet run --project src/AgentEval.Cli --framework net10.0 -- aef verify";

    /// <summary>What summary.md says about <c>aef/</c> when the tester must remove content: it is sealed, so it is deleted, never edited.</summary>
    public const string AefRedactionRule =
        "`aef/` holds the same answers and is sealed: never edit it. Delete it if it holds anything you cannot share, and say so.";

    /// <summary>The demo's informational version.</summary>
    public static string DemoVersion { get; } =
        typeof(Summary).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Writes the three files (failures are recorded in the folder's write errors).</summary>
    /// <param name="report">The report.</param>
    public static void WriteAll(DemoReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Folder.TryWriteJson(report.Folder.ResultsJson, ToResultsJson(report));
        report.Folder.TryWriteText(report.Folder.ResultsMarkdown, ToResultsMarkdown(report));
        report.Folder.TryWriteText(report.Folder.Summary, ToSummaryMarkdown(report));
    }

    /// <summary>The overall result word: <c>PASS</c>, <c>FAIL</c>, <c>NOT MEASURED</c> or <c>ERROR</c>.</summary>
    /// <param name="exitCode">The exit code.</param>
    public static string ResultWord(int exitCode) => exitCode switch
    {
        DemoExitCodes.Success => "PASS",
        DemoExitCodes.Failure => "FAIL",
        DemoExitCodes.NotMeasured => "NOT MEASURED",
        _ => "ERROR",
    };

    /// <summary>The stage symbol: ✓ pass, ✗ fail, – skipped or not measured.</summary>
    /// <param name="result">The result.</param>
    public static string Symbol(StageResult result) => result switch
    {
        StageResult.Pass => "✓",
        StageResult.Fail => "✗",
        _ => "–",
    };

    /// <summary>The stage result as printed.</summary>
    /// <param name="result">The result.</param>
    public static string Label(StageResult result) => result switch
    {
        StageResult.Pass => "PASS",
        StageResult.Fail => "FAIL",
        StageResult.NotMeasured => "NOT MEASURED",
        _ => "SKIPPED",
    };

    /// <summary>The final table the screen shows at every verbosity: the stages, the cases, the result and the files.</summary>
    /// <param name="report">The report.</param>
    public static string FinalTable(DemoReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder(2048);
        text.Append('\n').Append("Microsoft 365 demo — run ").Append(report.Folder.RunId).Append('\n');
        foreach (var stage in report.Stages)
        {
            text.Append("  ").Append(Symbol(stage.Result)).Append(' ').Append(stage.Name.PadRight(22)).Append(' ')
                .Append(Label(stage.Result).PadRight(13)).Append(' ').Append(IssueText.Truncate(IssueText.Flatten(stage.Detail), 140)).Append('\n');
        }

        if (report.Benchmark is { Cases.Count: > 0 } benchmark)
        {
            text.Append('\n').Append("  ").Append("Case".PadRight(34)).Append("Verdict".PadRight(14)).Append("Turn".PadRight(15))
                .Append("Time".PadRight(10)).Append("Criteria").Append('\n');
            foreach (var c in benchmark.Cases)
            {
                var criteria = c.Checks.FirstOrDefault(k => k.Name == Benchmark.CriteriaCheck);
                text.Append("  ").Append(IssueText.Truncate(c.Case.Id, 32).PadRight(34))
                    .Append(Benchmark.VerdictLabel(c.Verdict).PadRight(14))
                    .Append((c.Turn?.Outcome.ToString() ?? "(none)").PadRight(15))
                    .Append((c.Turn is null ? "–" : TurnRecorder.Seconds(c.Turn.Timing.Elapsed)).PadRight(10))
                    .Append(criteria is null ? "–" : c.Score is { } s ? $"{s.ToString(CultureInfo.InvariantCulture)}/100 {Benchmark.CheckLabel(criteria.Result)}" : Benchmark.CheckLabel(criteria.Result))
                    .Append('\n');
            }
        }

        var issues = IssueGroups(report.Issues).Select(g => $"{g.Key.Name} {g.Count().ToString(CultureInfo.InvariantCulture)}").ToArray();
        text.Append('\n').Append("  Issues: ").Append(issues.Length == 0 ? "none" : string.Join(", ", issues)).Append('\n');
        if (report.RuntimeError is not null)
        {
            text.Append("  Runtime error: ").Append(IssueText.Flatten(report.RuntimeError)).Append('\n');
        }

        foreach (var error in report.WriteErrors)
        {
            text.Append("  Not written: ").Append(error).Append('\n');
        }

        text.Append('\n').Append("Result: ").Append(ResultWord(report.ExitCode)).Append(" (exit ")
            .Append(report.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(") — ").Append(report.ExitMeaning).Append('\n');
        text.Append("Run folder: ").Append(report.Folder.Root.FullName).Append('\n');
        text.Append("  read first: ").Append(RunFolder.SummaryName).Append("; the full screen log: ").Append(RunFolder.ConsoleLogName)
            .Append("; trace: ").Append(RunFolder.TraceName).Append("; issues: ").Append(RunFolder.IssuesName).Append('\n');
        if (report.Aef is { } aef)
        {
            text.Append("  AEF run: ").Append(aef.Folder).Append(aef.AbortReason is null ? " (sealed)" : $" (sealed, closed aborted: {aef.AbortReason})")
                .Append("; check it from the repository root with: ").Append(VerifyCommand).Append(" \"")
                .Append(Path.GetFullPath(Path.Combine(report.Folder.Root.FullName, aef.Folder))).Append("\"\n");
        }

        return text.ToString();
    }

    /// <summary>summary.md: the header, the stage table, the case table, the issues by category, the files and what to send back.</summary>
    /// <param name="report">The report.</param>
    public static string ToSummaryMarkdown(DemoReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = report.Settings;
        var md = new StringBuilder(8192);
        md.Append("# Microsoft 365 demo — run summary\n\n");
        md.Append("**Result: ").Append(ResultWord(report.ExitCode)).Append("** (exit ").Append(report.ExitCode.ToString(CultureInfo.InvariantCulture))
            .Append(") — ").Append(Cell(report.ExitMeaning)).Append("\n\n");

        md.Append("| Run | Value |\n|---|---|\n");
        Row(md, "Demo run id", Code(report.Folder.RunId) + " (stamps the agent's trace events and issues)");
        Row(md, "Readiness run id", report.Readiness is null ? "the readiness check did not run" : Code(report.Readiness.RunId));
        Row(md, "Started (UTC)", Utc(report.StartedAt));
        Row(md, "Ended (UTC)", Utc(report.EndedAt));
        Row(md, "Config file", Code(s.ConfigFile.FullName) + " (" + Cell(s.ConfigSource) + ")");
        var config = report.Config;
        Row(md, "Tenant id", config is null ? "not loaded" : Code(config.TenantId.Trim()));
        Row(md, "App (client) id", config is null ? "not loaded" : Code(config.AppClientId.Trim()));
        Row(md, "Agent", config is null ? "not loaded" : string.IsNullOrWhiteSpace(config.AgentId)
            ? "the default Work IQ agent (no agentId)"
            : Code(config.AgentId!.Trim()) + (s.AgentId is null ? " (config)" : " (--agent-id)"));
        Row(md, "Agent display name", config is null ? "not loaded" : Cell(config.DisplayName));
        Row(md, "Endpoint", config is null ? "not loaded" : Code(config.Endpoint.Trim()));
        Row(md, "Signed-in account", SignedInAccount(report.Readiness));
        Row(md, "Profile", Code(s.ProfileName) + " (" + s.Profile + ")");
        Row(md, "Content policy", Code(s.ContentPolicy.ToString()));
        Row(md, "Consent", Cell(s.ConsentSource));
        Row(md, "Judge", Cell(report.JudgeDescription));
        Row(md, "Dataset", report.CaseSet is null
            ? "not loaded"
            : Cell(report.CaseSet.Source) + $" ({report.CaseSet.Cases.Count.ToString(CultureInfo.InvariantCulture)} cases)" +
              (report.CaseSet.IgnoredFieldsNote is { } ignored ? "; " + Cell(ignored) : ""));
        Row(md, "Turn cap", $"{s.MaxTurns.ToString(CultureInfo.InvariantCulture)} (turns counted, cost not metered)");
        Row(md, "Timeout", $"{s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s per readiness stage or agent turn");
        Row(md, "Screen verbosity", s.Verbosity.ToString().ToLowerInvariant() + " (console.log always has the full view)");
        Row(md, "Demo version", Code(DemoVersion));
        Row(md, "Provider version", Code(WorkIqDiagnostics.AdapterVersion ?? "unknown"));
        Row(md, "A2A version", Code(WorkIqDiagnostics.A2AVersion));
        Row(md, "Config SHA-256", report.Readiness?.ConfigSha256 is { } sha ? Code(sha) : config is null ? "not computed" : Code(config.ComputeIdentitySha256()));
        Row(md, ".NET", Cell(RuntimeInformation.FrameworkDescription));
        Row(md, "OS", Cell(RuntimeInformation.OSDescription));
        md.Append('\n');

        md.Append("## Stages\n\n| # | Stage | Result | Detail |\n|---|---|---|---|\n");
        var n = 0;
        foreach (var stage in report.Stages)
        {
            n++;
            md.Append("| ").Append(n.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(Cell(stage.Name)).Append(" | ")
                .Append(Symbol(stage.Result)).Append(" **").Append(Label(stage.Result)).Append("** | ").Append(Cell(stage.Detail)).Append(" |\n");
        }

        md.Append('\n');
        if (report.PromptTurn is { } prompt)
        {
            md.Append("## The prompt\n\n");
            md.Append("- Prompt: ").Append(Code(s.Prompt)).Append('\n');
            md.Append("- Turn: ").Append(Cell(TurnRecorder.TurnLine(prompt).Trim())).Append('\n');
            md.Append("- Evidence: ").Append(Cell(TurnRecorder.EvidenceAvailability(prompt.Evidence))).Append('\n');
            if (report.EvidenceFileOf(prompt) is { } file)
            {
                md.Append("- Evidence document: `").Append(file).Append("` (holds the answer text under the ").Append(s.ContentPolicy).Append(" policy)\n");
            }

            md.Append('\n');
        }

        md.Append("## Benchmark\n\n");
        if (report.Benchmark is { } benchmark)
        {
            md.Append(CaseTable(benchmark));
            if (benchmark.StopReason is not null)
            {
                md.Append("\nThe benchmark stopped early: ").Append(Cell(benchmark.StopReason)).Append(".\n");
            }

            md.Append("\nPer-check detail: `").Append(RunFolder.ResultsMarkdownName).Append("` and `").Append(RunFolder.ResultsJsonName).Append("`.\n\n");
            md.Append(AefParagraph(report)).Append('\n');
        }
        else
        {
            var row = report.Stages.FirstOrDefault(r => r.Name == StageNames.Benchmark);
            md.Append("The benchmark did not run: ").Append(Cell(NotRunDetail(row))).Append(".\n\n");
        }

        md.Append("## Issues\n\n");
        if (report.Issues.Count == 0)
        {
            md.Append("No issue was registered.\n\n");
        }
        else
        {
            md.Append("Every anomaly the run registered, grouped by who or what can fix it (the same entries as `")
                .Append(RunFolder.IssuesName).Append("`).\n\n");
            foreach (var group in IssueGroups(report.Issues))
            {
                md.Append("### ").Append(group.Key.Name).Append(" (").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append(") — ")
                    .Append(Cell(group.Key.WhoFixes)).Append("\n\n");
                foreach (var issue in group)
                {
                    var (cause, action) = IssueText.CauseAndAction(issue);
                    md.Append("- **").Append(Cell(issue.Code)).Append("** at stage ").Append(issue.Stage);
                    var codes = IssueText.Codes(issue);
                    if (codes.Length > 0)
                    {
                        md.Append(" — ").Append(Cell(codes));
                    }

                    md.Append('\n');
                    md.Append("  - message: ").Append(Cell(issue.Message)).Append('\n');
                    md.Append("  - cause: ").Append(Cell(cause)).Append('\n');
                    md.Append("  - action: ").Append(Cell(action)).Append('\n');
                    md.Append("  - request-id: ").Append(issue.RequestId is null ? "not received" : Code(issue.RequestId)).Append('\n');
                    if (issue.TaskId is not null || issue.ContextId is not null)
                    {
                        md.Append("  - task / context: ").Append(Code(issue.TaskId ?? "(none)")).Append(" / ").Append(Code(issue.ContextId ?? "(none)")).Append('\n');
                    }

                    if (issue.CaseId is not null)
                    {
                        md.Append("  - case: ").Append(Code(issue.CaseId)).Append('\n');
                    }
                }

                md.Append('\n');
            }
        }

        if (report.RuntimeError is not null || report.WriteErrors.Count > 0)
        {
            md.Append("## Errors of the run itself\n\n");
            if (report.RuntimeError is not null)
            {
                md.Append("- Runtime error: ").Append(Cell(report.RuntimeError)).Append('\n');
            }

            foreach (var error in report.WriteErrors)
            {
                md.Append("- Not written: ").Append(Cell(error)).Append('\n');
            }

            md.Append('\n');
        }

        md.Append("## Files\n\n| File | What | Tenant content |\n|---|---|---|\n");
        FileRow(md, RunFolder.SummaryName, "this summary", "ids, codes, messages");
        FileRow(md, RunFolder.ConsoleLogName, "everything printed, plus every trace event in full (the device-code user code is masked)", "yes: the prompt step's answer");
        FileRow(md, RunFolder.TraceName, "the connector's trace: every event of the readiness check and the agent's run", "ids, codes, timings; no prompt, no answer");
        FileRow(md, RunFolder.IssuesName, "the connector's issue register: every anomaly with its cause", "ids, codes; no prompt, no answer");
        FileRow(md, RunFolder.ReadinessMarkdownName + ", " + RunFolder.ReadinessJsonName, "the readiness report (stage by stage, with evidence)", "tenant, app and agent ids, a masked account name");
        FileRow(md, RunFolder.TurnsName, "one line per agent turn: outcome, ids, timing, failure codes and request-id", "no answer text");
        FileRow(md, RunFolder.EvidenceFolderName + "/<case>.json", $"the evidence document of each turn ({s.ContentPolicy})", "yes: answers, citation names and URLs");
        if (report.Aef is not null)
        {
            FileRow(md, RunFolder.AefFolderName + "/", "the benchmark as a sealed AEF run: the run settings, each case's state, and each turn's evidence document", $"yes: prompts, answers and the evidence documents ({s.ContentPolicy})");
        }

        FileRow(md, RunFolder.ResultsMarkdownName + ", " + RunFolder.ResultsJsonName, "per case: verdict, every check and what ran", "the judge's explanations");
        if (s.CaptureRaw)
        {
            FileRow(md, RunFolder.RawFolderName + "/", "the raw A2A requests, responses and stream frames (--capture-raw)", "yes: everything the service sent");
        }

        md.Append('\n').Append(NoTokenStatement).Append("\n\n");

        md.Append("## What to send back\n\n");
        md.Append(s.Zip
            ? "1. The zip written next to this folder: `" + report.Folder.ZipFile.Name + "`.\n"
            : "1. This whole folder, zipped (or run again with `--zip`).\n");
        md.Append("2. Read `").Append(RunFolder.EvidenceFolderName).Append("/` and `").Append(RunFolder.ConsoleLogName)
            .Append("` first: they hold the agent's answers. Remove what you cannot share and say so.")
            .Append(report.Aef is null ? "" : " " + AefRedactionRule)
            .Append('\n');
        if (s.CaptureRaw)
        {
            md.Append("3. `").Append(RunFolder.RawFolderName).Append("/` holds everything the service sent: send it only if the maintainers asked for it.\n");
        }

        md.Append("\nNever send `workiq.json`, the token cache, screenshots of the sign-in page, or any key.\n");
        return md.ToString();
    }

    /// <summary>results.md: the case table and every check of every case.</summary>
    /// <param name="report">The report.</param>
    public static string ToResultsMarkdown(DemoReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var md = new StringBuilder(4096);
        md.Append("# Microsoft 365 demo — benchmark results\n\n");
        md.Append("Run `").Append(report.Folder.RunId).Append("`; judge: ").Append(Cell(report.JudgeDescription)).Append("; dataset: ")
            .Append(report.CaseSet is null ? "not loaded" : Cell(report.CaseSet.Source)).Append(".\n\n");
        if (report.Benchmark is not { } benchmark)
        {
            var row = report.Stages.FirstOrDefault(r => r.Name == StageNames.Benchmark);
            md.Append("The benchmark did not run: ").Append(Cell(NotRunDetail(row))).Append(".\n");
            return md.ToString();
        }

        md.Append(CaseTable(benchmark)).Append('\n');
        if (benchmark.StopReason is not null)
        {
            md.Append("The benchmark stopped early: ").Append(Cell(benchmark.StopReason)).Append(".\n\n");
        }

        md.Append(AefParagraph(report)).Append('\n');

        foreach (var c in benchmark.Cases)
        {
            md.Append("## ").Append(Cell(c.Case.Id)).Append(" — ").Append(Benchmark.VerdictLabel(c.Verdict)).Append("\n\n");
            md.Append(Cell(c.Reason)).Append("\n\n");
            md.Append("| Check | Result | Detail |\n|---|---|---|\n");
            foreach (var check in c.Checks)
            {
                md.Append("| ").Append(Cell(check.Name)).Append(" | ").Append(Benchmark.CheckLabel(check.Result)).Append(" | ").Append(Cell(check.Detail)).Append(" |\n");
            }

            if (c.Criteria is { Count: > 0 } criteria)
            {
                md.Append("\nJudge, per criterion:\n\n");
                foreach (var criterion in criteria)
                {
                    md.Append("- ").Append(criterion.Met ? "met" : "not met").Append(": ").Append(Cell(criterion.Criterion));
                    if (!string.IsNullOrWhiteSpace(criterion.Explanation))
                    {
                        md.Append(" — ").Append(Cell(criterion.Explanation));
                    }

                    md.Append('\n');
                }
            }

            if (c.Turn is { } turn && report.EvidenceFileOf(turn) is { } file)
            {
                md.Append("\nEvidence: `").Append(file).Append("`\n");
            }

            md.Append('\n');
        }

        return md.ToString();
    }

    /// <summary>The <c>results.json</c> document.</summary>
    /// <param name="report">The report.</param>
    public static object ToResultsJson(DemoReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var cases = report.Benchmark?.Cases ?? [];
        return new
        {
            schema = ResultsSchema,
            runId = report.Folder.RunId,
            startedAt = report.StartedAt,
            endedAt = report.EndedAt,
            exitCode = report.ExitCode,
            result = ResultWord(report.ExitCode),
            exitMeaning = report.ExitMeaning,
            judge = report.JudgeDescription,
            dataset = report.CaseSet?.Source,
            benchmarkRan = report.Benchmark is not null,
            stopReason = report.Benchmark?.StopReason,
            aef = report.Aef is not { } written ? null : new { run = written.Folder, runHash = written.RunHash, abortReason = written.AbortReason },
            counts = new
            {
                pass = report.Benchmark?.Count(CaseVerdict.Pass) ?? 0,
                fail = report.Benchmark?.Count(CaseVerdict.Fail) ?? 0,
                error = report.Benchmark?.Count(CaseVerdict.Error) ?? 0,
                notMeasured = report.Benchmark?.Count(CaseVerdict.NotMeasured) ?? 0,
            },
            stages = report.Stages.Select(r => new { name = r.Name, result = Label(r.Result), detail = r.Detail }).ToArray(),
            cases = cases.Select(c => new
            {
                id = c.Case.Id,
                category = c.Case.Category,
                verdict = Benchmark.VerdictLabel(c.Verdict),
                reason = c.Reason,
                score = c.Score,
                passingScore = c.Case.PassingScore,
                checks = c.Checks.Select(k => new { name = k.Name, result = Benchmark.CheckLabel(k.Result), detail = k.Detail }).ToArray(),
                criteria = c.Criteria?.Select(k => new { criterion = k.Criterion, met = k.Met, explanation = k.Explanation }).ToArray(),
                turn = c.Turn is null ? null : new
                {
                    outcome = c.Turn.Outcome.ToString(),
                    transport = c.Turn.Transport.ToString(),
                    answerCompleteness = c.Turn.AnswerCompleteness.ToString(),
                    answerLength = c.Turn.AnswerText?.Length,
                    taskId = c.Turn.Task?.TaskId ?? c.Turn.Identity.ProviderTaskId,
                    contextId = c.Turn.ContextId ?? c.Turn.Identity.ProviderContextId,
                    turnId = c.Turn.Identity.TurnId,
                    elapsedMs = Math.Round(c.Turn.Timing.Elapsed.TotalMilliseconds, 1),
                    evidence = TurnRecorder.EvidenceAvailability(c.Turn.Evidence),
                    evidenceFile = report.EvidenceFileOf(c.Turn),
                    failure = c.Turn.Failure is not { } f ? null : new
                    {
                        category = f.Category.ToString(),
                        operation = f.Operation.ToString(),
                        certainty = f.Certainty.ToString(),
                        httpStatus = f.HttpStatus,
                        rpcErrorCode = f.RpcErrorCode,
                        requestId = f.RequestId,
                        message = WorkIqRedaction.Redact(f.SafeMessage),
                    },
                },
            }).ToArray(),
        };
    }

    /// <summary>
    /// The paragraph on the benchmark's AEF run, for summary.md and results.md: where it is, how to check it, how the
    /// verdicts map to its states, and why it closed aborted when it did.
    /// </summary>
    private static string AefParagraph(DemoReport report)
    {
        if (report.Aef is not { } aef)
        {
            return "AEF run: not written. summary.md lists the error under \"Errors of the run itself\".\n";
        }

        var md = new StringBuilder(512)
            .Append("AEF run: `").Append(aef.Folder).Append("`, the benchmark in AgentEval's record format, sealed")
            .Append(aef.RunHash is null ? "" : " (run hash `" + aef.RunHash + "`)").Append(". ")
            // The run folder's own path stays out of the file: it can name the tester's user folder.
            .Append("To check it, run `").Append(VerifyCommand).Append(" \"<run folder>/").Append(aef.Folder)
            .Append("\"` from the repository root. ")
            .Append("Each case's state there is its verdict: PASS is `passed`, FAIL is `failed`, ERROR is `error`, and NOT MEASURED is ")
            .Append("`not_measured` (`error` when the judge's call failed or gave no verdict). Neither is ever a pass or a fail.");
        if (aef.AbortReason is not null)
        {
            md.Append(" The benchmark stopped early, so the run closed aborted (`").Append(aef.AbortReason)
                .Append("`): a case it did not reach has no line.");
        }

        return md.Append('\n').ToString();
    }

    /// <summary>The issues grouped by <see cref="IssueText.GroupOf"/>, in group order (the categories, then the judge).</summary>
    private static IEnumerable<IGrouping<IssueGroup, WorkIqIssue>> IssueGroups(IEnumerable<WorkIqIssue> issues) =>
        issues.GroupBy(IssueText.GroupOf).OrderBy(g => g.Key.Order);

    private static string CaseTable(BenchmarkOutcome benchmark)
    {
        var md = new StringBuilder(1024);
        md.Append("| Case | Verdict | Turn | Answer | Time | Criteria | Reason |\n|---|---|---|---|---|---|---|\n");
        foreach (var c in benchmark.Cases)
        {
            var criteria = c.Checks.FirstOrDefault(k => k.Name == Benchmark.CriteriaCheck);
            md.Append("| ").Append(Code(c.Case.Id))
                .Append(" | **").Append(Benchmark.VerdictLabel(c.Verdict)).Append("**")
                .Append(" | ").Append(c.Turn?.Outcome.ToString() ?? "(none)")
                .Append(" | ").Append(c.Turn is null ? "–" : Cell(TurnRecorder.DescribeAnswer(c.Turn)))
                .Append(" | ").Append(c.Turn is null ? "–" : Benchmark.Milliseconds(c.Turn.Timing.Elapsed))
                .Append(" | ").Append(criteria is null ? "–" : (c.Score is { } s ? $"{s.ToString(CultureInfo.InvariantCulture)}/100 " : "") + Benchmark.CheckLabel(criteria.Result))
                .Append(" | ").Append(Cell(IssueText.Truncate(c.Reason, 300)))
                .Append(" |\n");
        }

        return md.ToString();
    }

    private static string SignedInAccount(ReadinessReport? readiness)
    {
        var auth = readiness?.Find(ReadinessStage.Auth);
        if (auth is not null)
        {
            foreach (var entry in auth.Evidence)
            {
                if (string.Equals(entry.Key, "account", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Value))
                {
                    return Cell(entry.Value) + " (masked, from the readiness sign-in)";
                }
            }
        }

        return "not recorded (the readiness sign-in did not run, did not pass, or reported no account)";
    }

    /// <summary>Why a stage did not run, without the "not run:" prefix its row detail may carry.</summary>
    private static string NotRunDetail(StageRow? row)
    {
        var detail = row?.Detail ?? "it was not reached";
        return detail.StartsWith("not run: ", StringComparison.Ordinal) ? detail["not run: ".Length..] : detail;
    }

    private static void Row(StringBuilder md, string name, string value) =>
        md.Append("| ").Append(name).Append(" | ").Append(value).Append(" |\n");

    private static void FileRow(StringBuilder md, string file, string what, string content) =>
        md.Append("| `").Append(file).Append("` | ").Append(Cell(what)).Append(" | ").Append(Cell(content)).Append(" |\n");

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Text safe inside a Markdown table cell or list item: redacted, one line, pipes escaped.</summary>
    private static string Cell(string? text) => IssueText.Flatten(WorkIqRedaction.Redact(text ?? "")).Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>Inline code for an identifier or path (escaped text when it holds a backtick).</summary>
    private static string Code(string? text)
    {
        var cell = Cell(text);
        return cell.Contains('`', StringComparison.Ordinal) ? cell : "`" + cell + "`";
    }
}
