// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The banner, printed before anything is sent: the versions, the run folder, the config, which agent and which identity,
/// the plan (readiness, the prompt, the benchmark and its judge), the limits and the screen verbosity; then the live
/// side-effect warning when the run will drive the agent, and the raw-capture warning.
/// </summary>
internal static class DemoBanner
{
    /// <summary>Prints the banner at <see cref="DemoVerbosity.Normal"/> (console.log always keeps it).</summary>
    /// <param name="context">The run's context.</param>
    /// <param name="config">The config as loaded.</param>
    /// <param name="caseSet">The cases the benchmark would run; null when none are loaded.</param>
    /// <param name="judge">The judge of the benchmark's criteria.</param>
    public static void Print(DemoContext context, LoadedConfig config, DemoCaseSet? caseSet, JudgeChoice judge)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(judge);
        var s = context.Settings;
        var c = config.Config;
        var folder = context.Folder;
        var benchmarkPlanned = s.Consent && !s.ReadinessOnly && !s.NoBenchmark;
        var b = new StringBuilder(2048);
        b.Append("Microsoft 365 agent demo — AgentEval ").Append(Summary.DemoVersion).Append(", provider ").Append(WorkIqDiagnostics.AdapterVersion ?? "unknown")
            .Append(", A2A ").Append(WorkIqDiagnostics.A2AVersion).Append('\n');
        Field(b, "run", $"{folder.RunId} → {folder.Root.FullName}");
        Field(b, "config", $"{s.ConfigFile.FullName} ({s.ConfigSource})");
        if (c is null)
        {
            Field(b, "agent", "unknown: the config did not load — " + IssueText.Flatten(config.Error));
            Field(b, "identity", "unknown: the config did not load");
        }
        else
        {
            Field(b, "agent", (string.IsNullOrWhiteSpace(c.AgentId) ? "the default Work IQ agent (Microsoft 365 Copilot)" : c.AgentId!.Trim() + (s.AgentId is null ? " (from the config)" : " (from --agent-id)")) +
                $", shown as \"{c.DisplayName}\"");
            Field(b, "identity", $"tenant {c.TenantId.Trim()}, app {c.AppClientId.Trim()}; you sign in as yourself (device code); the account is shown after sign-in");
            Field(b, "endpoint", $"{c.Endpoint.Trim()} (a token is only ever sent to: {string.Join(", ", c.TrustedHosts)})");
        }

        Field(b, "profile", $"{s.ProfileName} ({s.Profile}){(s.Profile == DiagnosticProfile.Native118Diagnostics ? " — EXPERIMENTAL" : "")}; content policy {s.ContentPolicy}");
        Field(b, "plan", "1. readiness: config, sign-in, agent card, protocol handshake (no prompt is sent)");
        if (s.ReadinessOnly)
        {
            Field(b, "", "stops after readiness (--readiness-only)");
        }
        else if (!s.Consent)
        {
            Field(b, "", $"2. prompt and 3. benchmark: NOT RUN — they need {DemoCommandLine.ConsentFlag} (or {DemoCommandLine.ConsentVariable}=true)");
        }
        else
        {
            Field(b, "", $"2. one prompt: \"{IssueText.Truncate(IssueText.Flatten(s.Prompt), 100)}\"");
            Field(b, "", s.NoBenchmark
                ? "3. benchmark: not run (--no-benchmark)"
                : $"3. benchmark: {(caseSet?.Cases.Count ?? 0).ToString(CultureInfo.InvariantCulture)} cases ({caseSet?.Source}) through MAFEvaluationHarness");
            if (!s.NoBenchmark)
            {
                if (caseSet?.IgnoredFieldsNote is { } ignored)
                {
                    Field(b, "dataset", ignored);
                }

                Field(b, "judge", judge.Description);
            }
        }

        var planned = s.Consent && !s.ReadinessOnly ? 1 + (benchmarkPlanned ? caseSet?.Cases.Count ?? 0 : 0) : 0;
        Field(b, "limits", $"turn cap {s.MaxTurns.ToString(CultureInfo.InvariantCulture)} (this run plans {planned.ToString(CultureInfo.InvariantCulture)} turn(s)); {s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s per stage or turn");
        if (planned > s.MaxTurns)
        {
            Field(b, "", $"the turn cap stops the run after {s.MaxTurns.ToString(CultureInfo.InvariantCulture)} turn(s): {(planned - s.MaxTurns).ToString(CultureInfo.InvariantCulture)} case(s) will be NOT MEASURED");
        }

        Field(b, "screen", $"{s.Verbosity.ToString().ToLowerInvariant()}; console.log in the run folder keeps the full view");
        var console = context.Console;
        console.Line(DemoVerbosity.Normal, b.ToString().TrimEnd('\n'), Tint.Accent);
        if (s.Consent && !s.ReadinessOnly)
        {
            console.Line(DemoVerbosity.Normal,
                "LIVE: the prompt and the benchmark run as the signed-in user. The delegated WorkIQAgent.Ask permission covers reads AND writes " +
                "of that user's Microsoft 365 data, and a prompt can trigger real actions. Consent: " + s.ConsentSource + ". Use a non-production tenant.",
                Tint.Warn);
        }

        if (s.CaptureRaw)
        {
            console.Line(DemoVerbosity.Normal, "raw/ will hold the raw A2A traffic (tenant content): read it before you send it.", Tint.Warn);
        }

        console.Blank();
    }

    private static void Field(StringBuilder b, string name, string value) => b.Append("  ").Append(name.PadRight(10)).Append(value).Append('\n');
}
