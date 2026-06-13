// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// ISO/IEC 42001 (AI management system) control mappings (Wave D, §4). Overwhelmingly governance; only a couple of
/// Annex-A controls are <see cref="ControlFidelity.Supporting"/>. No bespoke reporter — registry rows only.
/// </summary>
public static class Iso42001Controls
{
    /// <summary>Canonical framework key.</summary>
    public const string Framework = "ISO42001";

    /// <summary>All ISO 42001 control mappings.</summary>
    public static readonly ControlMapping[] All =
    [
        new() { ControlId = "A.6.2.4", ControlName = "AI System Verification & Validation", Description = "AI systems are verified and validated, including for security/robustness.", Framework = Framework, Fidelity = ControlFidelity.Supporting, RelevantAttacks = ["PromptInjection", "Jailbreak", "IndirectInjection"] },
        new() { ControlId = "A.9.3", ControlName = "Processes for Responsible AI Use", Description = "Responsible-use processes including handling of misinformation/confabulation.", Framework = Framework, Fidelity = ControlFidelity.Supporting, RelevantAttacks = ["Misinformation"] },
        new() { ControlId = "A.5.2", ControlName = "AI Policy", Description = "Organizational AI policy. Governance.", Framework = Framework, Fidelity = ControlFidelity.Governance, RelevantAttacks = [] },
        new() { ControlId = "A.10.2", ControlName = "Supplier & Third-Party Management", Description = "Management of AI suppliers and third parties. Governance/process.", Framework = Framework, Fidelity = ControlFidelity.Governance, RelevantAttacks = [] },
    ];
}

/// <summary>
/// NIST CSF 2.0 / SP 800-53 technical subcategory mappings (Wave D, §4). <see cref="ControlFidelity.Supporting"/>
/// for detect/protect subcategories a red-team contributes evidence toward. No bespoke reporter — registry rows only.
/// </summary>
public static class CsfControls
{
    /// <summary>Canonical framework key.</summary>
    public const string Framework = "CSF";

    /// <summary>All CSF / 800-53 control mappings.</summary>
    public static readonly ControlMapping[] All =
    [
        new() { ControlId = "PR.DS", ControlName = "Data Security (PR.DS / SC-28, SI-19)", Description = "Data-at-rest/in-transit protection and leakage prevention.", Framework = Framework, Fidelity = ControlFidelity.Supporting, RelevantAttacks = ["PIILeakage", "SystemPromptExtraction", "IndirectInjection", "VectorEmbedding"] },
        new() { ControlId = "PR.PS", ControlName = "Platform Security (PR.PS / SI-10, SI-15)", Description = "Input validation and output handling for downstream components.", Framework = Framework, Fidelity = ControlFidelity.Supporting, RelevantAttacks = ["InsecureOutput", "PromptInjection", "Jailbreak", "EncodingEvasion"] },
        new() { ControlId = "DE.CM", ControlName = "Continuous Monitoring (DE.CM / SC-5, SC-6)", Description = "Detection of anomalous/abusive activity and resource exhaustion.", Framework = Framework, Fidelity = ControlFidelity.Supporting, RelevantAttacks = ["InferenceAPIAbuse"] },
        new() { ControlId = "GV.SC", ControlName = "Cybersecurity Supply Chain (GV.SC / SR-family)", Description = "Supply-chain risk management. Governance.", Framework = Framework, Fidelity = ControlFidelity.Governance, RelevantAttacks = [] },
    ];
}

/// <summary>
/// Unified registry of every framework control mapping (Wave D, §4): SOC2, ISO 27001, NIST AI RMF, ISO 42001, and
/// CSF/800-53. Single point an anti-drift test can validate so a framework row's attacks can never silently diverge
/// from the canonical attack taxonomy. Only NIST has a bespoke reporter; the rest are crosswalk rows.
/// </summary>
public static class FrameworkCrosswalk
{
    /// <summary>All framework control mappings across all frameworks.</summary>
    public static readonly IReadOnlyList<ControlMapping> AllControls =
    [
        .. SOC2Controls.All,
        .. ISO27001Controls.All,
        .. NistAiRmfControls.All,
        .. Iso42001Controls.All,
        .. CsfControls.All,
    ];

    /// <summary>Distinct framework keys present in the crosswalk.</summary>
    public static IReadOnlyList<string> Frameworks =>
        AllControls.Select(c => c.Framework).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
}
