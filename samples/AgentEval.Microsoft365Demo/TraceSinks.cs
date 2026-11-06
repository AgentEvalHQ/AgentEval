// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The demo's trace sink, handed to the provider through <c>ReadinessCheckOptions.TraceSinks</c> and
/// <c>WorkIqOptions.TraceSinks</c>: it receives every event the JSONL trace file receives (stamped and redacted by the
/// provider), shows it on the screen at <see cref="DemoVerbosity.Detailed"/> (the provider's one-line format, without the
/// data payload and timing) or <see cref="DemoVerbosity.Diagnostic"/> (the full line), and always writes the full line to
/// console.log. It never throws: a sink that throws would be dropped by the provider for the rest of the run.
/// </summary>
internal sealed class DemoTraceSink : IWorkIqTraceSink
{
    private readonly DemoConsole _console;

    /// <summary>Creates the sink.</summary>
    /// <param name="console">The demo console.</param>
    public DemoTraceSink(DemoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Called with every readiness verdict event (<c>doctor.&lt;stage&gt;.pass|fail|skipped</c>) after the event's own line:
    /// the event, the stage name, the verdict (<c>pass</c>, <c>fail</c>, <c>skipped</c>) and whether the stage was
    /// requested. Lets the host print the stage line as the stage ends instead of after the whole check.
    /// </summary>
    public Action<WorkIqTraceEvent, string, string, bool>? OnStage { get; init; }

    /// <inheritdoc/>
    public void Write(WorkIqTraceEvent e)
    {
        try
        {
            WriteLine(e);
        }
        catch (Exception)
        {
            // Never let a display problem drop the sink: the JSONL trace file has the event either way.
        }

        try
        {
            if (OnStage is { } onStage && TryReadStageVerdict(e, out var stage, out var verdict, out var requested))
            {
                onStage(e, stage, verdict, requested);
            }
        }
        catch (Exception)
        {
            // The stage table is printed from the readiness report as well; a display problem here loses nothing.
        }
    }

    /// <summary>
    /// Reads a readiness verdict event: the stage and verdict from its documented trace code
    /// <c>doctor.&lt;stage&gt;.pass|fail|skipped</c>, and its <c>requested</c> flag from the event's data.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <param name="stage">The stage name.</param>
    /// <param name="verdict">The verdict.</param>
    /// <param name="requested">Whether the stage was requested (true when the event does not say).</param>
    internal static bool TryReadStageVerdict(WorkIqTraceEvent e, out string stage, out string verdict, out bool requested)
    {
        stage = verdict = "";
        requested = true;
        if (e.Stage != WorkIqTraceStage.Doctor)
        {
            return false;
        }

        var parts = e.Code.Split('.');
        if (parts.Length != 3 || parts[0] != "doctor" || parts[2] is not ("pass" or "fail" or "skipped"))
        {
            return false;
        }

        stage = parts[1];
        verdict = parts[2];
        if (e.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
            && data.TryGetProperty("requested", out var flag)
            && flag.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            requested = flag.GetBoolean();
        }

        return true;
    }

    private void WriteLine(WorkIqTraceEvent e)
    {
        var full = ConsoleTraceSink.Format(e);
        string? screen = null;
        if (_console.Shows(DemoVerbosity.Diagnostic))
        {
            screen = full;
        }
        else if (_console.Shows(DemoVerbosity.Detailed))
        {
            screen = ConsoleTraceSink.Format(e with { Data = null, ElapsedMs = null });
        }

        var tint = e.Level switch
        {
            WorkIqTraceLevel.Error => Tint.Bad,
            WorkIqTraceLevel.Warning => Tint.Warn,
            _ => Tint.Dim,
        };
        _console.Trace(screen, full, tint);
    }
}

/// <summary>
/// The demo's issue sink (<c>ReadinessCheckOptions.IssueSinks</c>, <c>WorkIqOptions.IssueSinks</c>): it keeps every issue
/// for the summary, shows one line per issue at <see cref="DemoVerbosity.Normal"/> and the detail block (stage, category,
/// code, AADSTS/HTTP/JSON-RPC codes, request-id, task/context ids, message, cause, action) at
/// <see cref="DemoVerbosity.Detailed"/>, and always writes the detail block to console.log. The screen shows each distinct
/// issue (stage + code + message) once: a repeat is counted, not printed again, and <see cref="RepeatedIssuesText"/> lists
/// the counts (<c>×N</c>) at the end; console.log and issues.jsonl keep every occurrence. A readiness stage's own verdict
/// issue (<c>doctor.&lt;stage&gt;.failed</c>) is one line even at detailed: the stage's FAILED block repeats it in full.
/// It never throws.
/// </summary>
internal sealed class DemoIssueSink : IWorkIqIssueSink
{
    private readonly DemoConsole _console;
    private readonly List<WorkIqIssue> _issues = [];
    private readonly Dictionary<(WorkIqTraceStage Stage, string Code, string Message), (WorkIqIssue First, int Count)> _distinct = [];
    private readonly object _gate = new();

