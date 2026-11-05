// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text;

namespace AgentEval.Tests.Microsoft365.TestHelpers;

/// <summary>
/// Reads the synthetic Work IQ fixtures embedded from <c>tests/AgentEval.Tests/Microsoft365/Fixtures</c> by their
/// folder-relative name — <c>envelopes/send-task-submitted.json</c>, <c>sse/stream-happy-path.txt</c>,
/// <c>cards/default-card-two-interfaces.json</c> — or by the dotted manifest form
/// (<c>envelopes.send-task-submitted.json</c>). The catalog itself is guarded by <c>FixtureCatalogTests</c>.
/// </summary>
internal static class FixtureLoader
{
    /// <summary>The manifest prefix every fixture resource carries.</summary>
    public const string ResourcePrefix = "AgentEval.Tests.Microsoft365.Fixtures.";

    private static readonly Assembly TestAssembly = typeof(FixtureLoader).Assembly;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Every embedded fixture, as folder-relative names (<c>envelopes/x.json</c>), sorted.</summary>
    public static IReadOnlyList<string> Names { get; } = TestAssembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..])
        .Select(ToRelativeName)
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>The full manifest resource name for a fixture name (slash, backslash or dotted form; a full name passes through).</summary>
    /// <param name="name">The fixture name.</param>
    public static string ResourceName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim().Replace('\\', '/').TrimStart('/');
        if (trimmed.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        {
            return trimmed;
        }

        return ResourcePrefix + trimmed.Replace('/', '.');
    }

    /// <summary>Whether <paramref name="name"/> names an embedded fixture.</summary>
    /// <param name="name">The fixture name (any form).</param>
    public static bool Exists(string name) =>
        !string.IsNullOrWhiteSpace(name) && TestAssembly.GetManifestResourceInfo(ResourceName(name)) is not null;

    /// <summary>Opens the fixture's bytes.</summary>
    /// <param name="name">The fixture name (any form).</param>
    /// <exception cref="FileNotFoundException">No such fixture; the message lists the known names.</exception>
    public static Stream Open(string name)
    {
        var resourceName = ResourceName(name);
        return TestAssembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Embedded Microsoft 365 fixture '{name}' not found (resource '{resourceName}'). Known fixtures: {string.Join(", ", Names)}");
    }

    /// <summary>The fixture's bytes exactly as embedded.</summary>
    /// <param name="name">The fixture name (any form).</param>
    public static byte[] Bytes(string name)
    {
        using var stream = Open(name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The fixture decoded as strict UTF-8 (no BOM is embedded; none is stripped).</summary>
    /// <param name="name">The fixture name (any form).</param>
    public static string Text(string name) => Utf8.GetString(Bytes(name));

    private static string ToRelativeName(string dotted)
    {
        // Folder segments are joined by '.', the file name keeps its own dots: "envelopes.x.json" → "envelopes/x.json".
        // Only the three known folders are mapped; a root-level file stays as is.
        foreach (var folder in new[] { "envelopes.", "sse.", "cards." })
        {
            if (dotted.StartsWith(folder, StringComparison.Ordinal))
            {
                return folder.TrimEnd('.') + "/" + dotted[folder.Length..];
            }
        }

        return dotted;
    }
}
