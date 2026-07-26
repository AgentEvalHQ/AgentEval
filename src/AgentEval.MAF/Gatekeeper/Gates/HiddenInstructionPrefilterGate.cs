// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic, bounded prefilter for obvious hidden or encoded prompt-injection markers in tool results.
/// It scans raw, decoded, Unicode-visibility-normalized, and HTML-comment-normalized projections. It never
/// rewrites tool content: clean results pass unchanged; a match or inconclusive inspection blocks the result.
/// This is a lexical prefilter, not a semantic injection judge, and it does not pass normalized text to one.
/// </summary>
public sealed class HiddenInstructionPrefilterGate : IToolResultGate, IConfigurationFingerprintContributor
{
    private const int MaxTokens = 256;
    private const int MaxTokenChars = 4096;
    private const int MaxFunctionNames = 256;
    private const int MaxFunctionNameChars = 256;

    private readonly IReadOnlyList<string> _tokens;
    private readonly IReadOnlySet<string>? _functionNames;

    /// <summary>
    /// Creates an inspect-all prefilter using <see cref="TokenInjectionGate.DefaultTokens"/>, or a custom bounded
    /// marker set when <paramref name="tokens"/> is supplied.
    /// </summary>
    public HiddenInstructionPrefilterGate(IEnumerable<string>? tokens = null)
    {
        _tokens = NormalizeTokens(tokens);
        ConfigurationFingerprint = ComputeConfigurationFingerprint();
    }

    /// <summary>
    /// Creates a prefilter restricted to exact, case-sensitive tool names. Pass <see langword="null"/> for
    /// <paramref name="tokens"/> to use <see cref="TokenInjectionGate.DefaultTokens"/>.
    /// </summary>
    public HiddenInstructionPrefilterGate(
        IEnumerable<string>? tokens,
        IEnumerable<string> functionNames)
        : this(tokens)
    {
        _functionNames = NormalizeFunctionNames(functionNames);
        ConfigurationFingerprint = ComputeConfigurationFingerprint();
    }

    /// <inheritdoc />
    public string PolicyName => "hidden-instruction-prefilter";

    /// <inheritdoc />
    public GateCost Cost => GateCost.Bounded;

    /// <summary>SHA-256 fingerprint of the normalized, secret-free prefilter configuration.</summary>
    public string ConfigurationFingerprint { get; private set; }

    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution => ConfigurationFingerprint;

    /// <inheritdoc />
    public ValueTask<ToolResultVerdict> InspectAsync(
        GatedToolResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_functionNames is not null && !_functionNames.Contains(result.FunctionName))
        {
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Result is null)
        {
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
        }

        var text = result.ResultText;
        if (text.Length == 0)
        {
            var emptyIsKnownText = result.Result is string or char ||
                result.Result is System.Text.Json.JsonElement
                {
                    ValueKind: System.Text.Json.JsonValueKind.String
                };
            return new ValueTask<ToolResultVerdict>(
                emptyIsKnownText
                    ? ToolResultVerdict.Allow(PolicyName)
                    : ToolResultVerdict.Block(
                        PolicyName,
                        "Tool result could not be inspected conclusively for hidden instructions."));
        }

        var scan = HiddenInstructionScanner.Scan(text, _tokens, cancellationToken);
        var verdict = scan switch
        {
            HiddenInstructionScanStatus.Clean => ToolResultVerdict.Allow(PolicyName),
            HiddenInstructionScanStatus.Match => ToolResultVerdict.Block(
                PolicyName,
                "Tool result contains a hidden instruction marker."),
            _ => ToolResultVerdict.Block(
                PolicyName,
                "Tool result could not be inspected conclusively for hidden instructions."),
        };
        return new ValueTask<ToolResultVerdict>(verdict);
    }

    private static IReadOnlyList<string> NormalizeTokens(IEnumerable<string>? tokens)
    {
        var source = tokens ?? TokenInjectionGate.DefaultTokens;
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var token in source)
        {
            count++;
            if (count > MaxTokens)
            {
                throw new ArgumentException($"at most {MaxTokens} tokens may be configured.", nameof(tokens));
            }

            if (token is null)
            {
                throw new ArgumentException("tokens cannot contain null.", nameof(tokens));
            }

            if (token.Length > MaxTokenChars)
            {
                throw new ArgumentException(
                    $"tokens may contain at most {MaxTokenChars} characters.",
                    nameof(tokens));
            }

            if (!HiddenInstructionScanner.TryNormalizeVisibility(token, out var normalizedToken))
            {
                throw new ArgumentException("tokens must contain valid Unicode text.", nameof(tokens));
            }

            normalizedToken = normalizedToken.Trim();
            if (normalizedToken.Length == 0)
            {
                throw new ArgumentException("tokens cannot contain empty or invisible-only entries.", nameof(tokens));
            }

            normalized.Add(normalizedToken);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("at least one token is required.", nameof(tokens));
        }

        return normalized
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlySet<string> NormalizeFunctionNames(IEnumerable<string> functionNames)
    {
        ArgumentNullException.ThrowIfNull(functionNames);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var functionName in functionNames)
        {
            count++;
            if (count > MaxFunctionNames)
            {
                throw new ArgumentException(
                    $"at most {MaxFunctionNames} function names may be configured.",
                    nameof(functionNames));
            }

            if (string.IsNullOrWhiteSpace(functionName) ||
                functionName.Length > MaxFunctionNameChars ||
                functionName.Any(char.IsControl))
            {
                throw new ArgumentException(
                    $"function names must be non-empty, control-free, and at most {MaxFunctionNameChars} characters.",
                    nameof(functionNames));
            }

            normalized.Add(functionName);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("at least one function name is required.", nameof(functionNames));
        }

        return normalized;
    }

    private string ComputeConfigurationFingerprint()
    {
        var canonical = new StringBuilder("gatekeeper.hidden-instruction-prefilter/1\n");
        AppendCanonical(canonical, "scope", _functionNames is null ? "all" : "selected");
        foreach (var token in _tokens)
        {
            AppendCanonical(
                canonical,
                "tokenHash",
                ManifestFingerprint.Hash(token.ToUpperInvariant()));
        }

        if (_functionNames is not null)
        {
            foreach (var functionName in _functionNames.OrderBy(value => value, StringComparer.Ordinal))
            {
                AppendCanonical(
                    canonical,
                    "functionHash",
                    ManifestFingerprint.Hash(functionName));
            }
        }

        return ManifestFingerprint.Hash(canonical.ToString());
    }

    private static void AppendCanonical(StringBuilder target, string name, string value)
        => target.Append(name).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');
}

