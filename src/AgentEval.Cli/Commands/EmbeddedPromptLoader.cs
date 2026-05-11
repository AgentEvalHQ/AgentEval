// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Reflection;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Phase-6 Task 6.8 — loads embedded judge-system prompt files from a benchmark
/// assembly's manifest resources and caches them for the lifetime of the
/// process. Pass-3 found the GDPR / EU AI Act benchmarks shipped polished
/// system prompts that were validated by tests, recorded in provenance, and
/// never reached the LLM. <c>JudgeFactory.Resolve</c> now accepts a
/// <c>systemPrompt</c>; the GDPR / EU AI Act bench commands load their prompts
/// through this helper and pass them through.
/// </summary>
internal static class EmbeddedPromptLoader
{
    private static readonly ConcurrentDictionary<(string Asm, string Suffix), string> s_cache = new();

    /// <summary>
    /// Loads the embedded resource whose manifest name ends with
    /// <paramref name="suffix"/> from <paramref name="assembly"/>. The result
    /// is cached per <c>(assembly-name, suffix)</c> pair. Throws
    /// <see cref="InvalidOperationException"/> when zero or multiple resources
    /// match — surface the producer-side mistake at the source rather than
    /// silently picking one.
    /// </summary>
    /// <param name="assembly">Assembly to scan (typically a benchmark-sample assembly).</param>
    /// <param name="suffix">Filename suffix to match (e.g. <c>"gdpr-judge-system.v1.md"</c>).</param>
    /// <returns>The full text of the embedded prompt.</returns>
    internal static string Load(Assembly assembly, string suffix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return s_cache.GetOrAdd((assembly.GetName().Name ?? "<anon>", suffix), _ => ReadOnce(assembly, suffix));
    }

    private static string ReadOnce(Assembly assembly, string suffix)
    {
        var matches = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"Embedded prompt resource ending with '{suffix}' was not found in assembly '{assembly.GetName().Name}'.");
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Embedded prompt resource ending with '{suffix}' is ambiguous in assembly " +
                $"'{assembly.GetName().Name}': {string.Join(", ", matches)}.");
        using var stream = assembly.GetManifestResourceStream(matches[0])
            ?? throw new InvalidOperationException(
                $"GetManifestResourceStream returned null for resource '{matches[0]}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
