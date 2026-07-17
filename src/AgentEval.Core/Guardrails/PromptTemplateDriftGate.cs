// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// Gatekeeper next-wave item — the THIRD application of the <see cref="ManifestFingerprint"/>/
/// <see cref="ManifestDriftDetector"/> primitive (after <c>SkillManifestPoisoningGate</c> for skill
/// manifests and <see cref="McpToolDescriptionPoisoningGate"/> for MCP tool schemas), applied to an agent's
/// prompt template files. Deterministic hash-pin-and-diff, no calibration debt: a prompt template doesn't
/// change mid-run, so this is checked once, at trust-pin time and at load/construction time — not a
/// per-turn gate. See <c>AgentEval.MAF.Gatekeeper.PromptTemplateDriftException</c> for the construction-time
/// wiring into <c>UseGatekeeper</c>.
/// </summary>
public static class PromptTemplateDriftGate
{
    /// <summary>SHA-256 fingerprint of a template's raw content.</summary>
    public static string Fingerprint(string templateContent)
    {
        ArgumentNullException.ThrowIfNull(templateContent);
        return ManifestFingerprint.Hash(templateContent);
    }

    /// <summary>
    /// Checks the current template set against a pinned <paramref name="baselineHashesByName"/> and reports
    /// drift — every template's fingerprint compared against its trust-time pin, plus new/removed templates.
    /// </summary>
    /// <param name="currentTemplatesByName">Key = a caller-chosen template identifier (a file path or logical name); value = the template's raw current content.</param>
    /// <param name="baselineHashesByName">Key = the same template identifier; value = the pinned fingerprint from a prior <see cref="CaptureBaseline"/> call.</param>
    public static IReadOnlyList<ManifestDriftFinding> CheckDrift(
        IReadOnlyDictionary<string, string> currentTemplatesByName,
        IReadOnlyDictionary<string, string> baselineHashesByName)
    {
        ArgumentNullException.ThrowIfNull(currentTemplatesByName);
        ArgumentNullException.ThrowIfNull(baselineHashesByName);

        var current = currentTemplatesByName.ToDictionary(kv => kv.Key, kv => Fingerprint(kv.Value), StringComparer.Ordinal);
        return ManifestDriftDetector.Detect(baselineHashesByName, current);
    }

    /// <summary>Captures a trust-time baseline (template identifier → fingerprint) from the current template set.</summary>
    public static IReadOnlyDictionary<string, string> CaptureBaseline(IReadOnlyDictionary<string, string> templatesByName)
    {
        ArgumentNullException.ThrowIfNull(templatesByName);
        return templatesByName.ToDictionary(kv => kv.Key, kv => Fingerprint(kv.Value), StringComparer.Ordinal);
    }
}
