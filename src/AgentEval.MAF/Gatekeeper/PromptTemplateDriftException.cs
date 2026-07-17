// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Thrown by <c>UseGatekeeper</c> at agent-CONSTRUCTION time (not per-turn — a prompt template doesn't
/// change mid-run) when a prompt template's content has changed since <see cref="GatekeeperOptions.PromptTemplateBaseline"/>
/// was captured — i.e. a possible tamper/rug-pull on a file the agent trusts by construction. Fail-closed:
/// construction refuses to complete, the same pattern as <see cref="UnprotectedHighRiskToolException"/>.
/// Opt-in — only thrown when BOTH <see cref="GatekeeperOptions.PromptTemplates"/> and
/// <see cref="GatekeeperOptions.PromptTemplateBaseline"/> are set.
/// </summary>
public sealed class PromptTemplateDriftException : Exception
{
    /// <summary>
    /// The drifted template(s) that triggered this exception — always <see cref="ManifestDriftKind.Changed"/>
    /// only. A template present in one side but not the other (added/removed) is not treated as drift; see
    /// <see cref="GatekeeperOptions.PromptTemplateBaseline"/> remarks for why.
    /// </summary>
    public IReadOnlyList<ManifestDriftFinding> Findings { get; }

    /// <summary>Creates the exception from the offending <paramref name="findings"/> (must be non-empty, all <see cref="ManifestDriftKind.Changed"/>).</summary>
    public PromptTemplateDriftException(IReadOnlyList<ManifestDriftFinding> findings)
        : base(BuildMessage(findings))
    {
        Findings = findings;
    }

    private static string BuildMessage(IReadOnlyList<ManifestDriftFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var names = string.Join(", ", findings.Select(f => f.Key));
        return $"UseGatekeeper: prompt template drift detected for [{names}] — content changed since " +
               "GatekeeperOptions.PromptTemplateBaseline was captured. This may indicate the prompt file was " +
               "tampered with after being trusted. Re-review the change and re-capture the baseline via " +
               "PromptTemplateDriftGate.CaptureBaseline if it is legitimate, or investigate if it is not.";
    }
}
