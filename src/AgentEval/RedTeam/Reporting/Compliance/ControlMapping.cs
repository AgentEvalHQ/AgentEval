// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
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

    /// <summary>OWASP LLM categories that map to this control.</summary>
    public required string[] OwaspCategories { get; init; }
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

    /// <summary>Tests that passed.</summary>
    public int PassedTests { get; init; }

    /// <summary>Tests that failed.</summary>
    public int FailedTests => TotalTests - PassedTests;

    /// <summary>Pass rate percentage.</summary>
    public double PassRate => TotalTests > 0 ? PassedTests * 100.0 / TotalTests : 0;

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
    /// <summary>All SOC2 control mappings relevant to AI security.</summary>
    public static readonly ControlMapping[] All =
    [
        new()
        {
            ControlId = "CC6.1",
            ControlName = "Logical and Physical Access Controls",
            Description = "The entity implements logical access security software, infrastructure, and architectures over protected information assets to protect them from security events.",
            Framework = "SOC2",
            RelevantAttacks = ["SystemPromptExtraction", "PIILeakage"],
            OwaspCategories = ["LLM06", "LLM07"]
        },
        new()
        {
            ControlId = "CC6.2",
            ControlName = "Access Restrictions",
            Description = "Prior to issuing system credentials and granting system access, the entity registers and authorizes new internal and external users.",
            Framework = "SOC2",
            RelevantAttacks = ["ExcessiveAgency"],
            OwaspCategories = ["LLM08"]
        },
        new()
        {
            ControlId = "CC6.3",
            ControlName = "Unauthorized Access Prevention",
            Description = "The entity authorizes, modifies, or removes access to data, software, functions, and other protected information assets based on roles, responsibilities, or the system design and changes.",
            Framework = "SOC2",
            RelevantAttacks = ["PromptInjection", "Jailbreak"],
            OwaspCategories = ["LLM01"]
        },
        new()
        {
            ControlId = "CC6.6",
            ControlName = "System Boundaries",
            Description = "The entity implements logical access security measures to protect against threats from sources outside its system boundaries.",
            Framework = "SOC2",
            RelevantAttacks = ["IndirectInjection", "EncodingEvasion"],
            OwaspCategories = ["LLM01"]
        },
        new()
        {
            ControlId = "CC6.7",
            ControlName = "Information Transmission",
            Description = "The entity restricts the transmission, movement, and removal of information to authorized internal and external users and processes.",
            Framework = "SOC2",
            RelevantAttacks = ["PIILeakage", "InsecureOutput"],
            OwaspCategories = ["LLM02", "LLM06"]
        },
        new()
        {
            ControlId = "CC7.2",
            ControlName = "System Monitoring",
            Description = "The entity monitors system components and the operation of those components for anomalies that are indicative of malicious acts, natural disasters, and errors.",
            Framework = "SOC2",
            RelevantAttacks = ["InferenceAPIAbuse"],
            OwaspCategories = ["LLM04"]
        },
        new()
        {
            ControlId = "CC8.1",
            ControlName = "Change Management",
            Description = "The entity authorizes, designs, develops or acquires, configures, documents, tests, approves, and implements changes to infrastructure, data, software, and procedures.",
            Framework = "SOC2",
            RelevantAttacks = [], // Baseline comparison feature
            OwaspCategories = []
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
    /// <summary>All ISO 27001 control mappings relevant to AI security.</summary>
    public static readonly ControlMapping[] All =
    [
        new()
        {
            ControlId = "A.5.1",
            ControlName = "Policies for Information Security",
            Description = "Information security policy and topic-specific policies shall be defined, approved by management, published, communicated to and acknowledged by relevant personnel and relevant interested parties.",
            Framework = "ISO27001",
            RelevantAttacks = ["PromptInjection", "Jailbreak", "PIILeakage"],
            OwaspCategories = ["LLM01", "LLM06"]
        },
        new()
        {
            ControlId = "A.5.15",
            ControlName = "Access Control",
            Description = "Rules to control physical and logical access to information and other associated assets shall be established and implemented based on business and information security requirements.",
            Framework = "ISO27001",
            RelevantAttacks = ["ExcessiveAgency", "SystemPromptExtraction"],
            OwaspCategories = ["LLM07", "LLM08"]
        },
        new()
        {
            ControlId = "A.5.33",
            ControlName = "Protection of Records",
            Description = "Records shall be protected from loss, destruction, falsification, unauthorized access, and unauthorized release.",
            Framework = "ISO27001",
            RelevantAttacks = ["PIILeakage"],
            OwaspCategories = ["LLM06"]
        },
        new()
        {
            ControlId = "A.8.3",
            ControlName = "Information Access Restriction",
            Description = "Access to information and other associated assets shall be restricted in accordance with the established topic-specific policy on access control.",
            Framework = "ISO27001",
            RelevantAttacks = ["PromptInjection", "Jailbreak"],
            OwaspCategories = ["LLM01"]
        },
        new()
        {
            ControlId = "A.8.11",
            ControlName = "Data Masking",
            Description = "Data masking shall be used in accordance with the organization's topic-specific policy on access control and other related topic-specific policies, and business requirements, taking applicable legislation into consideration.",
            Framework = "ISO27001",
            RelevantAttacks = ["PIILeakage"],
            OwaspCategories = ["LLM06"]
        },
        new()
        {
            ControlId = "A.8.12",
            ControlName = "Data Leakage Prevention",
            Description = "Data leakage prevention measures shall be applied to systems, networks and any other devices that process, store or transmit sensitive information.",
            Framework = "ISO27001",
            RelevantAttacks = ["PIILeakage", "SystemPromptExtraction", "InsecureOutput"],
            OwaspCategories = ["LLM02", "LLM06", "LLM07"]
        },
        new()
        {
            ControlId = "A.8.24",
            ControlName = "Use of Cryptography",
            Description = "Rules for the effective use of cryptography, including cryptographic key management, shall be defined and implemented.",
            Framework = "ISO27001",
            RelevantAttacks = ["EncodingEvasion"],
            OwaspCategories = ["LLM01"]
        },
        new()
        {
            ControlId = "A.8.28",
            ControlName = "Secure Coding",
            Description = "Secure coding principles shall be applied to software development.",
            Framework = "ISO27001",
            RelevantAttacks = ["InsecureOutput"],
            OwaspCategories = ["LLM02"]
        }
    ];

    /// <summary>Access control related controls (A.5.x).</summary>
    public static IEnumerable<ControlMapping> AccessControls =>
        All.Where(c => c.ControlId.StartsWith("A.5"));

    /// <summary>Technology controls (A.8.x).</summary>
    public static IEnumerable<ControlMapping> TechnologyControls =>
        All.Where(c => c.ControlId.StartsWith("A.8"));
}