    /// <summary>Creates the sink.</summary>
    /// <param name="console">The demo console.</param>
    public DemoIssueSink(DemoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>Every issue received so far, in arrival order (a snapshot).</summary>
    public IReadOnlyList<WorkIqIssue> Issues
    {
        get
        {
            lock (_gate)
            {
                return _issues.ToArray();
            }
        }
    }

    /// <summary>
    /// The issues the screen showed once but that occurred more than once, one line each with its count
    /// (<c>! Category code (Stage) ×N — message</c>); null when none repeated or the screen shows no issues (quiet).
    /// </summary>
    public string? RepeatedIssuesText
    {
        get
        {
            if (!_console.Shows(DemoVerbosity.Normal))
            {
                return null;
            }

            KeyValuePair<(WorkIqTraceStage Stage, string Code, string Message), (WorkIqIssue First, int Count)>[] repeated;
            lock (_gate)
            {
                repeated = _distinct.Where(d => d.Value.Count > 1).ToArray();
            }

            if (repeated.Length == 0)
            {
                return null;
            }

            var text = new StringBuilder(256).Append("Repeated issues (each shown once above; console.log and issues.jsonl keep every occurrence):");
            foreach (var (_, (first, count)) in repeated)
            {
                text.Append('\n').Append(IssueText.Counted(first, count));
            }

            return text.ToString();
        }
    }

    /// <inheritdoc/>
    public void Write(WorkIqIssue i)
    {
        try
        {
            int occurrence;
            lock (_gate)
            {
                _issues.Add(i);
                var key = (i.Stage, i.Code, IssueText.Flatten(i.Message));
                occurrence = _distinct.TryGetValue(key, out var seen) ? seen.Count + 1 : 1;
                _distinct[key] = (occurrence == 1 ? i : seen.First, occurrence);
            }

            var block = IssueText.DetailBlock(i);
            string? screen;
            if (occurrence > 1 || !_console.Shows(DemoVerbosity.Normal))
            {
                // A repeat (or quiet): console.log gets the block, the screen nothing; the count comes at the end.
                screen = null;
            }
            else if (_console.Shows(DemoVerbosity.Detailed) && !IssueText.IsStageVerdict(i))
            {
                screen = block;
            }
            else
            {
                screen = IssueText.OneLine(i) + (_console.Shows(DemoVerbosity.Detailed) ? " (the stage's FAILED block has the detail)" : "");
            }

            _console.Trace(screen, block, Tint.Warn);
        }
        catch (Exception)
        {
            // Never let a display problem drop the sink: the JSONL issue file has the issue either way.
        }
    }
}

/// <summary>
/// The heading an issue is grouped under in summary.md and the final table: a judge issue under "Judge" (the judge's
/// provider settings fix it), every other issue under its category.
/// </summary>
/// <param name="Name">The heading (<c>Judge</c>, or the category name).</param>
/// <param name="Order">The sort order: the categories in their declared order, then the judge.</param>
/// <param name="WhoFixes">Who or what can fix the issues of this group.</param>
internal sealed record IssueGroup(string Name, int Order, string WhoFixes);

/// <summary>How an issue is shown: the one-liner, the detail block, and the plain-language cause and action.</summary>
internal static class IssueText
{
    /// <summary>
    /// The separator the provider's readiness check puts between what a failure means and what to do. The provider's
    /// issue has one text field for both; the demo splits it here and nowhere else.
    /// </summary>
    private const string WhatToDo = " What to do: ";

    /// <summary>The separator of the provider's AADSTS catalogue form (<c>code: cause → fix</c>); split here and nowhere else.</summary>
    private const string Arrow = " → ";

    /// <summary>Who fixes a judge issue.</summary>
    private const string JudgeWhoFixes = "the judge model's provider settings (AI_INFERENCE_PROVIDER and its variables), not the agent or Work IQ";

    /// <summary>Whether <paramref name="i"/> is about the judge (<c>judge.*</c>), not the agent, Work IQ or the sign-in.</summary>
    /// <param name="i">The issue.</param>
    public static bool IsJudge(WorkIqIssue i) => i.Code.StartsWith("judge.", StringComparison.Ordinal);

    /// <summary>The group <paramref name="i"/> is listed under: <c>Judge</c> for a judge issue, else its category.</summary>
    /// <param name="i">The issue.</param>
    public static IssueGroup GroupOf(WorkIqIssue i) => IsJudge(i)
        ? new IssueGroup("Judge", int.MaxValue, JudgeWhoFixes)
        : new IssueGroup(i.Category.ToString(), (int)i.Category, WhoFixes(i.Category));

