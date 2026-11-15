// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json;

namespace AgentEval.Prompts;

/// <summary>Resolves immutable instruments without process-wide state or implicit locale fallback.</summary>
public sealed class JudgePromptResolver : IJudgePromptResolver
{
    // Until a metric is migrated, explicit configuration must never be silently discarded.
    internal static void RejectUnsupported(JudgePromptOptions? options, string evaluatorKey)
    {
        if (options is not null)
            throw new NotSupportedException($"Expected: an evaluator with prompt-pack support. Actual: '{evaluatorKey}' does not support prompt selection. " +
                "Suggestions: use a supported RAG metric or text-only AtomicLlmEval, or evaluate this metric separately without prompt selection. " +
                "Because: silently retaining an English instrument would misrepresent the requested evaluation.");
    }

    // An English code-only metric can share a run's options, but cannot consume a prompt
    // definition that purports to replace its keyword rules. Fallback is explicit as elsewhere.
    internal static bool CanUseEnglishPrefilter(JudgePromptOptions options, string evaluatorKey)
    {
        var locale = JudgePromptDefinition.NormalizeLocale(options.Locale);
        return locale.Split('-')[0] == "en"
            && options.Override?.EvaluatorKey != evaluatorKey
            && options.Pack?.TryGet(evaluatorKey, locale, out _) != true
            && (options.FallbackLocale is null || options.Pack?.TryGet(evaluatorKey, options.FallbackLocale, out _) != true);
    }

    /// <inheritdoc />
    public ResolvedJudgePrompt Resolve(JudgePromptDefinition builtIn, JudgePromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(options);
        builtIn.Validate();
        var requested = JudgePromptDefinition.NormalizeLocale(options.Locale);
        var fallback = options.FallbackLocale is null ? null : JudgePromptDefinition.NormalizeLocale(options.FallbackLocale);
        JudgePromptDefinition? selected;
        string origin;
        if (options.Override is { } replacement)
        {
            replacement.Validate();
            if (JudgePromptDefinition.NormalizeLocale(replacement.Locale) != requested)
                throw Invalid("override locale differs from the requested locale", "supply a matching override");
            selected = replacement;
            origin = "override";
        }
        else
        {
            (selected, origin) = Find(requested);
            if (selected is null && fallback is not null) (selected, origin) = Find(fallback);
            if (selected is null)
                throw Invalid($"no '{builtIn.EvaluatorKey}' instrument for '{requested}'", "supply that locale or explicitly configure FallbackLocale");
        }

        selected.Validate();
        if (selected.EvaluatorKey != builtIn.EvaluatorKey || selected.ResponseContract != builtIn.ResponseContract)
            throw Invalid("override key or response contract does not match the evaluator", "retain the exported key and response contract");
        if (selected.Dimension != builtIn.Dimension)
            throw Invalid("the replacement changes the evaluated dimension", "retain the exported dimension key and translate its surrounding instructions");
        if (builtIn.DataSlots.Except(selected.DataSlots, StringComparer.Ordinal).Any())
            throw Invalid("the replacement omits required evaluator data", "retain all exported data slots");
        if (builtIn.Criteria.Count > 0 && !builtIn.Criteria.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(selected.Criteria.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
            throw Invalid("the replacement changes stable criterion IDs", "translate criterion text while retaining every exported ID");

        selected = selected with { Locale = JudgePromptDefinition.NormalizeLocale(selected.Locale) };
        // Serialize the complete immutable definition, not a file path or case content. Any text or policy
        // change invalidates the identity even if an author forgot to increment the version.
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(selected))).ToLowerInvariant();
        var provenance = new JudgePromptProvenance(selected.EvaluatorKey, selected.Id, selected.Version,
            requested, selected.Locale, origin, requested != selected.Locale, digest, selected.ResponseContract);
        return new(selected, provenance, options.CaptureContent);

        (JudgePromptDefinition?, string) Find(string locale)
        {
            if (options.Pack?.TryGet(builtIn.EvaluatorKey, locale, out var definition) == true)
                return (definition, "pack");
            return JudgePromptDefinition.NormalizeLocale(builtIn.Locale) == locale ? (builtIn, "builtin") : (null, "");
        }
    }

    private static ArgumentException Invalid(string actual, string suggestion) => new(
        $"Expected: a compatible localized judge instrument. Actual: {actual}. Suggestions: {suggestion}. Because: prompt selection must be explicit and preserve the evaluator contract.");
}
