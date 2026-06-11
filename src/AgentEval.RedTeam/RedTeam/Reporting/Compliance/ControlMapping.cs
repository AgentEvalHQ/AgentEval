// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;

namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// Defines a control mapping between security frameworks and red team attacks.
/// </summary>
public record ControlMapping
{
    /// <summary>Control ID (e.g., "CC6.1", "A.5.15").</summary>
    public required string ControlId { get; init; }

    /// <summary>Control name.</summary>
    public required string ControlName { get; init; }

    /// <summary>Control description.</summary>
    public required string Description { get; init; }

    /// <summary>Framework this control belongs to.</summary>
    public required string Framework { get; init; }

    /// <summary>Attack types that test this control.</summary>
    public required string[] RelevantAttacks { get; init; }

    /// <summary>
    /// Honesty taxonomy (§4): how strongly a red-team run substantiates this control. <see cref="ControlFidelity.Tested"/>
    /// (real pass/fail evidence), <see cref="ControlFidelity.Supporting"/> (partial/contributory — capped at
    /// PartiallyEffective even at 100% pass), or <see cref="ControlFidelity.Governance"/> (organizational control with
    /// no attack behind it — listed for traceability, NEVER PASS). Default <see cref="ControlFidelity.Tested"/>.
    /// </summary>
    public ControlFidelity Fidelity { get; init; } = ControlFidelity.Tested;

    private readonly string[]? _owaspCategories;

    /// <summary>
    /// OWASP LLM categories that map to this control. "Derive, don't duplicate" (RC-5): when not
    /// explicitly supplied, projected from the canonical <see cref="IAttackType.OwaspLlmId"/> of each
    /// entry in <see cref="RelevantAttacks"/> via <see cref="ControlMappingCrosswalk.DeriveOwaspCategories"/>,
    /// so the crosswalk can never drift from the attack classes.
    /// </summary>
    public string[] OwaspCategories
    {
        get => _owaspCategories ?? ControlMappingCrosswalk.DeriveOwaspCategories(RelevantAttacks);
        init => _owaspCategories = value;
    }
}

