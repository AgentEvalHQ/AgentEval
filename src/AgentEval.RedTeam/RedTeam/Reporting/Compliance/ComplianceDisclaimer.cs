// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// Non-removable honesty disclaimer rendered into every AgentEval compliance report surface (RC-7).
/// AgentEval produces an automated, heuristic, narrow-coverage red-team coverage summary — NOT a formal
/// audit, attestation, or certification. A compile-time constant so it cannot be omitted/overridden.
/// </summary>
public static class ComplianceDisclaimer
{
    /// <summary>Heading used in place of the former "Attestation" label (report-facing rename).</summary>
    public const string Heading = "Automated Coverage Summary";

    /// <summary>Single-sentence inline disclaimer for compact surfaces (PDF, tables).</summary>
    public const string OneLine =
        "Automated, heuristic, narrow-coverage red-team coverage summary — NOT a formal audit, attestation, or certification.";

    /// <summary>Full disclaimer paragraph rendered as a non-removable report footer.</summary>
    public const string Text =
        "DISCLAIMER — This document is an AUTOMATED COVERAGE SUMMARY produced by AgentEval's red-team scanner. " +
        "It reflects the results of a heuristic, intentionally narrow set of automated probes against the system " +
        "named above at a single point in time. It is NOT a formal audit, NOT an attestation, and NOT a " +
        "certification under SOC 2, ISO/IEC 27001, OWASP, MITRE ATLAS, or any other framework, and it does not " +
        "establish compliance with any of them. Control and technique mappings are best-effort cross-references to " +
        "support qualified human reviewers and must be independently validated. No warranty is expressed or implied.";

    /// <summary>Markdown rendering of the non-removable footer (heading + paragraph).</summary>
    public static string ToMarkdown() =>
        $"## {Heading}{Environment.NewLine}{Environment.NewLine}> {Text}{Environment.NewLine}";
}