internal enum HiddenInstructionScanStatus
{
    Clean,
    Match,
    Inconclusive,
}

internal static class HiddenInstructionScanner
{
    private const int MaxProjectionCount = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static HiddenInstructionScanStatus Scan(
        string text,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return HiddenInstructionScanStatus.Clean;
        }

        var projections = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;

        bool Add(string projection)
        {
            if (projection.Length > ArgumentCanonicalizer.DefaultMaxLength)
            {
                complete = false;
                return false;
            }

            if (!seen.Add(projection))
            {
                return true;
            }

            if (projections.Count >= MaxProjectionCount)
            {
                complete = false;
                return false;
            }

            projections.Add(projection);
            return true;
        }

        var decoded = ArgumentCanonicalizer.CanonicalizeWithStatus(text);
        complete &= decoded.IsComplete;
        foreach (var projection in decoded.Projections)
        {
            Add(projection);
        }

        var initialCount = projections.Count;
        for (var index = 0; index < initialCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = projections[index];
            if (!TryNormalizeVisibility(projection, out var visible))
            {
                complete = false;
                continue;
            }

            Add(visible);
            if (!TryRemoveHtmlComments(projection, out var uncommented))
            {
                complete = false;
            }
            else
            {
                Add(uncommented);
            }

            if (!TryRemoveHtmlComments(visible, out var visibleUncommented))
            {
                complete = false;
            }
            else
            {
                Add(visibleUncommented);
            }
        }

        var transformedCount = projections.Count;
        for (var index = initialCount; index < transformedCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var secondary = ArgumentCanonicalizer.CanonicalizeWithStatus(
                projections[index],
                maxDepth: 1,
                maxLength: ArgumentCanonicalizer.DefaultMaxLength,
                maxProjections: 8);
            complete &= secondary.IsComplete;
            foreach (var projection in secondary.Projections)
            {
                Add(projection);
                if (TryNormalizeVisibility(projection, out var normalized))
                {
                    Add(normalized);
                }
                else
                {
                    complete = false;
                }
            }
        }

        foreach (var projection in projections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var token in tokens)
            {
                if (projection.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return HiddenInstructionScanStatus.Match;
                }
            }
        }

        return complete
            ? HiddenInstructionScanStatus.Clean
            : HiddenInstructionScanStatus.Inconclusive;
    }

    internal static bool TryNormalizeVisibility(string text, out string normalized)
    {
        try
        {
            StrictUtf8.GetByteCount(text);
            var decomposed = text.Normalize(NormalizationForm.FormKD);
            var builder = new StringBuilder(decomposed.Length);
            var lastWasSpace = false;
            foreach (var rune in decomposed.EnumerateRunes())
            {
                var codePoint = rune.Value;
                if (codePoint is >= 0xE0020 and <= 0xE007E)
                {
                    builder.Append((char)(codePoint - 0xE0000));
                    lastWasSpace = false;
                    continue;
                }

                if (codePoint is >= 0xE0000 and <= 0xE007F)
                {
                    continue;
                }

                var category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Format or
                    UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or
                    UnicodeCategory.EnclosingMark)
                {
                    continue;
                }

                if (category is UnicodeCategory.SpaceSeparator or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator ||
                    Rune.IsWhiteSpace(rune))
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                builder.Append(rune.ToString());
                lastWasSpace = false;
            }

            normalized = builder.ToString().Normalize(NormalizationForm.FormKC);
            StrictUtf8.GetByteCount(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static bool TryRemoveHtmlComments(string text, out string uncommented)
    {
        var start = text.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0)
        {
            uncommented = text;
            return true;
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (start >= 0)
        {
            builder.Append(text, position, start - position);
            var end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                uncommented = string.Empty;
                return false;
            }

            position = end + 3;
            start = text.IndexOf("<!--", position, StringComparison.Ordinal);
        }

        builder.Append(text, position, text.Length - position);
        uncommented = builder.ToString();
        return true;
    }
}
