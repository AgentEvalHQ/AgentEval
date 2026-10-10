using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>
/// Generated vectors (contracts/aef/1/spec/09-conformance.md, §9.2): a vector whose input would be too large for the
/// corpus holds only expected.json, with <c>generate</c>, steps applied in order to a copy of the vector's folder. As in
/// the conformance runner, generating is the runner's job: the driver is given the generated folder as it would be given
/// a stored one. Each vector is generated once per test run, under the temporary folder, and removed at exit. A
/// <c>link</c> step (round 7) makes a symbolic link; where this process cannot make one (Windows without the privilege),
/// the vector is skipped: its theory returns without judging it, as the runner reports it skipped. An
/// <c>ill-formed-name</c> step (pre-release) makes a file whose name is not a Unicode string: the byte 0xFF on Linux,
/// the unpaired surrogate U+DCFF on Windows; elsewhere the vector is skipped.
/// </summary>
internal static class GeneratedVectors
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), $"aef-generated-{Guid.NewGuid():N}");
    private static readonly ConcurrentDictionary<string, Lazy<string?>> Folders = new(StringComparer.Ordinal);
    private static readonly ConcurrentBag<byte[]> IllFormedFiles = [];

    static GeneratedVectors()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var raw in IllFormedFiles)
            {
                Unlink(raw);   // the base library cannot remove a file it cannot name
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }

    /// <summary>
    /// The folder vector <paramref name="id"/>'s recipe gives, or null when this platform cannot carry it out (a
    /// <c>link</c> step on a system that does not let this process make a symbolic link): the vector is skipped, as spec
    /// 09 §9.2.1 says a runner does, never failed.
    /// </summary>
    public static string? Of(string id, string vector, JsonArray steps) =>
        Folders.GetOrAdd(id, _ => new Lazy<string?>(() => Generate(vector, steps))).Value;

    private static string? Generate(string vector, JsonArray steps)
    {
        var folder = Path.Combine(Root, $"v{Folders.Count}-{Guid.NewGuid():N}");
        Copy(vector, folder);
        // Spec 09 §9.2.1 (round 6): a recipe's paths are relative, /-separated, with no .. segment, no drive and no leading
        // /; a runner refuses a recipe whose paths would leave the corpus or the vector (a backslash would be a separator
        // on Windows, so it is refused too).
        string Relative(JsonNode? node)
        {
            var text = (string)node!;
            return text.StartsWith('/') || text.Contains('\\', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal)
                   || text.Split('/').Contains("..")
                ? throw new InvalidOperationException($"{vector}: a generate path that would leave the corpus or the vector: {text}")
                : text.Replace('/', Path.DirectorySeparatorChar);
        }

        string At(JsonNode? relative) => Path.Combine(folder, Relative(relative));
        foreach (var step in steps)
        {
            var (op, arg) = step!.AsObject().Single();
            switch (op)
            {
                case "copy":
                    // SOURCE is relative to conformance/, TARGET to the vector's folder.
                    var source = Path.Combine(AefCorpus.Conformance, Relative(arg![0]));
                    Copy(source, At(arg[1]));
                    break;
                case "remove":
                    var target = At(arg);
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive: true);
                    }
                    else
                    {
                        File.Delete(target);
                    }

                    break;
                case "files":
                    var files = At(arg![0]);
                    Directory.CreateDirectory(files);
                    for (var i = 0; i < (int)arg[1]!; i++)
                    {
                        File.WriteAllBytes(Path.Combine(files, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), []);
                    }

                    break;
                case "write" or "append":
                    var path = At(arg![0]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var output = new FileStream(path, op == "write" ? FileMode.Create : FileMode.Append, FileAccess.Write))
                    {
                        foreach (var part in arg[1]!.AsArray())
                        {
                            var bytes = Encoding.UTF8.GetBytes((string)part![0]!);
                            for (var i = 0; i < (int)part[1]!; i++)
                            {
                                output.Write(bytes);
                            }
                        }
                    }

                    break;
                case "link":
                    // PATH becomes a symbolic link to TARGET, both relative to the vector's folder; the link holds the
                    // relative path from PATH's folder to TARGET (round 7). A platform that cannot make one skips the vector.
                    var link = At(arg![0]);
                    var linked = At(arg[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                    if (!TryLink(link, Path.GetRelativePath(Path.GetDirectoryName(link)!, linked), Directory.Exists(linked)))
                    {
                        return null;
                    }

                    break;
                case "ill-formed-name":
                    // An empty file in FOLDER named BEFORE, one ill-formed unit, then AFTER (spec 09 §9.2.1, pre-release).
                    var into = At(arg![0]);
                    Directory.CreateDirectory(into);
                    if (!TryIllFormedName(into, (string)arg[1]!, (string)arg[2]!))
                    {
                        return null;
                    }

                    break;
                default:
                    throw new InvalidOperationException($"{vector}: a generate step this runner does not know: {op}");
            }
        }

        return folder;
    }

    // A symbolic link holding `target` as written (a relative path), to a folder or a file: false when this system does
    // not let the process make one (Windows without the privilege or developer mode).
    private static bool TryLink(string link, string target, bool folder)
    {
        try
        {
            if (folder)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // A file whose name is not a Unicode string: on Linux the byte 0xFF (made by its raw name, which the base library
    // cannot write), on Windows the unpaired surrogate U+DCFF; false elsewhere (macOS refuses such names).
    private static bool TryIllFormedName(string folder, string before, string after)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(Path.Combine(folder, before + "\uDCFF" + after), []);
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        byte[] path = [.. Encoding.UTF8.GetBytes(Path.Combine(folder, before)), 0xFF, .. Encoding.UTF8.GetBytes(after), 0];
        const int writeOnly = 0x0001, create = 0x0020, closeOnExec = 0x0010;   // System.Native's flags
        var fd = Open(path, writeOnly | create | closeOnExec, Convert.ToInt32("644", 8));
        if (fd == -1)
        {
            return false;
        }

        new SafeFileHandle(fd, ownsHandle: true).Dispose();
        IllFormedFiles.Add(path);
        return true;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Open", SetLastError = true)]
    private static extern IntPtr Open(byte[] path, int flags, int mode);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Unlink", SetLastError = true)]
    private static extern int Unlink(byte[] path);

    // A file, or a folder with everything under it.
    private static void Copy(string source, string target)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            return;
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        Directory.CreateDirectory(target);
    }
}
