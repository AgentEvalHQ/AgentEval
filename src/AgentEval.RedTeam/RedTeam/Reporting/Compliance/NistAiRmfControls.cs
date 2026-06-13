// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// NIST AI RMF (AI 100-1) sub-action mappings relevant to a black-box red-team run (Wave D, §4). Only MEASURE
/// sub-actions a red-team can exercise carry attacks; GOVERN / MAP / MANAGE are listed for traceability with
/// <see cref="ControlFidelity.Governance"/> and no attacks — they can never be marked PASS. <c>OwaspCategories</c>
/// derive from the canonical attack taxonomy (RC-5: derive, don't duplicate).
/// <para>
/// #10 CAVEAT: the MEASURE sub-action IDs, titles, and attack assignments below are a BEST-EFFORT mapping and
/// MUST be verified against the official NIST AI 100-1 / AI RMF Playbook text before audit use. In particular
/// the security-vs-safety split across MEASURE.2.6 / 2.7 and the assignments of 2.9 / 4.2 are unverified here;
/// a tested-Effective verdict on a mis-assigned sub-action would misrepresent an unevaluated official subcategory.
/// </para>
/// </summary>
public static class NistAiRmfControls
{
    /// <summary>Canonical regulation key.</summary>
    public const string Framework = "NIST-AI-RMF";

    /// <summary>All NIST AI RMF control mappings.</summary>
    public static readonly ControlMapping[] All =
    [
        new()
        {
            ControlId = "MEASURE.2.6",
            ControlName = "AI System Robustness & Information Security",
            Description = "AI system is evaluated for security and resilience to adversarial manipulation of inputs, tools, retrieval context, and dependencies.",
            Framework = Framework,
            Fidelity = ControlFidelity.Tested,
            RelevantAttacks = ["PromptInjection", "Jailbreak", "IndirectInjection", "EncodingEvasion", "ExcessiveAgency", "InsecureOutput", "SupplyChain", "DataPoisoning", "VectorEmbedding", "InferenceAPIAbuse"],
        },
        new()
        {
            ControlId = "MEASURE.2.7",
            ControlName = "Adversarial / Red-Team Testing",
            Description = "AI system security and resilience are evaluated via red-teaming (adversarial prompt manipulation, jailbreaks, obfuscation).",
            Framework = Framework,
            Fidelity = ControlFidelity.Tested,
            RelevantAttacks = ["PromptInjection", "Jailbreak", "EncodingEvasion"],
        },
        new()
        {
            ControlId = "MEASURE.2.10",
            ControlName = "Privacy & Data Leakage",
            Description = "AI system is evaluated for disclosure of sensitive/personal information and system-prompt leakage.",
            Framework = Framework,
            Fidelity = ControlFidelity.Tested,
            RelevantAttacks = ["PIILeakage", "SystemPromptExtraction"],
        },
        new()
        {
            ControlId = "MEASURE.2.3",
            ControlName = "Confabulation / Output Reliability",
            Description = "AI system is evaluated for fabrication of authoritative detail about nonexistent entities (a confabulation proxy).",
            Framework = Framework,
            Fidelity = ControlFidelity.Supporting,
            RelevantAttacks = ["Misinformation"],
        },
        new()
        {
            ControlId = "MEASURE.2.9",
            ControlName = "Information Integrity",
            Description = "AI system is evaluated for in-context poisoning susceptibility and adoption of injected false information.",
            Framework = Framework,
            Fidelity = ControlFidelity.Supporting,
            RelevantAttacks = ["DataPoisoning", "Misinformation", "VectorEmbedding"],
        },
        new()
        {
            ControlId = "MEASURE.4.2",
            ControlName = "Value-Chain / Supply-Chain Integrity",
            Description = "AI system is evaluated for uncaveated recommendation of fake/typosquatted packages or models (a supply-chain proxy).",
            Framework = Framework,
            Fidelity = ControlFidelity.Supporting,
            RelevantAttacks = ["SupplyChain"],
        },
        // ── Governance / Map / Manage: traceability only, NEVER PASS (no attack behind them) ──
        new()
        {
            ControlId = "GOVERN.1.1",
            ControlName = "AI Risk Management Policies & Processes",
            Description = "Organizational policies, processes, and accountability for AI risk management. Not testable by a black-box red-team.",
            Framework = Framework,
            Fidelity = ControlFidelity.Governance,
            RelevantAttacks = [],
        },
        new()
        {
            ControlId = "GOVERN.4.1",
            ControlName = "Organizational Risk Culture & Accountability",
            Description = "A culture of risk awareness and accountability across the AI lifecycle. Organizational; not testable at inference time.",
            Framework = Framework,
            Fidelity = ControlFidelity.Governance,
            RelevantAttacks = [],
        },
        new()
        {
            ControlId = "MAP.1.1",
            ControlName = "Context & Intended-Use Mapping",
            Description = "Intended purpose, context, and stakeholders are established and documented. Organizational/lifecycle activity.",
            Framework = Framework,
            Fidelity = ControlFidelity.Governance,
            RelevantAttacks = [],
        },
        new()
        {
            ControlId = "MANAGE.1.3",
            ControlName = "Risk Treatment & Response Planning",
            Description = "AI risks are prioritized, treated, and responded to per organizational policy. Process/governance activity.",
            Framework = Framework,
            Fidelity = ControlFidelity.Governance,
            RelevantAttacks = [],
        },
    ];
}
