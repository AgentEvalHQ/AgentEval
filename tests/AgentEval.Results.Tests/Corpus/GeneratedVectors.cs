using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>
/// Generated vectors (contracts/aef/1/spec/09-conformance.md, §9.2): a vector whose input would be too large for the
/// corpus holds only expected.json, with <c>generate</c>, steps applied in order to a copy of the vector's folder. As in
/// the conformance runner, generating is the runner's job: the driver is given the generated folder as it would be given
/// a stored one. Each vector is generated once per test run, under the temporary folder, and removed at exit.
/// </summary>
internal static class GeneratedVectors
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), $"aef-generated-{Guid.NewGuid():N}");
    private static readonly ConcurrentDictionary<string, Lazy<string>> Folders = new(StringComparer.Ordinal);

    static GeneratedVectors()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
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

    /// <summary>The folder vector <paramref name="id"/>'s recipe gives.</summary>
    public static string Of(string id, string vector, JsonArray steps) =>
        Folders.GetOrAdd(id, _ => new Lazy<string>(() => Generate(vector, steps))).Value;

    private static string Generate(string vector, JsonArray steps)
    {
        var folder = Path.Combine(Root, $"v{Folders.Count}-{Guid.NewGuid():N}");
        Copy(vector, folder);
        string At(JsonNode? relative) => Path.Combine(folder, ((string)relative!).Replace('/', Path.DirectorySeparatorChar));
        foreach (var step in steps)
        {
            var (op, arg) = step!.AsObject().Single();
            switch (op)
            {
                case "copy":
                    // SOURCE is relative to conformance/, TARGET to the vector's folder.
                    var source = Path.Combine(AefCorpus.Conformance, ((string)arg![0]!).Replace('/', Path.DirectorySeparatorChar));
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
                default:
                    throw new InvalidOperationException($"{vector}: a generate step this runner does not know: {op}");
            }
        }

        return folder;
    }

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
