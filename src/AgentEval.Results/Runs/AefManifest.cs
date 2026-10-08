// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Results.Runs;

/// <summary>One sealed file in a manifest: its path in the run folder, the SHA-256 of its exact bytes (lower-case hex) and its size in bytes.</summary>
public readonly record struct AefManifestEntry(string Path, string Sha256, long Size);

/// <summary>
/// A run's manifest (contracts/aef/1/spec/04-integrity.md, [SEAL-3]): one line per sealed file,
/// <c>&lt;sha256-hex&gt;␠␠&lt;size in bytes, decimal&gt;␠␠&lt;path&gt;\n</c>, ordered by the UTF-8 bytes of the path
/// (<c>/</c> separators, so <c>ext/Z</c> before <c>ext/a-b</c> before <c>ext/a.b</c> before <c>ext/a/b</c>: the whole
/// path is compared, not segment by segment). Its SHA-256 is the run hash ([SEAL-4]). Nothing is re-encoded: there is no
/// canonical JSON, the digests are over the files' bytes ([SEAL-2]).
/// </summary>
public sealed class AefManifest
{
    private readonly byte[] _bytes;

    private AefManifest(IReadOnlyList<AefManifestEntry> entries, byte[] bytes)
    {
        Entries = entries;
        _bytes = bytes;
        RunHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>The entries, in the manifest's order.</summary>
    public IReadOnlyList<AefManifestEntry> Entries { get; }

    /// <summary>The manifest's bytes (UTF-8).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>The manifest as text.</summary>
    public string Text => Encoding.UTF8.GetString(_bytes);

    /// <summary>The run hash ([SEAL-4]): the SHA-256 of the manifest's bytes, lower-case hex.</summary>
    public string RunHash { get; }

    /// <summary>The manifest of these files, in [SEAL-3]'s order whatever order they are given in.</summary>
    /// <exception cref="ArgumentException">
    /// A path listed twice, a digest that is not 64 lower-case hex characters, or a negative size. (A path that breaks
    /// [RUN-3] is written as it is: the manifest is of the files present, and the path is a <c>path</c> problem.)
    /// </exception>
    public static AefManifest Create(IEnumerable<AefManifestEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var ordered = files.OrderBy(f => f.Path, AefProblemOrder.Utf8).ToList();
        var text = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (path, sha256, size) = ordered[i];
            ArgumentNullException.ThrowIfNull(path, nameof(files));
            if (i > 0 && string.Equals(ordered[i - 1].Path, path, StringComparison.Ordinal))
            {
                throw new ArgumentException($"{path} is listed twice: a manifest has one line per sealed file.", nameof(files));
            }

            if (sha256 is null || sha256.Length != 64 || !sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                throw new ArgumentException($"{path}: '{sha256}' is not a SHA-256 in lower-case hex.", nameof(files));
            }

            if (size < 0)
            {
                throw new ArgumentException($"{path}: a negative size.", nameof(files));
            }

            text.Append(sha256).Append("  ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("  ").Append(path).Append('\n');
        }

        return new AefManifest(ordered, Encoding.UTF8.GetBytes(text.ToString()));
    }
}
