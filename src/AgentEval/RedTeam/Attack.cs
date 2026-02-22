// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam.Attacks;

namespace AgentEval.RedTeam;

/// <summary>
/// Static factory for creating attack types.
/// Provides convenient access to all built-in attacks.
/// </summary>
/// <remarks>
/// Use this class for the simple API pattern:
/// <code>
/// var result = await agent.ScanAsync([
///     Attack.PromptInjection,
///     Attack.Jailbreak,
///     Attack.PIILeakage
/// ]);
/// </code>
/// </remarks>
public static class Attack
{
    // === Built-in Attack Types ===
    // Lazy singletons for efficient reuse

    private static readonly Lazy<PromptInjectionAttack> _promptInjection = new(() => new PromptInjectionAttack());
    private static readonly Lazy<JailbreakAttack> _jailbreak = new(() => new JailbreakAttack());
    private static readonly Lazy<PIILeakageAttack> _piiLeakage = new(() => new PIILeakageAttack());
    private static readonly Lazy<SystemPromptExtractionAttack> _systemPromptExtraction = new(() => new SystemPromptExtractionAttack());
    private static readonly Lazy<IndirectInjectionAttack> _indirectInjection = new(() => new IndirectInjectionAttack());
    private static readonly Lazy<InferenceAPIAbuseAttack> _inferenceApiAbuse = new(() => new InferenceAPIAbuseAttack());
    private static readonly Lazy<ExcessiveAgencyAttack> _excessiveAgency = new(() => new ExcessiveAgencyAttack());
    private static readonly Lazy<InsecureOutputAttack> _insecureOutput = new(() => new InsecureOutputAttack());
    private static readonly Lazy<EncodingEvasionAttack> _encodingEvasion = new(() => new EncodingEvasionAttack());

    /// <summary>
    /// Prompt Injection attack (OWASP LLM01).
    /// Tests for vulnerability to instruction override attacks.
    /// </summary>
    public static IAttackType PromptInjection => _promptInjection.Value;

    /// <summary>
    /// Jailbreak attack (OWASP LLM01).
    /// Tests for vulnerability to constraint bypass attacks.
    /// </summary>
    public static IAttackType Jailbreak => _jailbreak.Value;

    /// <summary>
    /// PII Leakage attack (OWASP LLM02).
    /// Tests for unauthorized disclosure of personal information.
    /// </summary>
    public static IAttackType PIILeakage => _piiLeakage.Value;

    /// <summary>
    /// System Prompt Extraction attack (OWASP LLM07).
    /// Tests for vulnerability to system prompt disclosure.
    /// </summary>
    public static IAttackType SystemPromptExtraction => _systemPromptExtraction.Value;

    /// <summary>
    /// Indirect Prompt Injection attack (OWASP LLM01).
    /// Tests for vulnerability to attacks via external data sources.
    /// </summary>
    public static IAttackType IndirectInjection => _indirectInjection.Value;

    /// <summary>
    /// Inference API Abuse attack (OWASP LLM10).
    /// Tests for vulnerability to ML inference API abuse and resource exhaustion.
    /// </summary>
    public static IAttackType InferenceAPIAbuse => _inferenceApiAbuse.Value;

    /// <summary>
    /// Excessive Agency attack (OWASP LLM06).
    /// Tests for vulnerability to scope expansion, privilege escalation, and unauthorized autonomous actions.
    /// </summary>
    public static IAttackType ExcessiveAgency => _excessiveAgency.Value;

    /// <summary>
    /// Insecure Output Handling attack (OWASP LLM05).
    /// Tests for outputs containing injection payloads (XSS, SQL, commands) that could compromise downstream systems.
    /// </summary>
    public static IAttackType InsecureOutput => _insecureOutput.Value;

    /// <summary>
    /// Encoding Evasion attack (OWASP LLM01).
    /// Tests for vulnerability to injection attempts disguised using 15+ encoding schemes.
    /// </summary>
    public static IAttackType EncodingEvasion => _encodingEvasion.Value;

    // === Convenience Methods ===

    /// <summary>
    /// Get all MVP attack types (PromptInjection, Jailbreak, PIILeakage).
    /// </summary>
    /// <exception cref="NotImplementedException">Until attacks are implemented.</exception>
    public static IReadOnlyList<IAttackType> AllMvp =>
        [PromptInjection, Jailbreak, PIILeakage];

    /// <summary>
    /// Get all built-in attack types.
    /// </summary>
    /// <exception cref="NotImplementedException">Until attacks are implemented.</exception>
    public static IReadOnlyList<IAttackType> All =>
        [PromptInjection, Jailbreak, PIILeakage, SystemPromptExtraction, IndirectInjection, InferenceAPIAbuse, ExcessiveAgency, InsecureOutput, EncodingEvasion];

    /// <summary>
    /// Get attack type by name (case-insensitive).
    /// </summary>
    /// <param name="name">Attack name (e.g., "PromptInjection").</param>
    /// <returns>The attack type, or null if not found.</returns>
    public static IAttackType? ByName(string name)
    {
        return name?.ToUpperInvariant() switch
        {
            "PROMPTINJECTION" or "PROMPT_INJECTION" => PromptInjection,
            "JAILBREAK" => Jailbreak,
            "PIILEAKAGE" or "PII_LEAKAGE" => PIILeakage,
            "SYSTEMPROMPTEXTRACTION" or "SYSTEM_PROMPT_EXTRACTION" => SystemPromptExtraction,
            "INDIRECTINJECTION" or "INDIRECT_INJECTION" => IndirectInjection,
            "INFERENCEAPIABUSE" or "INFERENCE_API_ABUSE" => InferenceAPIAbuse,
            "EXCESSIVEAGENCY" or "EXCESSIVE_AGENCY" => ExcessiveAgency,
            "INSECUREOUTPUT" or "INSECURE_OUTPUT" => InsecureOutput,
            "ENCODINGEVASION" or "ENCODING_EVASION" => EncodingEvasion,
            _ => null
        };
    }

    /// <summary>
    /// Get attack types by OWASP LLM ID.
    /// </summary>
    /// <param name="owaspId">OWASP LLM ID (e.g., "LLM01").</param>
    /// <returns>Attack types matching the OWASP ID.</returns>
    public static IReadOnlyList<IAttackType> ByOwaspId(string owaspId)
    {
        // Filter All attacks by their OwaspLlmId property.
        // Mapping based on OWASP LLM Top 10 v2.0 (2025):
        return owaspId?.ToUpperInvariant() switch
        {
            "LLM01" => [PromptInjection, Jailbreak, IndirectInjection, EncodingEvasion],
            "LLM02" => [PIILeakage],        // Sensitive Information Disclosure
            "LLM05" => [InsecureOutput],    // Improper Output Handling
            "LLM06" => [ExcessiveAgency],   // Excessive Agency
            "LLM07" => [SystemPromptExtraction],
            "LLM10" => [InferenceAPIAbuse], // Unbounded Consumption
            _ => []
        };
    }

    // === Attack Names for Convenience ===

    /// <summary>Available attack names for autocomplete/validation.</summary>
    public static IReadOnlyList<string> AvailableNames =>
        ["PromptInjection", "Jailbreak", "PIILeakage", "SystemPromptExtraction", "IndirectInjection", "InferenceAPIAbuse", "ExcessiveAgency", "InsecureOutput", "EncodingEvasion"];

    /// <summary>MVP attack names.</summary>
    public static IReadOnlyList<string> MvpNames =>
        ["PromptInjection", "Jailbreak", "PIILeakage"];
}
