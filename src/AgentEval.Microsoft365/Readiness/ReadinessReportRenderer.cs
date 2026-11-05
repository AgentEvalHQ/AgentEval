// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>
/// Renders a <see cref="ReadinessReport"/> as <c>readiness.md</c> (a header, the stage table — stage · result · summary ·
/// what it means · what to do — an evidence section per stage, and what to send back), as <c>readiness.json</c>
/// (schema <see cref="ReadinessReport.SchemaVersion"/>) and as the console view. Pure: no file is written, every
/// string is redacted once more on the way out, and the output is deterministic for a given report.
/// </summary>
public static class ReadinessReportRenderer
{
    /// <summary>The line every rendering ends its file list with.</summary>
    public const string NoTokenStatement =
        "None of these files contains a token, an auth code or the Authorization header.";

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Renders the report as Markdown (LF line endings).</summary>
    /// <param name="r">The report.</param>
    public static string ToMarkdown(ReadinessReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var md = new StringBuilder(4096);
        md.Append("# Microsoft 365 readiness report (`agenteval m365 doctor`)\n\n");
        md.Append("**Result: ").Append(r.Verdict).Append("** — ").Append(Escape(VerdictSentence(r))).Append("\n\n");

        md.Append("| Run | Value |\n|---|---|\n");
        Row(md, "Run id", Code(r.RunId));
        Row(md, "Started (UTC)", Utc(r.StartedAt));
        Row(md, "Ended (UTC)", Utc(r.EndedAt));
        Row(md, "Adapter version", Code(r.AdapterVersion ?? "unknown"));
        Row(md, "A2A version", Code(r.A2AVersion));
        Row(md, "Config SHA-256", r.ConfigSha256 is null ? "not computed (the config did not load)" : Code(r.ConfigSha256));
        Row(md, "OS", Escape(r.Environment.OperatingSystem));
        Row(md, ".NET", Escape(r.Environment.DotNet));
        Row(md, "Machine time zone", Escape(r.Environment.MachineTimeZone));
        md.Append('\n');

        md.Append("## Stages\n\n");
        md.Append("| # | Stage | Result | Summary | What it means | What to do |\n");
        md.Append("|---|---|---|---|---|---|\n");
        foreach (var s in r.Stages)
        {
            md.Append("| ").Append(ReadinessNames.Ordinal(s.Stage).ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(ReadinessNames.Of(s.Stage))
                .Append(" | **").Append(ReadinessNames.Of(s.Result)).Append("**")
                .Append(" | ").Append(Escape(s.Result == ReadinessResult.Skipped ? s.SkipReason ?? s.Summary : s.Summary))
                .Append(" | ").Append(Escape(s.Meaning))
                .Append(" | ").Append(Escape(s.Action))
                .Append(" |\n");
        }

        md.Append("\n## Evidence\n");
        foreach (var s in r.Stages)
        {
            md.Append("\n### ").Append(ReadinessNames.Ordinal(s.Stage).ToString(CultureInfo.InvariantCulture)).Append(". ")
                .Append(ReadinessNames.Of(s.Stage)).Append(" — ").Append(ReadinessNames.Of(s.Result)).Append("\n\n");
            if (s.Result == ReadinessResult.Skipped)
            {
                md.Append("Not run: ").Append(Escape(s.SkipReason ?? s.Summary)).Append(s.SkippedForConsent ? " (skipped for consent)" : "").Append('\n');
                continue;
            }

            if (s.ElapsedMs is { } ms)
            {
                md.Append("Elapsed: ").Append(ms.ToString("0", CultureInfo.InvariantCulture)).Append(" ms\n\n");
            }

            foreach (var (name, value) in s.Evidence)
            {
                md.Append("- **").Append(Escape(name)).Append("**: ").Append(Escape(value)).Append('\n');
            }

            foreach (var warning in s.Warnings)
            {
                md.Append("- ⚠ **Warning**: ").Append(Escape(warning)).Append('\n');
            }

            if (s.Issue is { } issue)
            {
                md.Append("- **Issue**: ").Append(issue.Category.ToString()).Append(" · ").Append(Code(issue.Code));
                if (issue.AadstsCode is not null)
                {
                    md.Append(" · ").Append(Code(issue.AadstsCode));
                }

                if (issue.HttpStatus is { } http)
                {
                    md.Append(" · HTTP ").Append(http.ToString(CultureInfo.InvariantCulture));
                }

                if (issue.RpcErrorCode is { } rpc)
                {
                    md.Append(" · JSON-RPC ").Append(rpc.ToString(CultureInfo.InvariantCulture));
                }

                if (issue.RequestId is not null)
                {
                    md.Append(" · request-id ").Append(Code(issue.RequestId));
                }

                md.Append('\n');
                md.Append("- **Cause**: ").Append(Escape(issue.SuggestedCause)).Append('\n');
            }
        }

        md.Append("\n## What to send back\n\n");
        var n = 1;
        md.Append(n++.ToString(CultureInfo.InvariantCulture)).Append(". The report folder")
            .Append(r.ReportDirectory is null ? " (the folder that holds this file)" : ": " + Code(r.ReportDirectory))
            .Append(" — `readiness.md` and `readiness.json`.\n");
        if (r.TraceFile is not null)
        {
            md.Append(n++.ToString(CultureInfo.InvariantCulture)).Append(". The trace file: ").Append(Code(r.TraceFile)).Append('\n');
        }

        if (r.IssueFile is not null)
        {
            md.Append(n.ToString(CultureInfo.InvariantCulture)).Append(". The issues file: ").Append(Code(r.IssueFile)).Append('\n');
        }

        md.Append('\n').Append(NoTokenStatement).Append(' ')
            .Append("They can contain your tenant id, app id and agent id, the card's URLs, HTTP statuses, error codes, ")
            .Append("`request-id` values and file paths on this machine; with a probe, this report also shows the fixed probe prompt ")
            .Append("and the first 80 characters of the agent's answer (the trace never carries prompts or answers).\n");
        return md.ToString();
    }

    /// <summary>Renders the report as indented JSON (schema <see cref="ReadinessReport.SchemaVersion"/>).</summary>
    /// <param name="r">The report.</param>
    public static string ToJson(ReadinessReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, JsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("schema", ReadinessReport.SchemaVersion);
            w.WriteString("runId", r.RunId);
            w.WriteString("result", r.Verdict);
            w.WriteBoolean("allPassed", r.AllPassed);
            w.WriteBoolean("anyFailed", r.AnyFailed);
            w.WriteBoolean("anySkippedForConsent", r.AnySkippedForConsent);
            w.WriteString("startedAt", r.StartedAt);
            w.WriteString("endedAt", r.EndedAt);
            WriteOptional(w, "traceFile", r.TraceFile);
            WriteOptional(w, "issueFile", r.IssueFile);
            WriteOptional(w, "reportDirectory", r.ReportDirectory);
            WriteOptional(w, "configSha256", r.ConfigSha256);
            WriteOptional(w, "adapterVersion", r.AdapterVersion);
            w.WriteString("a2aVersion", r.A2AVersion);
            w.WriteStartObject("environment");
            w.WriteString("operatingSystem", Redact(r.Environment.OperatingSystem));
            w.WriteString("dotNet", Redact(r.Environment.DotNet));
            w.WriteString("machineTimeZone", Redact(r.Environment.MachineTimeZone));
            w.WriteEndObject();

            w.WriteStartArray("stages");
            foreach (var s in r.Stages)
            {
                w.WriteStartObject();
                w.WriteString("stage", ReadinessNames.Of(s.Stage));
                w.WriteNumber("ordinal", ReadinessNames.Ordinal(s.Stage));
                w.WriteString("result", ReadinessNames.Of(s.Result));
                w.WriteBoolean("requested", s.Requested);
                w.WriteString("summary", Redact(s.Summary));
                w.WriteString("meaning", Redact(s.Meaning));
                w.WriteString("action", Redact(s.Action));
                if (s.ElapsedMs is { } ms)
                {
                    w.WriteNumber("elapsedMs", Math.Round(ms, 1));
                }

                WriteOptional(w, "skipReason", s.SkipReason);
                w.WriteBoolean("skippedForConsent", s.SkippedForConsent);
                w.WriteStartArray("warnings");
                foreach (var warning in s.Warnings)
                {
                    w.WriteStringValue(Redact(warning));
                }

                w.WriteEndArray();
                w.WriteStartArray("evidence");
                foreach (var (name, value) in s.Evidence)
                {
                    w.WriteStartObject();
                    w.WriteString("name", Redact(name));
                    w.WriteString("value", Redact(value));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                if (s.Issue is { } issue)
                {
                    w.WritePropertyName("issue");
                    JsonSerializer.SerializeToElement(issue, WorkIqDiagnosticsJson.Options).WriteTo(w);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("noTokenStatement", NoTokenStatement);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>
    /// Writes the console view: one line per stage (<c>[PASS]</c>, <c>[FAIL]</c>, <c>[SKIPPED]</c>) with its summary,
    /// what it means and what to do for a failed stage, the warnings, and the overall verdict.
    /// </summary>
    /// <param name="r">The report.</param>
    /// <param name="w">The destination.</param>
    public static void WriteConsole(ReadinessReport r, TextWriter w)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(w);
        w.WriteLine();
        w.WriteLine($"Microsoft 365 readiness (m365 doctor) — run {r.RunId}");
        foreach (var s in r.Stages)
        {
            var tag = "[" + ReadinessNames.Of(s.Result) + "]";
            var text = s.Result == ReadinessResult.Skipped ? s.SkipReason ?? s.Summary : s.Summary;
            w.WriteLine($"  {tag,-10}{ReadinessNames.Of(s.Stage),-10} {Redact(text)}");
            if (s.Result == ReadinessResult.Fail)
            {
                if (s.Issue is { } issue)
                {
                    w.WriteLine($"{"",12}{"",-10} issue:   {issue.Category} {issue.Code}{(issue.AadstsCode is null ? "" : " " + issue.AadstsCode)}{(issue.HttpStatus is { } h ? " HTTP " + h.ToString(CultureInfo.InvariantCulture) : "")}{(issue.RpcErrorCode is { } c ? " JSON-RPC " + c.ToString(CultureInfo.InvariantCulture) : "")}");
                }

                w.WriteLine($"{"",12}{"",-10} meaning: {Redact(s.Meaning)}");
                w.WriteLine($"{"",12}{"",-10} do:      {Redact(s.Action)}");
            }

            foreach (var warning in s.Warnings)
            {
                w.WriteLine($"{"",12}{"",-10} warning: {Redact(warning)}");
            }
        }

        w.WriteLine();
        w.WriteLine($"Result: {r.Verdict} — {VerdictSentence(r)}");
        w.Flush();
    }

    private static string VerdictSentence(ReadinessReport r)
    {
        var failed = r.Stages.Where(s => s.Result == ReadinessResult.Fail).Select(s => ReadinessNames.Of(s.Stage)).ToArray();
        if (failed.Length > 0)
        {
            return $"stage(s) {string.Join(", ", failed)} failed; the table says why and what to do.";
        }

        if (r.AllPassed)
        {
            return "every requested stage passed.";
        }

        var consent = r.Stages.Where(s => s.SkippedForConsent).Select(s => ReadinessNames.Of(s.Stage)).ToArray();
        if (consent.Length > 0)
        {
            return $"nothing failed, but {string.Join(" and ", consent)} did not run: {ReadinessStageResult.ConsentSkipReason}.";
        }

        var notRun = r.Stages.Where(s => s.Requested && s.Result == ReadinessResult.Skipped).Select(s => ReadinessNames.Of(s.Stage)).ToArray();
        return notRun.Length > 0
            ? $"nothing failed, but requested stage(s) {string.Join(", ", notRun)} did not run."
            : "no stage ran.";
    }

    private static void Row(StringBuilder md, string name, string value) =>
        md.Append("| ").Append(name).Append(" | ").Append(value).Append(" |\n");

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, Redact(value));
        }
    }

    private static string Utc(DateTimeOffset value) =>
        value == default ? "not recorded" : value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Inline code for an identifier or path, falling back to escaped text when it contains a backtick.</summary>
    private static string Code(string value)
    {
        var redacted = Redact(value);
        return redacted.Contains('`', StringComparison.Ordinal) ? Escape(redacted) : "`" + OneLine(redacted) + "`";
    }

    /// <summary>Makes text safe inside a Markdown table cell or list item: one line, pipes escaped, redacted.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return OneLine(Redact(value)).Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string OneLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    private static string Redact(string value) => WorkIqRedaction.Redact(value);
}
