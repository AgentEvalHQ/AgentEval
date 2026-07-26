// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Host-local lexical path normalization and directory-boundary containment.</summary>
internal static class PathContainmentPolicy
{
    internal const int MaxAllowedRoots = 256;
    internal const int MaxPathChars = 32_768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparison HostPathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static (IReadOnlyList<string> Roots, string? BasePath) NormalizeConfiguration(
        IEnumerable<string> allowedRoots,
        string? basePath,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        var normalizedBase = basePath is null
            ? null
            : NormalizeConfiguredAbsolute(basePath, nameof(basePath));
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer);
        var inputCount = 0;
        foreach (var root in allowedRoots)
        {
            inputCount++;
            if (inputCount > MaxAllowedRoots)
            {
                throw new ArgumentException(
                    $"at most {MaxAllowedRoots} allowed roots may be configured.",
                    parameterName);
            }

            roots.Add(NormalizeConfiguredAbsolute(root, parameterName));
        }

        if (roots.Count == 0)
        {
            throw new ArgumentException("at least one allowed root is required.", parameterName);
        }

        return (
            new ReadOnlyCollection<string>(roots
                .OrderBy(root => root, comparer)
                .ThenBy(root => root, StringComparer.Ordinal)
                .ToArray()),
            normalizedBase);
    }

    internal static bool Contains(object value, PathContainmentPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(predicate);

        try
        {
            if (!TryReadPath(value, out var rawPath) ||
                !TryNormalizeCandidate(rawPath, predicate.BasePath, out var candidate))
            {
                return false;
            }

            var candidateIsUnc = TryGetUncShare(candidate, out var candidateShare);
            foreach (var root in predicate.AllowedRoots)
            {
                var rootIsUnc = TryGetUncShare(root, out var rootShare);
                if (candidateIsUnc != rootIsUnc ||
                    (candidateIsUnc && !string.Equals(candidateShare, rootShare, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (IsAtOrBelow(candidate, root))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when
            (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return false;
        }

        return false;
    }

    private static string NormalizeConfiguredAbsolute(string path, string parameterName)
    {
        if (!TryNormalizeAbsolute(path, out var normalized))
        {
            throw new ArgumentException(
                "configured paths must be unambiguous, fully-qualified host-local paths.",
                parameterName);
        }

        return normalized;
    }

    private static bool TryNormalizeCandidate(string path, string? basePath, out string normalized)
    {
        normalized = string.Empty;
        if (!IsSafePathText(path))
        {
            return false;
        }

        var hostPath = NormalizeHostSeparators(path);
        if (LooksLikeForeignOrAmbiguousWindowsPath(hostPath) ||
            (Path.IsPathRooted(hostPath) && !Path.IsPathFullyQualified(hostPath)))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(hostPath))
        {
            if (basePath is null)
            {
                return false;
            }

            hostPath = Path.Combine(basePath, hostPath);
        }

        return TryNormalizeAbsolute(hostPath, out normalized);
    }

    private static bool TryNormalizeAbsolute(string path, out string normalized)
    {
        normalized = string.Empty;
        if (!IsSafePathText(path))
        {
            return false;
        }

        var hostPath = NormalizeHostSeparators(path);
        if (LooksLikeForeignOrAmbiguousWindowsPath(hostPath) ||
            !Path.IsPathFullyQualified(hostPath) ||
            !IsHostSyntaxSafe(hostPath) ||
            (OperatingSystem.IsWindows() && LooksLikeUnc(hostPath) && !TryGetUncShare(hostPath, out _)))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(hostPath);
            if (fullPath.Length > MaxPathChars ||
                !IsSafePathText(fullPath) ||
                !IsHostSyntaxSafe(fullPath))
            {
                return false;
            }

            normalized = Path.TrimEndingDirectorySeparator(fullPath);
            return normalized.Length > 0;
        }
        catch (Exception exception) when
            (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryReadPath(object value, out string path)
    {
        path = value switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            _ => string.Empty,
        };
        return IsSafePathText(path);
    }

    private static bool IsSafePathText(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathChars)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(path);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        foreach (var character in path)
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) ||
                category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeHostSeparators(string path)
        => OperatingSystem.IsWindows()
            ? path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            : path;

    private static bool LooksLikeForeignOrAmbiguousWindowsPath(string path)
    {
        if (HasWindowsDevicePrefix(path) || IsWindowsDriveRelative(path))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return IsWindowsDriveAbsolute(path) || LooksLikeUnc(path);
        }

        return false;
    }

    private static bool IsHostSyntaxSafe(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (HasWindowsDevicePrefix(path))
        {
            return false;
        }

        var colonCount = 0;
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if ("<>\"|?*".Contains(character))
            {
                return false;
            }

            if (character == ':')
            {
                colonCount++;
                if (index != 1 || !char.IsAsciiLetter(path[0]))
                {
                    return false;
                }
            }
        }

        if (colonCount > 1)
        {
            return false;
        }

        foreach (var segment in path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':' ||
                segment is "." or "..")
            {
                continue;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.') || IsReservedWindowsDeviceName(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAtOrBelow(string candidate, string root)
    {
        if (string.Equals(candidate, root, HostPathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, HostPathComparison);
    }

    private static bool TryGetUncShare(string path, out string share)
    {
        share = string.Empty;
        if (!OperatingSystem.IsWindows() || !LooksLikeUnc(path))
        {
            return false;
        }

        var parts = path.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            parts[0] is "." or ".." ||
            parts[1] is "." or "..")
        {
            return false;
        }

        share = parts[0] + "\\" + parts[1];
        return true;
    }

    private static bool LooksLikeUnc(string path)
        => path.Length >= 2 &&
           (path[0] is '\\' or '/') &&
           (path[1] is '\\' or '/');

    private static bool HasWindowsDevicePrefix(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
           path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
           path.StartsWith(@"\??\", StringComparison.Ordinal) ||
           path.StartsWith("//?/", StringComparison.Ordinal) ||
           path.StartsWith("//./", StringComparison.Ordinal);

    private static bool IsWindowsDriveAbsolute(string path)
        => path.Length >= 3 &&
           char.IsAsciiLetter(path[0]) &&
           path[1] == ':' &&
           path[2] is '\\' or '/';

    private static bool IsWindowsDriveRelative(string path)
        => path.Length >= 2 &&
           char.IsAsciiLetter(path[0]) &&
           path[1] == ':' &&
           (path.Length == 2 || path[2] is not ('\\' or '/'));

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
}
