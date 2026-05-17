// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentEval.Evals.Agentic.Adversarial;

/// <summary>
/// Shared utility for loading adversarial pattern libraries from embedded JSON resources
/// and compiling regex patterns under uniform options. Centralises pattern compilation,
/// resource I/O, and custom-pattern grafting that would otherwise be duplicated across
/// <see cref="DirectInjectionEval"/>, <see cref="PersonaAttackEval"/>, and
/// <see cref="JailbreakResistanceEval"/>.
/// <para>
/// All patterns are compiled with <see cref="RegexOptions.Compiled"/> and
/// <see cref="RegexOptions.CultureInvariant"/>, with a 200&#x202F;ms match timeout to
/// guard against catastrophic-backtracking regexes in adversarial libraries.
/// </para>
/// </summary>
internal static class AdversarialPatternLibrary
{
    private static readonly RegexOptions DefaultOptions =
        RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A compiled adversarial pattern: stable identifier, compiled <see cref="Regex"/>,
    /// and severity tag (low/medium/high/critical) carried over from the source library.
    /// </summary>
    internal sealed record CompiledPattern(string Id, Regex Pattern, string Severity);

    /// <summary>
    /// Loads patterns from an embedded JSON resource (located by <paramref name="resourceName"/>),
    /// extracts entries from the <paramref name="arrayKey"/> top-level array, then appends any
    /// caller-supplied custom regex strings. Malformed regexes and missing entries are skipped
    /// silently so a single broken library entry cannot crash the evaluator.
    /// </summary>
    /// <param name="assembly">Assembly that owns the embedded resource (typically the agentic assembly).</param>
    /// <param name="resourceName">Fully-qualified embedded-resource name (e.g. <c>"AgentEval.Evals.Agentic.Adversarial.Resources.direct-injection-patterns.v1.json"</c>).</param>
    /// <param name="arrayKey">Top-level JSON property holding the pattern array (e.g. <c>"patterns"</c>, <c>"templates"</c>).</param>
    /// <param name="customPatterns">Optional caller-supplied regex strings appended after the built-in library.</param>
    /// <param name="customId">Identifier assigned to entries from <paramref name="customPatterns"/>. Defaults to <c>"custom"</c>.</param>
    /// <param name="customSeverity">Severity assigned to entries from <paramref name="customPatterns"/>. Defaults to <c>"medium"</c>.</param>
    public static IReadOnlyList<CompiledPattern> Load(
        Assembly assembly,
        string resourceName,
        string arrayKey,
        IReadOnlyList<string>? customPatterns = null,
        string customId = "custom",
        string customSeverity = "medium")
    {
        var result = new List<CompiledPattern>();
        LoadFromResource(assembly, resourceName, arrayKey, result);
        AppendCustomPatterns(result, customPatterns, customId, customSeverity);
        return result;
    }

    /// <summary>
    /// Reads the named embedded JSON resource and appends every well-formed pattern found
    /// under <paramref name="arrayKey"/> to <paramref name="destination"/>. Used directly
    /// when an evaluator needs to merge patterns from multiple resources (see
    /// <see cref="JailbreakResistanceEval"/>).
    /// </summary>
    public static void LoadFromResource(
        Assembly assembly,
        string resourceName,
        string arrayKey,
        List<CompiledPattern> destination)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty(arrayKey, out var arrayEl))
                return;

            foreach (var item in arrayEl.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) ||
                    !item.TryGetProperty("regex", out var regexEl))
                    continue;

                var id       = idEl.GetString() ?? "unknown";
                var regexStr = regexEl.GetString() ?? string.Empty;
                var severity = item.TryGetProperty("severity", out var sevEl)
                    ? (sevEl.GetString() ?? "medium")
                    : "medium";

                if (string.IsNullOrEmpty(regexStr))
                    continue;

                if (TryCompile(regexStr, out var rx))
                    destination.Add(new CompiledPattern(id, rx!, severity));
            }
        }
    }

    /// <summary>
    /// Compiles each non-empty caller-supplied regex string and appends it to
    /// <paramref name="destination"/>. Malformed regexes are skipped silently.
    /// </summary>
    public static void AppendCustomPatterns(
        List<CompiledPattern> destination,
        IReadOnlyList<string>? customPatterns,
        string customId = "custom",
        string customSeverity = "medium")
    {
        if (customPatterns is null)
            return;

        foreach (var pat in customPatterns)
        {
            if (string.IsNullOrEmpty(pat))
                continue;

            if (TryCompile(pat, out var rx))
                destination.Add(new CompiledPattern(customId, rx!, customSeverity));
        }
    }

    private static bool TryCompile(string pattern, out Regex? regex)
    {
        try
        {
            regex = new Regex(pattern, DefaultOptions, DefaultTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }
}
