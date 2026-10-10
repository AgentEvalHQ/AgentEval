using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AgentEval.Results.Runs;
using Microsoft.Win32.SafeHandles;

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

    private readonly List<byte[]> _rawFiles = [];

    public void Dispose()
    {
        foreach (var raw in _rawFiles)
        {
            Unlink(raw);   // the base library cannot remove a file it cannot name
        }

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
    public void UnderOverlays_NothingIsAPathProblem_AndLinksAreListedForTheChain()
    {
        // [RUN-3] (round 5): overlays/ is not checked by the rule; whatever it holds is the overlay chain's to report
        // ([OVL-5]). Its files are listed (the chain reads them); its links, pipes, sockets and devices are set apart.
        Directory.CreateDirectory(Path.Combine(_run, "overlays", ".hidden"));
        File.WriteAllText(Path.Combine(_run, "overlays", ".DS_Store"), "");
        File.WriteAllText(Path.Combine(_run, "overlays", ".hidden", "a b"), "");
        File.WriteAllText(Path.Combine(_run, "overlays", "events.ndjson"), "");
        var links = TryLink(Path.Combine(_run, "overlays", "seal-0001.json"), Path.Combine(_outside, "secret.txt"));

        var listing = AefFolder.List(_run);

        Assert.Empty(listing.Problems);
        Assert.Contains("overlays/.DS_Store", listing.Files);
        Assert.Contains("overlays/.hidden/a b", listing.Files);
        Assert.Equal(links ? ["overlays/seal-0001.json"] : [], listing.OverlayIrregular);
        Assert.DoesNotContain("overlays/seal-0001.json", listing.Files);
        Assert.False(listing.OverlaysOverLimit);
    }

    [Fact]
    public void AFileNamedOverlays_IsAPathProblemOfTheRun()
    {
        // [RUN-3] (round 7): overlays is a folder; an entry of that name that is a file is a path problem of the run.
        File.WriteAllText(Path.Combine(_run, "overlays"), "");

        var listing = AefFolder.List(_run);

        Assert.Contains("overlays", listing.Files);
        Assert.Equal(new[] { new AefProblem("overlays", "path") }, listing.Problems);
        Assert.Empty(listing.OverlayIrregular!);
    }

    [Fact]
    public void ALinkNamedOverlays_ToAFolder_IsAPathProblemOfTheRun_AndIsNotFollowed()
    {
        // [RUN-3] (round 7): a link named overlays, to a folder included (overlays on other storage are mounted, never
        // linked), is a path problem of the run; nothing behind it is listed, for the run or for the chain.
        Directory.CreateDirectory(Path.Combine(_outside, "deeper"));
        File.WriteAllText(Path.Combine(_outside, "events.ndjson"), "");
        if (!TryLink(Path.Combine(_run, "overlays"), _outside, folder: true))
        {
            return;   // this system does not let the test make links (Windows without the privilege)
        }

        var listing = AefFolder.List(_run);

        Assert.Equal(new[] { "blobs/sha256/ab/abcd", "results.ndjson", "run.json" }, listing.Files);
        Assert.Equal(new[] { new AefProblem("overlays", "path") }, listing.Problems);
        Assert.Empty(listing.OverlayIrregular!);
    }

    [Fact]
    public void ALinkNamedOverlays_ToAFile_IsAPathProblemOfTheRun()
    {
        if (!TryLink(Path.Combine(_run, "overlays"), Path.Combine(_outside, "secret.txt"), folder: false))
        {
            return;
        }

        Assert.Equal(new[] { new AefProblem("overlays", "path") }, AefFolder.List(_run).Problems);
    }

    [Fact]
    public void APathWhoseFirstSegmentIsOverlaysInAnotherCase_IsAPathProblemAtThatPath_BesideTheOverlaysFolder()
    {
        // [RUN-3] (round 7): beside overlays/, Overlays/x and a file OVERLAYS are path problems at their own paths (the
        // clash with overlays/ is reported outside it, which the rule checks). Only a case-sensitive file system can hold
        // both, so elsewhere (Windows, macOS by default) the test has nothing to build.
        Directory.CreateDirectory(Path.Combine(_run, "overlays"));
        File.WriteAllText(Path.Combine(_run, "overlays", "events.ndjson"), "");
        if (Directory.Exists(Path.Combine(_run, "OVERLAYS")))
        {
            return;   // a case-insensitive file system
        }

        Directory.CreateDirectory(Path.Combine(_run, "Overlays", "sub"));
        File.WriteAllText(Path.Combine(_run, "Overlays", "sub", "x"), "");
        File.WriteAllText(Path.Combine(_run, "OVERLAYS"), "");

        var listing = AefFolder.List(_run);

        Assert.Equal(new[] { new AefProblem("OVERLAYS", "path"), new AefProblem("Overlays/sub/x", "path") }, listing.Problems);
        Assert.Contains("overlays/events.ndjson", listing.Files);
    }

    [Fact]
    public void APathWhoseFirstSegmentIsOverlaysInAnotherCase_IsAPathProblem_InARunWithoutOverlaysToo()
    {
        // [RUN-3] (round 7) sets no condition on the run: Overlays/notes.txt in a run with no overlays folder is a path
        // problem all the same (overlays come after the seal, and would clash with it then). A file named OverlaysX, or
        // a folder overlays-old, is not: its first segment is another name.
        Directory.CreateDirectory(Path.Combine(_run, "Overlays"));
        File.WriteAllText(Path.Combine(_run, "Overlays", "notes.txt"), "");
        File.WriteAllText(Path.Combine(_run, "OverlaysX"), "");
        Directory.CreateDirectory(Path.Combine(_run, "overlays-old"));
        File.WriteAllText(Path.Combine(_run, "overlays-old", "y"), "");

        var listing = AefFolder.List(_run);

        Assert.Equal(new[] { new AefProblem("Overlays/notes.txt", "path") }, listing.Problems);
    }

    [Theory]
    [InlineData("overlays", true)]
    [InlineData("OVERLAYS", true)]
    [InlineData("Overlays/x", true)]
    [InlineData("oVeRlAyS/a/b", true)]
    [InlineData("overlays/events.ndjson", false)]   // under overlays/: the chain's, never the run's
    [InlineData("overlays/seal-0001.json", false)]
    [InlineData("overlaysx", false)]
    [InlineData("ext/overlays", false)]
    [InlineData("ext/Overlays/x", false)]
    [InlineData("overlay", false)]
    public void TheOverlaysBoundary_IsTheFirstSegmentOverlaysInAnyCase_OutsideOverlays(string path, bool breaks)
    {
        Assert.Equal(breaks, AefFolder.BreaksTheOverlaysBoundary(path));
    }

    // A symbolic link, where the operating system lets this process make one.
    private static bool TryLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
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

    // ------------------------------------------------------------------ names that are not Unicode strings (§3.9)

    [Theory]
    [InlineData(new byte[] { 0xFF }, "\uFFFD")]                       // one byte that is never UTF-8
    [InlineData(new byte[] { 0xFF, 0xFE }, "\uFFFD\uFFFD")]           // two maximal ill-formed subsequences
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98 }, "\uFFFD")]            // a four-byte sequence cut short: one
    [InlineData(new byte[] { 0xC3, 0x28 }, "\uFFFD(")]                 // a lead byte without its continuation
    public void OnLinux_ANameThatIsNotUtf8_IsListedUnderItsSpellingWithUFFFD_AsAPathProblem_AndNeverRead(byte[] illFormed, string spelled)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var name = $"n{spelled}o.txt";
        RawFile(_run, illFormed, "x"u8.ToArray());

        var listing = AefFolder.List(_run);

        Assert.DoesNotContain(name, listing.Files);   // no reader opens it
        Assert.Equal([name], listing.IllFormed!);
        Assert.Equal([new AefProblem(name, "path")], listing.Problems);
    }

    [Fact]
    public void OnLinux_UnderOverlays_SuchANameIsNoProblemOfTheRun_ButTheChainsUnexpectedFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_run, "overlays"));
        RawFile(Path.Combine(_run, "overlays"), [0xFF], []);

        var listing = AefFolder.List(_run);

        Assert.Empty(listing.Problems);
        Assert.Equal(["overlays/n\uFFFDo.txt"], listing.OverlayIrregular!);
        Assert.Equal([new AefProblem("overlays/n\uFFFDo.txt", "unexpected-file")],
            AgentEval.Results.Integrity.OverlayChain.Verify(AefRunFolder.Open(_run), null, "", null).Problems);
    }

    [Fact]
    public void OnLinux_TwoNamesWithOneSpelling_AreEachListed_AndEachReported()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        RawFile(_run, [0xFF], []);
        RawFile(_run, [0xFE], []);   // n\xFEo.txt and n\xFFo.txt are both n\uFFFDo.txt

        var listing = AefFolder.List(_run);

        Assert.Equal(["n\uFFFDo.txt", "n\uFFFDo.txt"], listing.IllFormed!);
        Assert.Equal([new AefProblem("n\uFFFDo.txt", "path"), new AefProblem("n\uFFFDo.txt", "path")], listing.Problems);
    }

    [Fact]
    public void OnLinux_ASealedRunWithSuchAName_IsInvalid_NotSealedAndAPathProblem_OncePerEntry()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A sealed run of the corpus, copied, with two such files at its root.
        var source = Path.Combine(Corpus.AefCorpus.Conformance, "valid", "completed-eval", "run");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(_run, "sealed", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var run = Path.Combine(_run, "sealed");
        RawFile(run, [0xFF], "x"u8.ToArray());
        RawFile(run, [0xFE], "y"u8.ToArray());

        var verification = AgentEval.Results.Integrity.AefRunVerifier.Verify(run);

        Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Invalid, verification.Outcome);
        Assert.Equal(
            [new AefProblem("n\uFFFDo.txt", "not-sealed"), new AefProblem("n\uFFFDo.txt", "not-sealed"),
             new AefProblem("n\uFFFDo.txt", "path"), new AefProblem("n\uFFFDo.txt", "path")],
            verification.Problems);
        // A sealer refuses such a run: its paths are not ones a seal can name ([SEAL-1], [RUN-3]).
        File.Delete(Path.Combine(run, "seal.json"));
        File.Delete(Path.Combine(run, "attestation.dsse.json"));
        var refused = Assert.Throws<InvalidOperationException>(() => AgentEval.Results.Writing.AefSealer.Seal(
            run, new AgentEval.Results.Writing.AefSealOptions { SealedBy = AgentEval.Results.Writing.AefSealedBy.Ingest }));
        Assert.Contains("n\uFFFDo.txt path", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWindows_ANameWithAnUnpairedSurrogate_IsListedUnderItsSpellingWithUFFFD_AsAPathProblem_AndNeverRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllBytes(Path.Combine(_run, "n\uDCFFo.txt"), "x"u8.ToArray());

        var listing = AefFolder.List(_run);

        Assert.DoesNotContain("n\uFFFDo.txt", listing.Files);
        Assert.Equal(["n\uFFFDo.txt"], listing.IllFormed!);
        Assert.Equal([new AefProblem("n\uFFFDo.txt", "path")], listing.Problems);
    }

    // A file in folder named n, the ill-formed bytes, then o.txt, made by its raw name (the base library cannot).
    private void RawFile(string folder, byte[] illFormed, byte[] content)
    {
        byte[] path = [.. Encoding.UTF8.GetBytes(Path.Combine(folder, "n")), .. illFormed, .. "o.txt"u8, 0];
        const int writeOnly = 0x0001, create = 0x0020, closeOnExec = 0x0010;   // System.Native's flags
        var fd = Open(path, writeOnly | create | closeOnExec, Convert.ToInt32("644", 8));
        Assert.NotEqual(-1, fd);
        using (var stream = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Write, 1))
        {
            stream.Write(content);
        }

        _rawFiles.Add(path);
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Open", SetLastError = true)]
    private static extern IntPtr Open(byte[] path, int flags, int mode);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Unlink", SetLastError = true)]
    private static extern int Unlink(byte[] path);

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
