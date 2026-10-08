// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentEval.Results.Json;

namespace AgentEval.Results.Runs;

/// <summary>
/// One run folder as a verifier reads it (contracts/aef/1/spec/03-run.md, §3.1; spec 04, §4.1): its regular files by
/// path (<c>/</c> separators, in byte order), listed once without following links (<see cref="AefFolder"/>), the
/// <c>path</c> problems of [RUN-3], and each file's bytes, SHA-256 and size on demand. Digests are computed once and
/// kept; a file is hashed as a stream, so a blob of up to 1 GiB never sits in memory whole.
/// </summary>
/// <remarks>
/// A file is never read beyond its size at listing plus one byte: a file that grew since, or an entry that is not a
/// regular file but was listed as one (see the remarks of <see cref="AefFolder"/>), throws <see cref="IOException"/>
/// rather than be read without end.
/// </remarks>
public sealed partial class AefRunFolder
{
    private const int BufferBytes = 1 << 20;

    private readonly HashSet<string> _present;
    private readonly Dictionary<string, (string Sha256, long Size)> _digests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private AefManifest? _manifest;

    private AefRunFolder(string root, AefFolderListing listing)
    {
        Root = root;
        Files = listing.Files;
        PathProblems = listing.Problems;
        OverFileLimit = listing.Problems.Any(p => p is { Path: ".", Code: "limit" });
        _present = new HashSet<string>(Files, StringComparer.Ordinal);
        SealedFiles = [.. Files.Where(IsSealed)];
    }

    /// <summary>The folder.</summary>
    public string Root { get; }

    /// <summary>The regular files, by path in the folder, in byte order ([SEAL-3]'s order).</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// The <c>path</c> problems of [RUN-3] (a bad name, a case clash, a link, pipe, socket or device), or the one
    /// <c>limit</c> problem at <c>.</c> when the folder holds more files than [ENC-17] allows.
    /// </summary>
    public IReadOnlyList<AefProblem> PathProblems { get; }

    /// <summary>
    /// The folder holds more than 100,000 files ([ENC-17]): <see cref="Files"/> is only part of it, and nothing more is
    /// checked ([ENC-18]: a part is never read as the whole).
    /// </summary>
    public bool OverFileLimit { get; }

    /// <summary>[SEAL-1]: every file except <c>seal.json</c>, <c>attestation.dsse.json</c> and those under <c>overlays/</c>, in byte order.</summary>
    public IReadOnlyList<string> SealedFiles { get; }

    /// <summary>Lists a run folder.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    /// <exception cref="IOException">The folder or an entry cannot be read.</exception>
    public static AefRunFolder Open(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"{directory}: not a folder.");
        }

        return new AefRunFolder(directory, AefFolder.List(directory));
    }

    /// <summary>Whether a sealed file ([SEAL-1]): not <c>seal.json</c>, <c>attestation.dsse.json</c> or under <c>overlays/</c>.</summary>
    public static bool IsSealed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path is not ("seal.json" or "attestation.dsse.json") && !path.StartsWith("overlays/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is where [EVD-3] stores a blob: <c>blobs/sha256/&lt;ab&gt;/&lt;64 hex&gt;</c>, lower
    /// case, <c>&lt;ab&gt;</c> being the first two characters of the name. The name is returned in
    /// <paramref name="sha256"/>.
    /// </summary>
    public static bool IsBlobPath(string path, out string sha256)
    {
        ArgumentNullException.ThrowIfNull(path);
        var m = BlobPathPattern().Match(path);
        sha256 = m.Success && string.Equals(m.Groups[1].Value, m.Groups[2].Value[..2], StringComparison.Ordinal) ? m.Groups[2].Value : "";
        return sha256.Length > 0;
    }

    /// <summary>Where [EVD-3] stores the blob with this SHA-256 (64 lower-case hex characters).</summary>
    public static string BlobPath(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        return $"blobs/sha256/{sha256[..Math.Min(2, sha256.Length)]}/{sha256}";
    }

    /// <summary>Whether the folder holds a regular file at <paramref name="path"/>.</summary>
    public bool Has(string path) => path is not null && _present.Contains(path);

    /// <summary>The file's size in bytes, as listed.</summary>
    /// <exception cref="FileNotFoundException">No such file in the run.</exception>
    public long Size(string path)
    {
        Require(path);
        if (_digests.TryGetValue(path, out var known))
        {
            return known.Size;
        }

        if (!_sizes.TryGetValue(path, out var size))
        {
            _sizes[path] = size = new FileInfo(FullPath(path)).Length;
        }

        return size;
    }

    /// <summary>
    /// The file's bytes, when it has at most <paramref name="maxBytes"/> (a JSON file's limit,
    /// <see cref="AefLimits.MaxBytesOf"/>). Its digest is kept, so it is not read again to seal it.
    /// </summary>
    /// <exception cref="AefLimitException">The file is larger than <paramref name="maxBytes"/>: it is not read ([ENC-18]).</exception>
    /// <exception cref="FileNotFoundException">No such file in the run.</exception>
    /// <exception cref="IOException">The file cannot be read, or changed while it was read.</exception>
    public byte[] Read(string path, long maxBytes)
    {
        var size = Size(path);
        if (size > maxBytes)
        {
            throw new AefLimitException($"{path}: {size} bytes, above the {maxBytes} [ENC-17] allows.");
        }

        var bytes = new byte[size];
        using (var stream = OpenRead(path))
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() >= 0)
            {
                throw new IOException($"{path}: the file grew while it was read.");
            }
        }

        _digests.TryAdd(path, (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), size));
        return bytes;
    }

    /// <summary>The SHA-256 of the file's exact bytes ([SEAL-2]), lower-case hex, hashed as a stream.</summary>
    /// <exception cref="FileNotFoundException">No such file in the run.</exception>
    /// <exception cref="IOException">The file cannot be read, or changed while it was read.</exception>
    public string Sha256(string path)
    {
        if (_digests.TryGetValue(path, out var known))
        {
            return known.Sha256;
        }

        var size = Size(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[(int)Math.Min(BufferBytes, Math.Max(size, 1))];
        long total = 0;
        using (var stream = OpenRead(path))
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > size)
                {
                    throw new IOException($"{path}: the file holds more than the {size} bytes it was listed with.");
                }

                hash.AppendData(buffer, 0, read);
            }
        }

        if (total != size)
        {
            throw new IOException($"{path}: the file shrank while it was read.");
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        _digests[path] = (digest, size);
        return digest;
    }

    /// <summary>
    /// The manifest of the files present ([SEAL-3]): one line per sealed file (<see cref="SealedFiles"/>), whatever a
    /// seal says. For a run with a withheld blob it is not the sealed manifest: the blob is gone.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public AefManifest Manifest() =>
        _manifest ??= AefManifest.Create(SealedFiles.Select(p => new AefManifestEntry(p, Sha256(p), Size(p))));

    /// <summary>
    /// The run hash recomputed from the files present ([SEAL-4]): the SHA-256 of <see cref="Manifest"/>. Not always "the
    /// run's run hash": for a run with a seal valid against the reader schema that is the sealed value
    /// (<see cref="Integrity.SealVerifier.RunHashOf"/>).
    /// </summary>
    public string ComputeRunHash() => Manifest().RunHash;

    private Stream OpenRead(string path) =>
        new FileStream(FullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);

    private string FullPath(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    private void Require(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_present.Contains(path))
        {
            throw new FileNotFoundException($"{path} is not a file of the run {Root}.", path);
        }
    }

    [GeneratedRegex("^blobs/sha256/([0-9a-f]{2})/([0-9a-f]{64})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BlobPathPattern();
}