    /// <summary>One line: <c>! issue Category code (Stage) HTTP … JSON-RPC … AADSTS… req=… — message</c>.</summary>
    /// <param name="i">The issue.</param>
    public static string OneLine(WorkIqIssue i)
    {
        var line = new StringBuilder(200)
            .Append("  ! issue ").Append(i.Category).Append(' ').Append(i.Code).Append(" (").Append(i.Stage).Append(')');
        var codes = Codes(i);
        if (codes.Length > 0)
        {
            line.Append(' ').Append(codes);
        }

        if (!string.IsNullOrWhiteSpace(i.RequestId))
        {
            line.Append(" request-id=").Append(i.RequestId);
        }

        if (!string.IsNullOrWhiteSpace(i.CaseId))
        {
            line.Append(" case=").Append(i.CaseId);
        }

        line.Append(" — ").Append(Truncate(Flatten(i.Message), 160));
        return line.ToString();
    }

    /// <summary>One line with the count of a repeated issue: <c>! Category code (Stage) ×N — message</c>.</summary>
    /// <param name="i">The issue (its first occurrence).</param>
    /// <param name="count">How many times it occurred.</param>
    public static string Counted(WorkIqIssue i, int count)
    {
        var line = new StringBuilder(200)
            .Append("  ! ").Append(i.Category).Append(' ').Append(i.Code).Append(" (").Append(i.Stage).Append(") ×")
            .Append(count.ToString(CultureInfo.InvariantCulture));
        var codes = Codes(i);
        if (codes.Length > 0)
        {
            line.Append(' ').Append(codes);
        }

        line.Append(" — ").Append(Truncate(Flatten(i.Message), 160));
        return line.ToString();
    }

    /// <summary>Whether <paramref name="i"/> is a readiness stage's own verdict issue (<c>doctor.&lt;stage&gt;.failed</c>), which the stage's FAILED block repeats in full.</summary>
    /// <param name="i">The issue.</param>
    public static bool IsStageVerdict(WorkIqIssue i) =>
        i.Stage == WorkIqTraceStage.Doctor && i.Code.StartsWith("doctor.", StringComparison.Ordinal) && i.Code.EndsWith(".failed", StringComparison.Ordinal);

    /// <summary>Who or what can fix <paramref name="i"/>: the judge's provider for a judge issue, else by its category.</summary>
    /// <param name="i">The issue.</param>
    public static string WhoFixes(WorkIqIssue i) => GroupOf(i).WhoFixes;

