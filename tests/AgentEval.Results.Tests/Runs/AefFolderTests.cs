using System.Diagnostics;
using System.Net.Sockets;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Tests.Runs;

/// <summary>
/// [RUN-3]: a run folder holds only regular files and folders; a link, pipe, socket or device is a <c>path</c> problem,
/// never followed or read. Git cannot carry such entries portably, so there is no corpus vector: these tests make them
/// where the operating system allows it.
/// </summary>
public sealed class AefFolderTests : IDisposable
{
    private readonly string _run = Path.Combine(Path.GetTempPath(), $"aef-folder-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"aef-outside-{Guid.NewGuid():N}");

    public AefFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_run, "blobs", "sha256", "ab"));
        File.WriteAllText(Path.Combine(_run, "run.json"), "{}");
        File.WriteAllText(Path.Combine(_run, "results.ndjson"), "");
        File.WriteAllText(Path.Combine(_run, "blobs", "sha256", "ab", "abcd"), "x");
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "not part of the run");
    }

    public void Dispose()
    {
        Directory.Delete(_run, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Fact]
    public void RegularFilesAndFolders_AreListedInByteOrder_WithNoProblem()
    {
        var listing = AefFolder.List(_run);

        Assert.Equal(new[] { "blobs/sha256/ab/abcd", "results.ndjson", "run.json" }, listing.Files);
        Assert.Empty(listing.Problems);
        Assert.Equal(AefEntryKind.Folder, AefFolder.KindOf(Path.Combine(_run, "blobs")));
        Assert.Equal(AefEntryKind.File, AefFolder.KindOf(Path.Combine(_run, "run.json")));
    }

    [Fact]
    public void ABadName_OrAHiddenFile_IsAPathProblem()
    {
        File.WriteAllText(Path.Combine(_run, ".DS_Store"), "");
        Directory.CreateDirectory(Path.Combine(_run, "ext", "Data"));
        File.WriteAllText(Path.Combine(_run, "ext", "Data", "x"), "");
        Directory.CreateDirectory(Path.Combine(_run, "ext", ".empty"));

        var listing = AefFolder.List(_run);

        // [RUN-3]'s rules are on the paths of files: the empty folder ext/.empty is ignored (W3-9, ruled 10-08).
        Assert.Contains(".DS_Store", listing.Files);
        Assert.Equal(new[] { ".DS_Store" }, listing.Problems.Select(p => p.Path));
    }

    [Fact]
    public void AFolderBreaksTheRules_OnlyThroughTheFilesInIt_AndACaseClashReportsTheFilesUnderTheLaterFolder()
    {
        Directory.CreateDirectory(Path.Combine(_run, "ext", "Data"));
        File.WriteAllText(Path.Combine(_run, "ext", "Data", "x"), "");
        Directory.CreateDirectory(Path.Combine(_run, ".hidden"));
        File.WriteAllText(Path.Combine(_run, ".hidden", "y"), "");
        var caseSensitive = !Directory.Exists(Path.Combine(_run, "ext", "dATA"));
        if (caseSensitive)
        {
            // Only a case-sensitive file system can hold the clash.
            Directory.CreateDirectory(Path.Combine(_run, "ext", "data"));
            File.WriteAllText(Path.Combine(_run, "ext", "data", "y"), "");
            File.WriteAllText(Path.Combine(_run, "ext", "data", "z"), "");
        }

        var listing = AefFolder.List(_run);

        // The files, never the folders .hidden or ext/data.
        Assert.Equal(
            caseSensitive ? new[] { ".hidden/y", "ext/data/y", "ext/data/z" } : new[] { ".hidden/y" },
            listing.Problems.Select(p => p.Path));
    }

    [Fact]
    public void ASymbolicLink_ToAFileOrAFolder_IsAPathProblem_AndIsNotFollowed()
    {
        if (!TryLink(Path.Combine(_run, "notes.txt"), Path.Combine(_outside, "secret.txt"), folder: false)
            || !TryLink(Path.Combine(_run, "ext"), _outside, folder: true))
        {
            return;   // this system does not let the test make links (Windows without the privilege)
        }

        var listing = AefFolder.List(_run);

        Assert.Equal(new[] { "blobs/sha256/ab/abcd", "results.ndjson", "run.json" }, listing.Files);   // nothing from outside
        Assert.Equal(new[] { new AefProblem("ext", "path"), new AefProblem("notes.txt", "path") }, listing.Problems);
        Assert.Equal(AefEntryKind.Link, AefFolder.KindOf(Path.Combine(_run, "ext")));
    }

    [Fact]
    public void APipe_IsAPathProblem_AndIsNeverOpened()
    {
        if (OperatingSystem.IsWindows() || !Run("mkfifo", Path.Combine(_run, "evidence.ndjson")))
        {
            return;   // no named pipes in a folder here
        }

        var listing = AefFolder.List(_run);   // opening the pipe would block until a writer came

        Assert.DoesNotContain("evidence.ndjson", listing.Files);
        Assert.Equal(new[] { new AefProblem("evidence.ndjson", "path") }, listing.Problems);
        Assert.Equal(AefEntryKind.Other, AefFolder.KindOf(Path.Combine(_run, "evidence.ndjson")));
    }

    [Fact]
    public void ASocket_IsAPathProblem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // a Windows AF_UNIX socket file is a reparse point: the link test covers that kind
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(_run, "gates.ndjson")));

        var listing = AefFolder.List(_run);

        Assert.Equal(new[] { new AefProblem("gates.ndjson", "path") }, listing.Problems);
        Assert.Equal(AefEntryKind.Other, AefFolder.KindOf(Path.Combine(_run, "gates.ndjson")));
    }

    [Fact]
    public void ADevice_IsOther()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/dev/null"))
        {
            return;
        }

        Assert.Equal(AefEntryKind.Other, AefFolder.KindOf("/dev/null"));
    }

    [Fact]
    public void OnUnix_TheGuardedNativeCall_PassesItsProbe_AndIsUsed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.False(AefFolder.NativeInUse);   // Windows never uses it
            return;
        }

        Assert.True(AefFolder.NativeInUse);
    }

    [Fact]
    public void TheFallback_StillTellsFilesFoldersAndLinks_WithoutTheNativeCall()
    {
        var linked = TryLink(Path.Combine(_run, "notes.txt"), Path.Combine(_outside, "secret.txt"), folder: false)
                     && TryLink(Path.Combine(_run, "ext"), _outside, folder: true);

        using (AefFolder.UseFallbackForTesting())
        {
            Assert.False(AefFolder.NativeInUse);
            var listing = AefFolder.List(_run);

            Assert.Equal(new[] { "blobs/sha256/ab/abcd", "results.ndjson", "run.json" }, listing.Files);   // nothing from outside
            Assert.Equal(AefEntryKind.Folder, AefFolder.KindOf(Path.Combine(_run, "blobs")));
            Assert.Equal(AefEntryKind.File, AefFolder.KindOf(Path.Combine(_run, "run.json")));
            if (linked)
            {
                Assert.Equal(new[] { new AefProblem("ext", "path"), new AefProblem("notes.txt", "path") }, listing.Problems);
                Assert.Equal(AefEntryKind.Link, AefFolder.KindOf(Path.Combine(_run, "ext")));
            }
        }

        Assert.Equal(!OperatingSystem.IsWindows(), AefFolder.NativeInUse);   // the switch is undone
    }

    [Fact]
    public void TheFallback_TakesAPipeForAFile_TheDocumentedTradeOff_ButNeverOpensItWhileListing()
    {
        if (OperatingSystem.IsWindows() || !Run("mkfifo", Path.Combine(_run, "evidence.ndjson")))
        {
            return;
        }

        using (AefFolder.UseFallbackForTesting())
        {
            var listing = AefFolder.List(_run);   // opening the pipe would block: listing returns

            Assert.Contains("evidence.ndjson", listing.Files);
            Assert.Empty(listing.Problems);
            Assert.Equal(AefEntryKind.File, AefFolder.FallbackKindOf(Path.Combine(_run, "evidence.ndjson")));
        }

        Assert.Equal(new[] { new AefProblem("evidence.ndjson", "path") }, AefFolder.List(_run).Problems);   // native: a path problem
    }

    [Fact]
    public void AnEntryThatIsGone_IsAnIOException_InTheFallbackToo()
    {
        using (AefFolder.UseFallbackForTesting())
        {
            Assert.ThrowsAny<IOException>(() => AefFolder.KindOf(Path.Combine(_run, "no-such-entry")));
        }
    }

    private static bool TryLink(string link, string target, bool folder)
    {
        try
        {
            if (folder)
                Directory.CreateSymbolicLink(link, target);
            else
                File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool Run(string program, string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, ArgumentList = { argument } });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
