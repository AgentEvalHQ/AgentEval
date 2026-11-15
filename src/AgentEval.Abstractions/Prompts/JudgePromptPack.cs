// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Prompts;

/// <summary>An immutable set of versioned instruments, indexed by evaluator key and explicit locale.</summary>
public sealed class JudgePromptPack
{
    private readonly IReadOnlyDictionary<(string Key, string Locale), JudgePromptDefinition> _definitions;

    /// <summary>Creates a pack, validating every definition and rejecting duplicate key/locale pairs.</summary>
    public JudgePromptPack(IEnumerable<JudgePromptDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var items = definitions.Take(257).ToArray();
        if (items.Length is 0 or > 256)
            throw new ArgumentException("Expected: 1–256 judge definitions. Actual: invalid pack size. Suggestions: split or populate the pack. Because: loading must be bounded.", nameof(definitions));
        var index = new Dictionary<(string, string), JudgePromptDefinition>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate();
            var normalized = item with { Locale = JudgePromptDefinition.NormalizeLocale(item.Locale) };
            if (!index.TryAdd((normalized.EvaluatorKey, normalized.Locale), normalized))
                throw new ArgumentException("Expected: unique evaluator/locale pairs. Actual: duplicate prompt definition. Suggestions: retain one definition per pair. Because: precedence must be explicit.", nameof(definitions));
        }
        _definitions = new System.Collections.ObjectModel.ReadOnlyDictionary<(string, string), JudgePromptDefinition>(index);
        Definitions = Array.AsReadOnly(index.Values.OrderBy(d => d.EvaluatorKey, StringComparer.Ordinal).ThenBy(d => d.Locale, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Definitions in stable key/locale order.</summary>
    public IReadOnlyList<JudgePromptDefinition> Definitions { get; }

    /// <summary>Finds an exact key/locale pair. No parent-locale or English fallback is implicit.</summary>
    public bool TryGet(string evaluatorKey, string locale, out JudgePromptDefinition? definition) =>
        _definitions.TryGetValue((evaluatorKey, JudgePromptDefinition.NormalizeLocale(locale)), out definition);
}

/// <summary>Invocation-owned prompt selection. A complete explicit override wins over the configured pack.</summary>
public sealed record JudgePromptOptions
{
    /// <summary>Immutable pack to resolve. Null permits built-in content only when its locale matches explicitly.</summary>
    public JudgePromptPack? Pack { get; init; }
    /// <summary>Requested locale. Required explicitly; defaults to the built-in English locale.</summary>
    public string Locale { get; init; } = "en";
    /// <summary>Explicit fallback locale; null means fail when the requested locale is unavailable.</summary>
    public string? FallbackLocale { get; init; }
    /// <summary>Complete replacement for one matching evaluator. It must have the requested locale.</summary>
    public JudgePromptDefinition? Override { get; init; }
    /// <summary>Opt into retaining actual resolved system/user/retry content in in-process result provenance.</summary>
    public bool CaptureContent { get; init; }
}

/// <summary>Content-free identity and selection history for the instrument actually sent.</summary>
public sealed record JudgePromptProvenance(
    string EvaluatorKey, string InstrumentId, string Version, string RequestedLocale,
    string EffectiveLocale, string Origin, bool FallbackUsed, string Digest,
    JudgeResponseContract ResponseContract)
{
    /// <summary>Rejects pooling configured instruments with different identities, or configured and legacy instruments.</summary>
    public static void RequireCompatible(IEnumerable<JudgePromptProvenance?> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Select(p => p?.Digest ?? "legacy-unconfigured").Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            throw new InvalidOperationException("Expected: one compatible judge instrument across pooled results. Actual: incompatible prompt identities. Suggestions: run each locale or rubric separately. Because: different instruments cannot share one quality statistic.");
    }

    /// <summary>Explicitly captured text. Native content-off evidence must omit this property.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JudgePromptContent? Content { get; init; }
}

/// <summary>Actual resolved messages, retained only on explicit request; may contain customer policy or case data.</summary>
public sealed record JudgePromptContent(string SystemText, string UserMessage, string RetryText);

/// <summary>Resolved immutable instrument and its selection provenance.</summary>
public sealed record ResolvedJudgePrompt(JudgePromptDefinition Definition, JudgePromptProvenance Provenance, bool CaptureContent);

/// <summary>Resolves per-evaluation prompt configuration against a built-in descriptor without global mutation.</summary>
public interface IJudgePromptResolver
{
    /// <summary>Resolves an override, pack entry or explicit built-in locale, validating contract compatibility.</summary>
    ResolvedJudgePrompt Resolve(JudgePromptDefinition builtIn, JudgePromptOptions options);
}

/// <summary>Optional rich binding for judges that support declarative instruments.</summary>
public interface IJudgePromptBindable
{
    /// <summary>Whether prompt selection was explicitly configured before a built-in descriptor was bound.</summary>
    bool HasPromptConfiguration { get; }

    /// <summary>Binds the built-in descriptor; configured overrides take precedence over its text and policy.</summary>
    AgentEval.Core.IEvaluator WithPromptDefinition(JudgePromptDefinition definition, JudgePromptOptions? options = null);
}

/// <summary>The configured instrument actually used by a bound judge.</summary>
public interface IResolvedJudgePromptSource
{
    /// <summary>The resolved instrument, or null when using the legacy unconfigured path.</summary>
    ResolvedJudgePrompt? ResolvedPrompt { get; }
}
