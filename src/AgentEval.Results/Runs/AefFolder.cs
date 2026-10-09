// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace AgentEval.Results.Runs;

/// <summary>What one entry of a run folder is. Only a regular file or a folder may be in a run ([RUN-3]).</summary>
public enum AefEntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A folder (not a link to one).</summary>
    Folder,

    /// <summary>A symbolic link or another reparse point, to a file or a folder: never followed.</summary>
    Link,

    /// <summary>A pipe, a socket or a device: never opened (a pipe would block a reader, a device never ends).</summary>
    Other,
}

/// <summary>
/// The regular files of a run folder, in byte order of their paths, and the <c>path</c> problems of [RUN-3]: over the
/// paths of files (a folder breaks the rules only through the files in it, and for a case clash the files under the
/// later folder are reported; an empty folder is ignored), and every symbolic link, pipe, socket or device. When the
/// folder holds more files than [ENC-17] allows (not counting <c>seal.json</c>, <c>attestation.dsse.json</c> and
/// <c>overlays/</c>), the walk stops: <see cref="Problems"/> is the one <c>limit</c> problem
/// at <c>.</c> ([ENC-18]) and <see cref="Files"/> is not the whole folder.
/// </summary>
/// <remarks>
/// <c>overlays/</c> is not checked by [RUN-3] (round 5): it grows after the run is sealed, so whatever it holds is the
/// overlay chain's to report ([OVL-5]), never a problem of the run. Its regular files are in <see cref="Files"/> (the
/// chain reads them) but take no part in <see cref="Problems"/>, not even in a case clash; its links, pipes, sockets and
/// devices are in <see cref="OverlayIrregular"/>, for the overlay verifier to report as <c>unexpected-file</c>. When it
/// holds more files than one events file and two per batch ([ENC-17]: 19,999), <see cref="OverlaysOverLimit"/> is set:
/// the overlay verifier reports <c>limit</c> at <c>overlays</c> once and still checks the chain from the files it names
/// ([OVL-5]). Every file is listed all the same (R4N-8: the listing a count needs is not bounded).
/// </remarks>
/// <param name="Files">The regular files, by path, in byte order.</param>
/// <param name="Problems">The <c>path</c> problems of [RUN-3] outside <c>overlays/</c>, or the one <c>limit</c> at <c>.</c>.</param>
/// <param name="OverlaysOverLimit">More than 19,999 files under <c>overlays/</c>.</param>
/// <param name="OverlayIrregular">The entries under <c>overlays/</c> that are not regular files nor folders, in byte order.</param>
public sealed record AefFolderListing(
    IReadOnlyList<string> Files, IReadOnlyList<AefProblem> Problems, bool OverlaysOverLimit = false, IReadOnlyList<string>? OverlayIrregular = null);

/// <summary>
/// Lists a run folder as [RUN-3] (contracts/aef/1/spec/03-run.md) allows it: every entry is a regular file or a
/// folder; a symbolic link, device, pipe or socket is a <c>path</c> problem and is never followed or read (a link can
/// point outside the run; a pipe would block a reader).
/// </summary>
/// <remarks>
/// <para>
/// How an entry's type is found, and the trade-off. Windows: <see cref="File.GetAttributes(string)"/> reads the entry
/// itself (a link is a reparse point, a device has <see cref="FileAttributes.Device"/>). Unix: the base library shows a
/// link (<see cref="FileAttributes.ReparsePoint"/>, <see cref="FileSystemInfo.LinkTarget"/>) but reads a pipe, a socket
/// and a device as a normal file, and opening a pipe to find out would block. So this class calls <c>lstat</c> through
/// <c>SystemNative_LStat</c>, an export of the runtime's own native shim (<c>libSystem.Native</c>, the one the base
/// library's file APIs use). That export is not a documented API: its name or its <c>FileStatus</c> layout could change
/// in a later runtime.
/// </para>
/// <para>
/// So the call is guarded. Before the first use, the export is probed on the file system's root (<c>/</c>), which must
/// come back as a folder: a missing library or export (<see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/>),
/// a marshalling failure, a <c>FileStatus</c> larger than the buffer (the buffer's tail is a canary the probe checks),
/// or a mode that does not say "folder" switches this process to the fallback for good. A single call that fails later
/// (it returns an error) falls back for that entry only.
/// </para>
/// <para>
/// The fallback is what the base library can tell: a link through <see cref="FileSystemInfo.LinkTarget"/> and the
/// <see cref="FileAttributes.ReparsePoint"/> attribute, a folder through <see cref="FileAttributes.Directory"/>, and
/// every other entry taken as a regular file. Listing never opens an entry, so the listing itself never blocks. What the
/// fallback loses: a pipe, socket or device is not reported as <c>path</c>, and a reader that later opens such an
/// "entry" may block (a pipe) or fail (a socket). A device that never ends is bounded by the readers of this library,
/// which never read more than a file's size at listing plus one byte (<see cref="AefRunFolder"/>).
/// </para>
/// </remarks>
public static class AefFolder
{
    // Room for System.Native's FileStatus (about 130 bytes today); only its first two fields are read. The tail beyond
    // FileStatusBytes is a canary: a FileStatus that grew past FileStatusBytes would write into it.
    private const int FileStatusBytes = 512;
    private const int CanaryBytes = 512;
    private const byte Canary = 0xA5;

