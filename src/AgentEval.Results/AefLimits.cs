// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results;

/// <summary>
/// The limits of [ENC-17] (contracts/aef/1/spec/02-encoding.md, §2.6): a writer never exceeds them, and a reader refuses
/// anything beyond them, so that two readers never disagree on a file; it reports what it refused as <c>limit</c>
/// ([ENC-18]: a file at its path, one NDJSON line at <c>&lt;file&gt;:&lt;line&gt;</c>, the number of files at
/// <c>.</c>), and never refuses anything within them. Each is checked on the bytes before the content is trusted: a
/// size, a count of LFs, a depth scan. Path length is [RUN-3]'s, a <c>path</c> problem rather than a limit.
/// </summary>
public static class AefLimits
{
    /// <summary>
    /// The deepest nesting of objects and arrays in a JSON text: the top-level object is at depth 1, so <c>{}</c> has
    /// depth 1 and <c>{"a":[]}</c> depth 2.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// The size of a JSON file, or of one NDJSON line without its LF, in bytes: 4 MiB (except the files of
    /// <see cref="MaxSealBytes"/> and <see cref="MaxEnvelopeBytes"/>).
    /// </summary>
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The size of <c>seal.json</c> or a batch seal (<c>overlays/seal-&lt;nnnn&gt;.json</c>), in bytes: 40 MiB. They
    /// list every sealed file: a run of 100,000 files with 255-byte paths needs a seal of about 38 MiB, indented.
    /// </summary>
    public const int MaxSealBytes = 40 * 1024 * 1024;

    /// <summary>
    /// The size of a DSSE envelope (<c>*.dsse.json</c>), in bytes: 56 MiB. It holds the base64 of a seal, 4/3 of the
    /// seal's size, so the largest seal can be signed.
    /// </summary>
    public const int MaxEnvelopeBytes = 56 * 1024 * 1024;

    /// <summary>The number of lines in one NDJSON file.</summary>
    public const int MaxLines = 1_000_000;

    /// <summary>The size of one NDJSON file, in bytes: 1 GiB, like a blob.</summary>
    public const long MaxNdjsonBytes = 1L << 30;

    /// <summary>The number of files in one run folder.</summary>
    public const int MaxFiles = 100_000;

    /// <summary>The size of one blob, in bytes: 1 GiB.</summary>
    public const long MaxBlobBytes = 1L << 30;

    /// <summary>The length of a path in a run folder, in UTF-8 bytes ([RUN-3]: longer is a <c>path</c> problem).</summary>
    public const int MaxPathBytes = 255;

    /// <summary>
    /// The size limit of the JSON file at <paramref name="path"/> (a path in a run folder, or a file name):
    /// <see cref="MaxEnvelopeBytes"/> for any <c>*.dsse.json</c>; <see cref="MaxSealBytes"/> for <c>seal.json</c> and
    /// <c>overlays/seal-&lt;nnnn&gt;.json</c>; <see cref="MaxJsonBytes"/> for every other.
    /// </summary>
    public static int MaxBytesOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".dsse.json", StringComparison.Ordinal))
        {
            return MaxEnvelopeBytes;
        }

        var batchSeal = normalized.EndsWith(".json", StringComparison.Ordinal)
                        && (normalized.StartsWith("overlays/seal-", StringComparison.Ordinal) || normalized.Contains("/overlays/seal-", StringComparison.Ordinal))
                        && name.Length == "seal-0000.json".Length && name[5..9].All(char.IsAsciiDigit);
        return name == "seal.json" || batchSeal ? MaxSealBytes : MaxJsonBytes;
    }
}
