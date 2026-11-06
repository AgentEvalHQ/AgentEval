// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The readiness step: runs the provider's readiness check (config, sign-in, agent card, protocol handshake; no prompt
/// is sent) with the run's sinks and token source, writes readiness.md and readiness.json, adds one stage row per
/// requested stage, prints each stage as it ends (through the trace sink) and the detail of a failed one.
/// </summary>
internal static class ReadinessStep
{
    /// <summary>Runs the readiness check and records its report and stage rows.</summary>
    /// <param name="context">The run's context.</param>
    /// <param name="config">The config as loaded.</param>
    /// <param name="record">Where the report and the rows go.</param>
    /// <param name="cancellationToken">Stops the check: the stage in progress fails as canceled, the rest are skipped.</param>
    public static async Task RunAsync(DemoContext context, LoadedConfig config, RunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(record);
        var settings = context.Settings;
        var folder = context.Folder;
        var console = context.Console;
        console.Line(DemoVerbosity.Normal, "Readiness (no prompt is sent)", Tint.Accent);
        var options = new ReadinessCheckOptions
        {
            Stages = null,
            Probe = false,
            IUnderstandLiveSideEffects = settings.Consent,
            ContinueAfterFailure = false,
            StageTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            Profile = settings.Profile,
            ProfileSource = "--profile",
            RawCaptureDirectory = settings.CaptureRaw ? folder.RawFolder : null,
            Verbose = false,
            TraceFile = folder.Trace,
            // One issue register for the run: the check writes issues.jsonl, the live agent appends to it.
            IssueFile = folder.Issues,
            DiagnosticsDirectory = folder.Root,
            Console = context.SignIn,
            TraceSinks = [context.TraceSink],
            IssueSinks = [context.IssueSink],
        };

        // The file keeps the config's path and hash in the report; an --agent-id override is passed as the loaded config
        // (unvalidated, so an override that breaks it fails the config stage; a file that does not load is passed as the file).
        var inCode = settings.AgentId is not null ? config.Overridden : null;
        var report = await Microsoft365ReadinessCheck.RunAsync(
            inCode,
            inCode is null ? settings.ConfigFile : null,
            options,
            context.NewInnerHandler(),
            context.TokenProviderFor(config.Config),
            cancellationToken).ConfigureAwait(false);
        context.SignIn.Flush();
        record.Readiness = report;
        folder.TryWriteText(folder.ReadinessMarkdown, ReadinessReportRenderer.ToMarkdown(report));
        folder.TryWriteText(folder.ReadinessJson, ReadinessReportRenderer.ToJson(report));

        foreach (var stage in report.Stages.Where(st => st.Requested))
        {
            var name = StageNames.ReadinessPrefix + stage.Stage.ToString().ToLowerInvariant();
            var result = stage.Result switch
            {
                ReadinessResult.Pass => StageResult.Pass,
                ReadinessResult.Fail => StageResult.Fail,
                _ when report.AnyFailed => StageResult.Skipped,
                _ => StageResult.NotMeasured,
            };
            var detail = stage.Result == ReadinessResult.Skipped ? stage.SkipReason ?? stage.Summary : stage.Summary;
            if (stage.Result == ReadinessResult.Fail && !string.IsNullOrWhiteSpace(stage.Action))
            {
                detail += " — do: " + stage.Action;
            }

            record.Stages.Add(new StageRow(name, result, detail));
            PrintStageDetail(console, stage);
        }

        console.Line(DemoVerbosity.Normal, $"  readiness {report.Verdict}: {folder.Relative(folder.ReadinessMarkdown.FullName)}, {folder.Relative(folder.ReadinessJson.FullName)}",
            report.AnyFailed ? Tint.Bad : report.AllPassed ? Tint.Good : Tint.Warn);
        console.Blank();
    }

    /// <summary>
    /// Prints a readiness stage line as its verdict event arrives (the trace sink calls it), so the tester sees each stage
    /// end instead of waiting for the whole check. The line's text is the event message after its <c>"&lt;stage&gt; VERDICT: "</c>
    /// prefix; the stage table and the FAILED block are printed from the typed report afterwards.
    /// </summary>
    /// <param name="console">The console.</param>
    /// <param name="e">The verdict event.</param>
    /// <param name="stage">The stage name.</param>
    /// <param name="verdict">The verdict (<c>pass</c>, <c>fail</c>, <c>skipped</c>).</param>
    /// <param name="requested">Whether the stage was requested.</param>
    public static void PrintStageLine(DemoConsole console, WorkIqTraceEvent e, string stage, string verdict, bool requested)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(e);
        if (!requested)
        {
            console.Line(DemoVerbosity.Detailed, $"  – {(StageNames.ReadinessPrefix + stage).PadRight(22)} not requested", Tint.Dim);
            return;
        }

        var (symbol, tint) = verdict switch
        {
            "pass" => ("✓", Tint.Good),
            "fail" => ("✗", Tint.Bad),
            _ => ("–", Tint.Warn),
        };
        var message = e.Message;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        var summary = colon >= 0 ? message[(colon + 2)..] : message;
        var elapsed = e.ElapsedMs is { } ms && console.Shows(DemoVerbosity.Detailed) ? $"  ({ms.ToString("0", CultureInfo.InvariantCulture)} ms)" : "";
        console.Line(verdict == "fail" ? DemoVerbosity.Quiet : DemoVerbosity.Normal, $"  {symbol} {(StageNames.ReadinessPrefix + stage).PadRight(22)} {summary}{elapsed}", tint);
    }

    private static void PrintStageDetail(DemoConsole console, ReadinessStageResult stage)
    {
        if (stage.Result == ReadinessResult.Fail)
        {
            var block = new StringBuilder(512);
            block.Append("    ").Append(StageNames.ReadinessPrefix).Append(stage.Stage.ToString().ToLowerInvariant()).Append(" FAILED:\n");
            if (stage.Issue is { } issue)
            {
                Field(block, "category", $"{issue.Category} — {IssueText.WhoFixes(issue)}");
                Field(block, "code", issue.Code);
                var codes = IssueText.Codes(issue);
                Field(block, "codes", codes.Length == 0 ? "none" : codes);
                Field(block, "request-id", issue.RequestId ?? "not received");
                if (issue.TaskId is not null || issue.ContextId is not null)
                {
                    Field(block, "task/context", $"{issue.TaskId ?? "(none)"} / {issue.ContextId ?? "(none)"}");
                }

                Field(block, "message", issue.Message);
            }

            Field(block, "meaning", stage.Meaning);
            Field(block, "action", stage.Action);
            console.Line(DemoVerbosity.Quiet, block.ToString().TrimEnd('\n'), Tint.Bad);
        }

        foreach (var warning in stage.Warnings)
        {
            console.Line(DemoVerbosity.Normal, "      warning      " + IssueText.Flatten(warning), Tint.Warn);
        }

        if (stage.Evidence.Count > 0)
        {
            console.Line(DemoVerbosity.Detailed, $"    {StageNames.ReadinessPrefix}{stage.Stage.ToString().ToLowerInvariant()} evidence:", Tint.Dim);
        }

        foreach (var entry in stage.Evidence)
        {
            console.Line(DemoVerbosity.Detailed, $"      {entry.Key}: {IssueText.Flatten(entry.Value)}", Tint.Dim);
        }
    }

    private static void Field(StringBuilder block, string name, string value) =>
        block.Append("      ").Append(name.PadRight(13)).Append(IssueText.Flatten(value)).Append('\n');
}
