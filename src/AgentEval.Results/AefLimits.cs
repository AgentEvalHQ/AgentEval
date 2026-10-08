// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results;

/// <summary>
/// The limits of [ENC-17] (contracts/aef/1/spec/02-encoding.md, §2.6): a writer never exceeds them, a reader may refuse
/// anything beyond them and reports it as <c>limit</c> ([ENC-18]), and never refuses anything within them. Path length
/// is [RUN-3]'s, a <c>path</c> problem rather than a limit.
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
    /// <see cref="MaxSealBytes"/>).
    /// </summary>
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The size of <c>seal.json</c>, a batch seal (<c>overlays/seal-&lt;nnnn&gt;.json</c>) or a DSSE envelope
    /// (<c>*.dsse.json</c>), in bytes: 32 MiB. They list or hold every sealed file, so 4 MiB would cap a sealable run
    /// at about 20,000 files.
    /// </summary>
    public const int MaxSealBytes = 32 * 1024 * 1024;

    /// <summary>The number of lines in one NDJSON file.</summary>
    public const int MaxLines = 1_000_000;

    /// <summary>The number of files in one run folder.</summary>
    public const int MaxFiles = 100_000;

    /// <summary>The size of one blob, in bytes: 1 GiB.</summary>
    public const long MaxBlobBytes = 1L << 30;

    /// <summary>The length of a path in a run folder, in UTF-8 bytes ([RUN-3]: longer is a <c>path</c> problem).</summary>
    public const int MaxPathBytes = 255;

    /// <summary>
    /// The size limit of the JSON file at <paramref name="path"/> (a path in a run folder, or a file name):
    /// <see cref="MaxSealBytes"/> for <c>seal.json</c>, <c>overlays/seal-&lt;nnnn&gt;.json</c> and any
    /// <c>*.dsse.json</c>; <see cref="MaxJsonBytes"/> for every other.
    /// </summary>
    public static int MaxBytesOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var batchSeal = normalized.EndsWith(".json", StringComparison.Ordinal)
                        && (normalized.StartsWith("overlays/seal-", StringComparison.Ordinal) || normalized.Contains("/overlays/seal-", StringComparison.Ordinal))
                        && name.Length == "seal-0000.json".Length && name[5..9].All(char.IsAsciiDigit);
        return name == "seal.json" || batchSeal || name.EndsWith(".dsse.json", StringComparison.Ordinal)
            ? MaxSealBytes
            : MaxJsonBytes;
    }
}