    /// <summary>The detail block of one issue (several lines, each field only when known).</summary>
    /// <param name="i">The issue.</param>
    public static string DetailBlock(WorkIqIssue i)
    {
        var (cause, action) = CauseAndAction(i);
        var block = new StringBuilder(512);
        block.Append("  ! ISSUE ").Append(i.Category).Append(' ').Append(i.Code).Append('\n');
        Field(block, "stage", i.Stage.ToString());
        Field(block, "category", $"{i.Category} — {WhoFixes(i)}");
        Field(block, "code", i.Code);
        var codes = Codes(i);
        Field(block, "codes", codes.Length == 0 ? "none (no HTTP status, JSON-RPC code or AADSTS code)" : codes);
        Field(block, "request-id", i.RequestId ?? "not received");
        if (i.TaskId is not null || i.ContextId is not null)
        {
            Field(block, "task/context", $"{i.TaskId ?? "(none)"} / {i.ContextId ?? "(none)"}");
        }

        if (i.CaseId is not null || i.TurnId is not null)
        {
            Field(block, "case/turn", $"{i.CaseId ?? "(none)"} / {i.TurnId ?? "(none)"}");
        }

        if (i.A2AState is not null)
        {
            Field(block, "A2A state", i.A2AState);
        }

        Field(block, "message", Flatten(i.Message));
        Field(block, "cause", cause);
        Field(block, "action", action);
        if (i.RunId is not null)
        {
            Field(block, "run", i.RunId + (i.At == default ? "" : " at " + i.At.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC"));
        }

        return block.ToString().TrimEnd('\n');
    }

    /// <summary>The HTTP status, JSON-RPC code and AADSTS code that are known, as one string.</summary>
    /// <param name="i">The issue.</param>
    public static string Codes(WorkIqIssue i)
    {
        var parts = new List<string>(3);
        if (i.HttpStatus is { } http)
        {
            parts.Add("HTTP " + http.ToString(CultureInfo.InvariantCulture));
        }

        if (i.RpcErrorCode is { } rpc)
        {
            parts.Add("JSON-RPC " + rpc.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(i.AadstsCode))
        {
            parts.Add(i.AadstsCode!);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The cause and the action of an issue. A judge issue is the demo's own: both come from <see cref="JudgeFailure"/>.
    /// A provider issue carries both in one text field: the readiness check's "meaning. What to do: action" and the
    /// AADSTS catalogue's "code: cause → fix" are split; otherwise the suggested cause is the cause and the action comes
    /// from the category (who can fix it).
    /// </summary>
    /// <param name="i">The issue.</param>
    public static (string Cause, string Action) CauseAndAction(WorkIqIssue i)
    {
        if (IsJudge(i))
        {
            return (JudgeFailure.CauseFor(i.HttpStatus), JudgeFailure.ActionFor(i.HttpStatus));
        }

        var suggested = Flatten(i.SuggestedCause);
        var at = suggested.IndexOf(WhatToDo, StringComparison.Ordinal);
        if (at >= 0)
        {
            return (suggested[..at].Trim(), suggested[(at + WhatToDo.Length)..].Trim());
        }

        at = suggested.IndexOf(Arrow, StringComparison.Ordinal);
        if (at >= 0)
        {
            return (suggested[..at].Trim(), suggested[(at + Arrow.Length)..].Trim());
        }

        return (suggested.Length == 0 ? "not known (the message says what was observed)" : suggested, ActionFor(i.Category));
    }

    /// <summary>Who or what can fix an issue of <paramref name="category"/>.</summary>
    /// <param name="category">The category.</param>
    public static string WhoFixes(WorkIqIssueCategory category) => category switch
    {
        WorkIqIssueCategory.Config => "your config file or the options",
        WorkIqIssueCategory.Authentication => "the sign-in (you, or the app registration)",
        WorkIqIssueCategory.Permission => "an Entra / Microsoft 365 administrator (consent, licence, Work IQ enabled)",
        WorkIqIssueCategory.Network => "the network between this machine and Work IQ",
        WorkIqIssueCategory.Protocol => "the connector maintainers (the reply is not the A2A 1.0 shape)",
        WorkIqIssueCategory.Service => "the Work IQ service (note the request-id)",
        WorkIqIssueCategory.Capture => "the raw capture settings (the run itself is unaffected)",
        WorkIqIssueCategory.Budget => "the run's limits (--max-turns, --timeout-seconds)",
        WorkIqIssueCategory.Case => "the benchmark case (the agent paused for a confirmation)",
        _ => "not classified",
    };

    /// <summary>What to do about an issue of <paramref name="category"/> when its cause carries no action of its own.</summary>
    /// <param name="category">The category.</param>
    public static string ActionFor(WorkIqIssueCategory category) => category switch
    {
        WorkIqIssueCategory.Config =>
            "Fix the field the message names in your config file (start from samples/datasets/m365-config.template.json) and run again.",
        WorkIqIssueCategory.Authentication =>
            "Complete the sign-in when the prompt appears; check tenantId and appClientId against the app registration's Overview page and that 'Allow public client flows' is on.",
        WorkIqIssueCategory.Permission =>
            "Ask an administrator to grant admin consent for the delegated WorkIQAgent.Ask permission, and check the user's Microsoft 365 Copilot licence and that Work IQ is enabled.",
        WorkIqIssueCategory.Network =>
            "Check the network, proxy or VPN to workiq.svc.cloud.microsoft and run again; a timed-out prompt may have reached the service, so do not resend it blindly.",
        WorkIqIssueCategory.Protocol =>
            "Send the run folder back: the service answered in a shape this connector does not expect.",
        WorkIqIssueCategory.Service =>
            "Run again later; if it repeats, send the run folder back (the request-id lets the service owner find the call).",
        WorkIqIssueCategory.Capture =>
            "Nothing for the run itself; raw/ may be incomplete. Send the folder back if the maintainers asked for raw/.",
        WorkIqIssueCategory.Budget =>
            "Raise --max-turns or --timeout-seconds only if you meant the run to go further; otherwise send the run folder back.",
        WorkIqIssueCategory.Case =>
            "Rephrase the case so it asks for information only. Nothing was approved.",
        _ => "Send the run folder back.",
    };

    /// <summary>Text on one line.</summary>
    /// <param name="text">The text.</param>
    public static string Flatten(string? text) =>
        (text ?? "").Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Text cut to <paramref name="max"/> characters with an ellipsis.</summary>
    /// <param name="text">The text.</param>
    /// <param name="max">The most characters kept.</param>
    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static void Field(StringBuilder block, string name, string value) =>
        block.Append("      ").Append(name.PadRight(13)).Append(Flatten(value)).Append('\n');
}
