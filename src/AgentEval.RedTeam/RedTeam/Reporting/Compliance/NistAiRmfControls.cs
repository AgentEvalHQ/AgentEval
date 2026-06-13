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
/// H14 (verified 2026-06-13 against NIST AI 100-1 AI RMF 1.0 Core, nvlpubs.nist.gov/nistpubs/ai/nist.ai.100-1.pdf):
/// the prior best-effort mapping mis-assigned subcategories. Corrections: <b>MEASURE 2.6 is SAFETY</b> ("evaluated
/// regularly for safety risks … can fail safely") — a black-box red-team does NOT assess it, so it carries no
/// attacks; <b>MEASURE 2.7 is SECURITY AND RESILIENCE</b> — the home for ALL adversarial-robustness attacks (prompt
/// injection, jailbreak, indirect/encoding, excessive agency, insecure output, supply-chain, data poisoning,
/// vector/RAG, inference abuse); red-teaming is the evaluation METHOD for 2.7, not a separate subcategory. The
/// mislabeled MEASURE 2.9 (which is explainability/interpretability) and MEASURE 4.2 (domain-expert validation) were
/// removed; their evidence folds into 2.7 (security) or 2.5 (validity/reliability). Supply-chain governance is
/// surfaced via GOVERN.6.1 (third-party risk), a no-attack governance row.
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
            ControlId = "MEASURE.2.7",
            ControlName = "Security and Resilience",
            Description = "AI system security and resilience are evaluated (and documented) via red-teaming — adversarial manipulation of inputs, tools, retrieval context, and dependencies (prompt injection, jailbreak, obfuscation, supply-chain, poisoning, inference abuse).",
            Framework = Framework,
            Fidelity = ControlFidelity.Tested,
            RelevantAttacks = ["PromptInjection", "Jailbreak", "IndirectInjection", "EncodingEvasion", "ExcessiveAgency", "InsecureOutput", "SupplyChain", "DataPoisoning", "VectorEmbedding", "InferenceAPIAbuse"],
        },
        new()
        {
            ControlId = "MEASURE.2.10",
            ControlName = "Privacy Risk",
            Description = "Privacy risk of the AI system is examined and documented — disclosure of sensitive/personal information and system-prompt leakage.",
            Framework = Framework,
            Fidelity = ControlFidelity.Tested,
            RelevantAttacks = ["PIILeakage", "SystemPromptExtraction"],
        },
        new()
        {
            ControlId = "MEASURE.2.5",
            ControlName = "Validity and Reliability",
            Description = "AI system is demonstrated valid and reliable — here, a confabulation/output-reliability proxy (fabrication of authoritative detail about nonexistent entities).",
            Framework = Framework,
            Fidelity = ControlFidelity.Supporting,
            RelevantAttacks = ["Misinformation"],
        },
        new()
        {
            ControlId = "MEASURE.2.6",
            ControlName = "Safety",
            Description = "AI system is evaluated regularly for safety risks and can fail safely. A black-box conversational red-team does NOT assess fail-safe/safety behaviour — listed for traceability with no attacks (never PASS).",
            Framework = Framework,
            Fidelity = ControlFidelity.Governance,
            RelevantAttacks = [],
        },
        // ── Governance / Map / Manage: traceability only, NEVER PASS (no attack behind them) ──
        new()
        {
            ControlId = "GOVERN.6.1",
            ControlName = "Third-Party / Supply-Chain Risk Policies",
            Description = "Policies and procedures address AI risks arising from third-party software, data, and supply-chain. Process/governance; the SupplyChain attack provides supporting evidence but the control itself is not testable at inference time.",
            Framework = Framework,
            Fidelity = ControlFidelity.Governance,
            RelevantAttacks = [],
        },
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
