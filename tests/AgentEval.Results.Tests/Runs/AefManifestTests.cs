using System.Security.Cryptography;
using System.Text;
using AgentEval.Results.Runs;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Runs;

/// <summary>[SEAL-2]–[SEAL-4]: the manifest's lines, its order by the UTF-8 bytes of the whole path, and the run hash.</summary>
public sealed class AefManifestTests : IDisposable
{
    private const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aef-manifest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Entries_AreOrderedByTheUtf8BytesOfTheWholePath_NotSegmentBySegment()
    {
        // '-' (0x2D) < '.' (0x2E) < '/' (0x2F) < 'Z' (0x5A) < 'a' (0x61): ext/Z, ext/a-b, ext/a.b, ext/a/b. Segment by
        // segment would put ext/a/b before ext/a-b; ordinal UTF-16 agrees with UTF-8 here but not above U+FFFF.
        var manifest = AefManifest.Create(
        [
            new("ext/a/b", Empty, 0), new("ext/a.b", Empty, 0), new("ext/Z", Empty, 0), new("ext/a-b", Empty, 0),
            new("ext/\U0001F600", Empty, 0), new("ext/～", Empty, 0),
        ]);

        Assert.Equal(["ext/Z", "ext/a-b", "ext/a.b", "ext/a/b", "ext/～", "ext/\U0001F600"], manifest.Entries.Select(e => e.Path));
    }

    [Fact]
    public void ALine_IsTheDigest_TwoSpaces_TheSize_TwoSpaces_ThePath_AndLF()
    {
        var manifest = AefManifest.Create([new("run.json", Empty, 0), new("blobs/sha256/ab/x", new string('a', 64), 1234)]);

        Assert.Equal($"{new string('a', 64)}  1234  blobs/sha256/ab/x\n{Empty}  0  run.json\n", manifest.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.Text))).ToLowerInvariant(), manifest.RunHash);
    }

    [Fact]
    public void TheManifestOfNoFiles_IsEmpty_AndItsRunHashIsTheHashOfNothing()
    {
        var manifest = AefManifest.Create([]);

        Assert.Equal("", manifest.Text);
        Assert.Equal(Empty, manifest.RunHash);
    }

    [Theory]
    [InlineData("run.json", "run.json", Empty, Empty)]
    [InlineData("run.json", "x", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", Empty)]
    [InlineData("run.json", "x", "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Empty)]
    public void APathTwice_OrADigestNotInLowerCaseHex_IsRefused(string a, string b, string digestA, string digestB) =>
        Assert.Throws<ArgumentException>(() => AefManifest.Create([new(a, digestA, 0), new(b, digestB, 0)]));

    [Fact]
    public void TheFoldersManifest_SealsEveryFileButTheSealItsSignatureAndOverlays_OverTheirExactBytes()
    {
        Write("run.json", "{\"a\": \"ünïcode\"}\r\n");
        Write("ext/a/b", "x");
        Write("seal.json", "{}");
        Write("attestation.dsse.json", "{}");
        Write("overlays/events.ndjson", "");
        Write("overlays/seal-0001.json", "{}");

        var folder = AefRunFolder.Open(_dir);
        var manifest = folder.Manifest();

        Assert.Equal(["ext/a/b", "run.json"], folder.SealedFiles);
        var bytes = File.ReadAllBytes(Path.Combine(_dir, "run.json"));
        Assert.Equal(
            $"{Hex(SHA256.HashData("x"u8))}  1  ext/a/b\n{Hex(SHA256.HashData(bytes))}  {bytes.Length}  run.json\n",
            manifest.Text);
        Assert.Equal(manifest.RunHash, folder.ComputeRunHash());
    }

    [Fact]
    public void TheCorpusPathOrderVector_GivesItsExpectedManifest()
    {
        var vector = Path.Combine(AefCorpus.Conformance, "seal-vectors", "path-order");

        var manifest = AefRunFolder.Open(Path.Combine(vector, "run")).Manifest();

        Assert.Equal(File.ReadAllText(Path.Combine(vector, "expected-manifest.txt"), new UTF8Encoding(false)), manifest.Text);
    }

    [Fact]
    public void ABlobIsHashedAsAStream_AndItsSizeIsTheBytesHashed()
    {
        var blob = new byte[(3 << 20) + 7];   // more than one read buffer
        new Random(7).NextBytes(blob);
        var name = Hex(SHA256.HashData(blob));
        var path = AefRunFolder.BlobPath(name);
        Directory.CreateDirectory(Path.Combine(_dir, Path.GetDirectoryName(path)!));
        File.WriteAllBytes(Path.Combine(_dir, path), blob);

        var folder = AefRunFolder.Open(_dir);

        Assert.Equal(name, folder.Sha256(path));
        Assert.Equal(blob.Length, folder.Size(path));
        Assert.True(AefRunFolder.IsBlobPath(path, out var parsed) && parsed == name);
    }

    [Theory]
    [InlineData("blobs/sha256/63/635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5", true)]
    [InlineData("blobs/sha256/64/635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5", false)]   // folder is not the first two
    [InlineData("blobs/sha256/63/635221C9C64F48E2843E4186B0A1B66F07A1492C14DCB866BB83DBA6A5E5FEF5", false)]   // upper case
    [InlineData("blobs/sha256/63/635221c9", false)]
    [InlineData("blobs/sha512/63/635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5", false)]
    public void ABlobPath_IsWhereEvd3StoresIt(string path, bool blob) => Assert.Equal(blob, AefRunFolder.IsBlobPath(path, out _));

    private void Write(string path, string text)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