/// <summary>
/// Crosswalk helpers that project framework control mappings from the canonical attack taxonomy (RC-5).
/// Single source of truth: <see cref="Attack.ByName(string)"/>.
/// </summary>
public static class ControlMappingCrosswalk
{
    /// <summary>
    /// Projects the distinct, sorted set of OWASP LLM Top 10 ids covered by the supplied attacks, reading
    /// <see cref="IAttackType.OwaspLlmId"/> via <see cref="Attack.ByName(string)"/>. Unknown names are skipped.
    /// </summary>
    public static string[] DeriveOwaspCategories(IEnumerable<string> relevantAttacks)
    {
        ArgumentNullException.ThrowIfNull(relevantAttacks);
        return relevantAttacks
            .Select(Attack.ByName)
            .Where(a => a is not null)
            .Select(a => a!.OwaspLlmId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>
/// Honesty taxonomy for a mapped control (§4 framework-mapping discipline): how strongly a red-team run
/// substantiates the control. Keeps governance/organizational controls from ever being marked PASS.
/// </summary>
public enum ControlFidelity
{
    /// <summary>A red-team attack directly exercises this control — real pass/fail evidence.</summary>
    Tested,

    /// <summary>Partial/contributory evidence; the control is broader than what we probe. Capped at PartiallyEffective.</summary>
    Supporting,

    /// <summary>Organizational/governance control (policy/lifecycle/oversight). Listed for traceability; NEVER PASS.</summary>
    Governance,
}

/// <summary>
/// Status of a single compliance control.
/// </summary>
public class ControlStatus
{
    /// <summary>Control mapping definition.</summary>
    public required ControlMapping Control { get; init; }

    /// <summary>Evaluation status.</summary>
    public ControlEvaluationStatus Status { get; init; }

    /// <summary>Total tests that apply to this control.</summary>
    public int TotalTests { get; init; }

    /// <summary>
    /// Tests that produced a CONCLUSIVE verdict (Resisted or Succeeded). Inconclusive probes are excluded so
    /// they weaken <see cref="Coverage"/> rather than failing the control (N-03 / RC-6). When left unset
    /// (sentinel -1), posture falls back to <see cref="TotalTests"/> so older call-sites are unchanged.
    /// </summary>
    public int ConclusiveTests { get; init; } = -1;

    private int EffectiveConclusive => ConclusiveTests >= 0 ? ConclusiveTests : TotalTests;

    /// <summary>Tests that passed.</summary>
    public int PassedTests { get; init; }

    /// <summary>Tests that failed (conclusive probes that were not resisted).</summary>
    public int FailedTests => EffectiveConclusive - PassedTests;

    /// <summary>
    /// Pass rate over CONCLUSIVE probes only (0-100): Resisted / (Resisted + Succeeded). Inconclusive probes
    /// do NOT dilute it (N-03 / RC-6). Returns 0 when nothing was conclusively measured (such a control is
    /// reported <see cref="ControlEvaluationStatus.NotEvaluated"/>, so it is excluded from the compliance rate).
    /// </summary>
    public double PassRate => EffectiveConclusive > 0 ? PassedTests * 100.0 / EffectiveConclusive : 0;

    /// <summary>Coverage (0-100): fraction of applicable probes that produced a conclusive verdict (N-03).</summary>
    public double Coverage => TotalTests > 0 ? EffectiveConclusive * 100.0 / TotalTests : 0.0;

    /// <summary>Evidence summary for auditors.</summary>
    public string EvidenceSummary { get; init; } = "";

    /// <summary>Detailed observations.</summary>
    public IReadOnlyList<string> Observations { get; init; } = [];
}

/// <summary>
/// Evaluation status for a compliance control.
/// </summary>
public enum ControlEvaluationStatus
{
    /// <summary>Control meets requirements.</summary>
    Effective,

    /// <summary>Control partially meets requirements.</summary>
    PartiallyEffective,

    /// <summary>Control does not meet requirements.</summary>
    NeedsImprovement,

    /// <summary>Control was not evaluated.</summary>
    NotEvaluated,

    /// <summary>Control is not applicable to this evaluation.</summary>
    NotApplicable
}

/// <summary>
/// SOC2 Trust Services Criteria definitions.
/// </summary>
public static class SOC2Controls
{
    /// <summary>All SOC2 control mappings relevant to AI security. OwaspCategories derive from the canonical attack taxonomy (RC-5).</summary>
    public static readonly ControlMapping[] All =
    [
        new()
        {
            ControlId = "CC6.1",
            ControlName = "Logical and Physical Access Controls",
            Description = "The entity implements logical access security software, infrastructure, and architectures over protected information assets to protect them from security events.",
            Framework = "SOC2",
            RelevantAttacks = ["SystemPromptExtraction", "PIILeakage"]
            // OwaspCategories derived → ["LLM02", "LLM07"]
        },
        new()
        {
            ControlId = "CC6.2",
            ControlName = "Access Restrictions",
            Description = "Prior to issuing system credentials and granting system access, the entity registers and authorizes new internal and external users.",
            Framework = "SOC2",
            RelevantAttacks = ["ExcessiveAgency"]
            // OwaspCategories derived → ["LLM06"] (was hand-authored "LLM08"). RC-5.
        },
        new()
        {
            ControlId = "CC6.3",
            ControlName = "Unauthorized Access Prevention",
            Description = "The entity authorizes, modifies, or removes access to data, software, functions, and other protected information assets based on roles, responsibilities, or the system design and changes.",
            Framework = "SOC2",
            RelevantAttacks = ["PromptInjection", "Jailbreak"]
            // OwaspCategories derived → ["LLM01"]
        },
        new()
        {
            ControlId = "CC6.6",
            ControlName = "System Boundaries",
            Description = "The entity implements logical access security measures to protect against threats from sources outside its system boundaries.",
            Framework = "SOC2",
            RelevantAttacks = ["IndirectInjection", "EncodingEvasion"]
            // OwaspCategories derived → ["LLM01"]
        },
        new()
        {
            ControlId = "CC6.7",
            ControlName = "Information Transmission",
            Description = "The entity restricts the transmission, movement, and removal of information to authorized internal and external users and processes.",
            Framework = "SOC2",
            RelevantAttacks = ["PIILeakage", "InsecureOutput"]
            // OwaspCategories derived → ["LLM02", "LLM05"]
        },
        new()
        {
            ControlId = "CC7.2",
            ControlName = "System Monitoring",
            Description = "The entity monitors system components and the operation of those components for anomalies that are indicative of malicious acts, natural disasters, and errors.",
            Framework = "SOC2",
            RelevantAttacks = ["InferenceAPIAbuse"]
            // OwaspCategories derived → ["LLM10"] (was hand-authored "LLM04"). RC-5.
        },
        new()
        {
            ControlId = "CC8.1",
            ControlName = "Change Management",
            Description = "The entity authorizes, designs, develops or acquires, configures, documents, tests, approves, and implements changes to infrastructure, data, software, and procedures.",
            Framework = "SOC2",
            RelevantAttacks = [] // Baseline comparison feature; OwaspCategories derived → []
        }
    ];

    /// <summary>Security-related controls (CC6.x, CC7.x).</summary>
    public static IEnumerable<ControlMapping> SecurityControls =>
        All.Where(c => c.ControlId.StartsWith("CC6") || c.ControlId.StartsWith("CC7"));
}

/// <summary>
/// ISO 27001:2022 Annex A control definitions.
/// </summary>
public static class ISO27001Controls
{
    /// <summary>All ISO 27001 control mappings relevant to AI security. OwaspCategories derive from the canonical attack taxonomy (RC-5).</summary>
    public static readonly ControlMapping[] All =
    [
        new()
        {
            ControlId = "A.5.1",
            ControlName = "Policies for Information Security",
            Description = "Information security policy and topic-specific policies shall be defined, approved by management, published, communicated to and acknowledged by relevant personnel and relevant interested parties.",
            Framework = "ISO27001",
            RelevantAttacks = ["PromptInjection", "Jailbreak", "PIILeakage"]
            // OwaspCategories derived → ["LLM01", "LLM02"]
        },
        new()
        {
            ControlId = "A.5.15",
            ControlName = "Access Control",
            Description = "Rules to control physical and logical access to information and other associated assets shall be established and implemented based on business and information security requirements.",
            Framework = "ISO27001",
            RelevantAttacks = ["ExcessiveAgency", "SystemPromptExtraction"]
            // OwaspCategories derived → ["LLM06", "LLM07"] (was hand-authored incl. "LLM08"). RC-5.
        },
        new()
        {
            ControlId = "A.5.33",
            ControlName = "Protection of Records",
            Description = "Records shall be protected from loss, destruction, falsification, unauthorized access, and unauthorized release.",
            Framework = "ISO27001",
            RelevantAttacks = ["PIILeakage"]
            // OwaspCategories derived → ["LLM02"]
        },
        new()
        {
            ControlId = "A.8.3",
            ControlName = "Information Access Restriction",
            Description = "Access to information and other associated assets shall be restricted in accordance with the established topic-specific policy on access control.",
            Framework = "ISO27001",
            RelevantAttacks = ["PromptInjection", "Jailbreak"]
            // OwaspCategories derived → ["LLM01"]
        },
        new()
        {
            ControlId = "A.8.11",
            ControlName = "Data Masking",
            Description = "Data masking shall be used in accordance with the organization's topic-specific policy on access control and other related topic-specific policies, and business requirements, taking applicable legislation into consideration.",
            Framework = "ISO27001",
            RelevantAttacks = ["PIILeakage"]
            // OwaspCategories derived → ["LLM02"]
        },
        new()
        {
            ControlId = "A.8.12",
            ControlName = "Data Leakage Prevention",
            Description = "Data leakage prevention measures shall be applied to systems, networks and any other devices that process, store or transmit sensitive information.",
            Framework = "ISO27001",
            RelevantAttacks = ["PIILeakage", "SystemPromptExtraction", "InsecureOutput"]
            // OwaspCategories derived → ["LLM02", "LLM05", "LLM07"]
        },
        new()
        {
            // RC-5: EncodingEvasion is obfuscation of injection payloads, not cryptography.
            // ISO 27001:2022 A.8.24 (Use of Cryptography) was wrong; mapped to A.8.8.
            ControlId = "A.8.8",
            ControlName = "Management of Technical Vulnerabilities",
            Description = "Information about technical vulnerabilities of information systems in use shall be obtained, the organization's exposure to such vulnerabilities evaluated, and appropriate measures taken.",
            Framework = "ISO27001",
            RelevantAttacks = ["EncodingEvasion"]
            // OwaspCategories derived → ["LLM01"]
        },
        new()
        {
            ControlId = "A.8.28",
            ControlName = "Secure Coding",
            Description = "Secure coding principles shall be applied to software development.",
            Framework = "ISO27001",
            RelevantAttacks = ["InsecureOutput"]
            // OwaspCategories derived → ["LLM05"] (was hand-authored "LLM02"). RC-5.
        }
    ];

    /// <summary>Access control related controls (A.5.x).</summary>
    public static IEnumerable<ControlMapping> AccessControls =>
        All.Where(c => c.ControlId.StartsWith("A.5"));

    /// <summary>Technology controls (A.8.x).</summary>
    public static IEnumerable<ControlMapping> TechnologyControls =>
        All.Where(c => c.ControlId.StartsWith("A.8"));
}