    // The file type bits of FileStatus.Mode, as System.Native gives them (its own constants, the same on every Unix).
    private const int TypeMask = 0xF000;
    private const int TypeRegular = 0x8000;
    private const int TypeDirectory = 0x4000;
    private const int TypeLink = 0xA000;

    // 0: not probed yet; 1: the native call works; -1: it does not, the fallback is used for the rest of the process.
    private static int s_native;

    // A test switch: the fallback for the current async flow only, so tests running in parallel are not affected.
    private static readonly AsyncLocal<bool> s_fallbackForTesting = new();

    /// <summary>Lists the folder (see <see cref="AefFolderListing"/>).</summary>
    /// <exception cref="IOException">The folder or an entry cannot be read.</exception>
    public static AefFolderListing List(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,   // folders are walked here, so a link to one is never entered
            IgnoreInaccessible = false,
            AttributesToSkip = 0,            // hidden and system entries are entries too
            ReturnSpecialDirectories = false,
        };

        var files = new List<string>();
        var irregular = new List<string>();
        var counted = 0;          // [ENC-17]: the files that count toward the limit
        var overlayEntries = 0;   // [ENC-17]: the files under overlays/, one events file and two per batch at most
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((folder, ""));
        while (pending.TryPop(out var current))
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(current.Full, "*", options))
            {
                var name = Path.GetFileName(full);
                var relative = current.Relative.Length == 0 ? name : $"{current.Relative}/{name}";
                var kind = KindOf(full);
                if (kind == AefEntryKind.Folder)
                {
                    pending.Push((full, relative));
                    continue;
                }

                (kind == AefEntryKind.File ? files : irregular).Add(relative);
                overlayEntries += IsUnderOverlays(relative) ? 1 : 0;
                counted += CountsTowardTheLimit(relative) ? 1 : 0;
                if (counted > AefLimits.MaxFiles)
                {
                    return new AefFolderListing([.. files.Order(AefProblemOrder.Utf8)], [new AefProblem(".", "limit")]);
                }
            }
        }

        // [RUN-3]'s rules are on the paths of files; a link, pipe, socket or device is a path problem whatever its name.
        // overlays/ is not checked by them (round 5): whatever it holds is the overlay chain's to report ([OVL-5]). Its
        // files are still listed whatever their number (R4N-8), for the chain to read.
        var runFiles = files.Concat(irregular).Where(p => !IsUnderOverlays(p));
        var runIrregular = irregular.Where(p => !IsUnderOverlays(p));
        var problems = AefPaths.Check(runFiles).Concat(runIrregular.Select(p => new AefProblem(p, "path"))).Distinct();
        return new AefFolderListing(
            [.. files.Order(AefProblemOrder.Utf8)],
            AefProblemOrder.Sort(problems),
            overlayEntries > AefLimits.MaxOverlayFiles,
            [.. irregular.Where(IsUnderOverlays).Order(AefProblemOrder.Utf8)]);
    }

    /// <summary>Whether <paramref name="path"/> is under <c>overlays/</c>, which [RUN-3] does not check ([OVL-5] does).</summary>
    public static bool IsUnderOverlays(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith("overlays/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a file counts toward the 100,000 of [ENC-17]: every file but <c>seal.json</c>, <c>attestation.dsse.json</c>
    /// and those under <c>overlays/</c>, so a run at the limit can still be sealed and take overlays.
    /// </summary>
    public static bool CountsTowardTheLimit(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path is not ("seal.json" or "attestation.dsse.json") && !path.StartsWith("overlays/", StringComparison.Ordinal);
    }

    /// <summary>What the entry at <paramref name="path"/> is, without following it if it is a link.</summary>
    /// <exception cref="IOException">The entry cannot be read.</exception>
    public static AefEntryKind KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            // File.GetAttributes reads the entry itself, not a link's target.
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0 ? AefEntryKind.Link
                : (attributes & FileAttributes.Directory) != 0 ? AefEntryKind.Folder
                : (attributes & FileAttributes.Device) != 0 ? AefEntryKind.Other
                : AefEntryKind.File;
        }

        if (!s_fallbackForTesting.Value && NativeUsable() && TryNativeKind(path) is { } kind)
        {
            return kind;
        }

        return FallbackKindOf(path);
    }

    /// <summary>
    /// Whether entry types are found through <c>lstat</c> here (false on Windows, where it is never used, when the
    /// native export was found unusable, and under <see cref="UseFallbackForTesting"/>).
    /// </summary>
    internal static bool NativeInUse => !OperatingSystem.IsWindows() && !s_fallbackForTesting.Value && NativeUsable();

    /// <summary>
    /// For tests: lists entries with the fallback (the base library alone) in the current async flow until the result
    /// is disposed, as if the native export could not be used.
    /// </summary>
    internal static IDisposable UseFallbackForTesting()
    {
        var before = s_fallbackForTesting.Value;
        s_fallbackForTesting.Value = true;
        return new Restore(() => s_fallbackForTesting.Value = before);
    }

    /// <summary>The fallback: what the base library tells without opening the entry (see the remarks of <see cref="AefFolder"/>).</summary>
    internal static AefEntryKind FallbackKindOf(string path)
    {
        // FileInfo reads the entry itself (lstat on Unix); for a folder path it reads the folder's attributes too.
        var info = new FileInfo(path);
        var attributes = info.Attributes;
        if ((int)attributes == -1)
        {
            throw new IOException($"{path}: the entry is gone.");
        }

        return info.LinkTarget is not null || (attributes & FileAttributes.ReparsePoint) != 0 ? AefEntryKind.Link
            : (attributes & FileAttributes.Directory) != 0 ? AefEntryKind.Folder
            : (attributes & FileAttributes.Device) != 0 ? AefEntryKind.Other
            : AefEntryKind.File;
    }

    // Whether the native export may be used: probed once per process on the file system's root, a folder on every Unix.
    // It must say "folder" and must not write past FileStatusBytes; anything else switches to the fallback for good.
    private static bool NativeUsable()
    {
        if (Volatile.Read(ref s_native) == 0)
        {
            ProbeNative("/");
        }

        return Volatile.Read(ref s_native) > 0;
    }

    private static void ProbeNative(string folder)
    {
        bool works;
        try
        {
            works = CallLStat(folder, out var mode) == 0 && (mode & TypeMask) == TypeDirectory;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                      or MarshalDirectiveException or InvalidOperationException)
        {
            works = false;
        }

        Interlocked.CompareExchange(ref s_native, works ? 1 : -1, 0);
    }

    // The entry's kind through lstat, or null when the call fails (the caller falls back for this entry).
    private static AefEntryKind? TryNativeKind(string path)
    {
        int mode;
        try
        {
            if (CallLStat(path, out mode) != 0)
            {
                return null;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                      or MarshalDirectiveException or InvalidOperationException)
        {
            Volatile.Write(ref s_native, -1);
            return null;
        }

        return (mode & TypeMask) switch
        {
            TypeRegular => AefEntryKind.File,
            TypeDirectory => AefEntryKind.Folder,
            TypeLink => AefEntryKind.Link,
            _ => AefEntryKind.Other,
        };
    }

    // Calls SystemNative_LStat into a buffer whose tail is a canary; FileStatus starts with Flags and Mode (two int32s).
    // Throws InvalidOperationException when the canary was overwritten: the FileStatus layout is not the one expected.
    private static int CallLStat(string path, out int mode)
    {
        var status = new byte[FileStatusBytes + CanaryBytes];
        status.AsSpan(FileStatusBytes).Fill(Canary);
        var result = NativeMethods.LStat(path, status);
        if (status.AsSpan(FileStatusBytes).IndexOfAnyExcept(Canary) >= 0)
        {
            throw new InvalidOperationException("SystemNative_LStat wrote past the FileStatus this code expects.");
        }

        mode = MemoryMarshal.Read<int>(status.AsSpan(sizeof(int)));
        return result;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static class NativeMethods
    {
        [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
        public static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] status);
    }
}
