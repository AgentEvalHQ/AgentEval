// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Controls whether a report carries raw attack text, so results can be published without publishing a
/// working attack corpus.
/// </summary>
/// <remarks>
/// <para>Every shipped exporter embeds the probe <c>Prompt</c> and <c>Response</c> verbatim. That is correct
/// for a local triage artifact and wrong for a published one: a SARIF file uploaded to code scanning, or a
/// JSON report attached to an issue, hands the reader a working corpus of prompts that got through. The
/// reference prompt-injection benchmark solves this by emitting metadata-only artifacts
/// (<c>raw_text_in_output: false</c>) — findings, counts and rates, but no payloads.</para>
///
/// <para><b>The default is unchanged (<see cref="IncludeAttackText"/> = <see langword="true"/>)</b> so no
/// existing caller silently loses evidence it depends on. Publication paths opt in to
/// <see cref="MetadataOnly"/>.</para>
///
/// <para><b>Redaction is not deletion.</b> A redacted field is replaced with an explicit marker, never
/// dropped, so a reader can tell "withheld" from "there was nothing here" — the same distinction the rest of
/// this codebase draws between a coverage gap and a clean result.</para>
/// </remarks>
/// <param name="IncludeAttackText">
/// When <see langword="false"/>, probe prompts and responses are replaced with <see cref="RedactedMarker"/>.
/// Counts, verdicts, severities, techniques, fidelity tiers and rates are always retained — redaction removes
/// payloads, not findings.
/// </param>
public sealed record ReportRedaction(bool IncludeAttackText = true)
{
    /// <summary>The placeholder substituted for withheld attack text.</summary>
    public const string RedactedMarker = "[redacted: metadata-only report]";

    /// <summary>Full-fidelity local artifact. The default, preserving existing behaviour.</summary>
    public static ReportRedaction Full { get; } = new(IncludeAttackText: true);

    /// <summary>
    /// Publication-safe artifact: findings, counts and rates, but no attack payloads. Equivalent to the
    /// reference benchmark's <c>raw_text_in_output: false</c>.
    /// </summary>
    public static ReportRedaction MetadataOnly { get; } = new(IncludeAttackText: false);

    /// <summary>
    /// Returns <paramref name="text"/> unchanged, or the redaction marker when attack text is withheld.
    /// A <see langword="null"/> input stays <see langword="null"/>: absent is not the same as withheld.
    /// </summary>
    public string? Apply(string? text)
    {
        if (IncludeAttackText || text is null)
        {
            return text;
        }

        return RedactedMarker;
    }
}
