// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// Searches everything a demo run left behind — the screen text, every file under the run folder (raw captures
/// included) and every entry of every zip — for strings that must never be there: the token the fake provider handed
/// out and the <c>Bearer </c> scheme prefix of an <c>Authorization</c> header. Bytes are decoded as UTF-8 and as
/// UTF-16, so a hit survives either encoding a writer may pick.
/// </summary>
internal static class LeakScanner
{
    /// <summary>The substrings no output may contain.</summary>
    public static readonly string[] Forbidden = [FakeTokenProvider.Token, "Bearer "];

    /// <summary>Every hit as <c>where: forbidden … context …</c>; empty when the run left nothing forbidden.</summary>
    /// <param name="screen">The text the demo wrote to its out and err writers.</param>
    /// <param name="roots">The run folder and anything else the run wrote (a zip beside it, for example); missing paths are skipped.</param>
    public static IReadOnlyList<string> Scan(string screen, params FileSystemInfo[] roots)
    {
        var hits = new List<string>();
        Check("screen", screen, Forbidden, hits);
        ScanFiles(Forbidden, roots, hits);
        return hits;
    }

    /// <summary>Every hit of <paramref name="forbidden"/> in the files under <paramref name="roots"/> (zip entries included); the screen is not scanned.</summary>
    /// <param name="forbidden">The substrings no file may contain.</param>
    /// <param name="roots">The run folder, a zip beside it…; missing paths are skipped.</param>
    public static IReadOnlyList<string> ScanFiles(string[] forbidden, params FileSystemInfo[] roots)
    {
        var hits = new List<string>();
        ScanFiles(forbidden, roots, hits);
        return hits;
    }

    private static void ScanFiles(string[] forbidden, FileSystemInfo[] roots, List<string> hits)
    {
        foreach (var root in roots)
        {
            root.Refresh();
            if (!root.Exists)
            {
                continue;
            }

            var files = root is DirectoryInfo directory
                ? directory.EnumerateFiles("*", SearchOption.AllDirectories)
                : [(FileInfo)root];
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file.FullName);
                CheckBytes(file.FullName, bytes, forbidden, hits);
                if (file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = ZipFile.OpenRead(file.FullName);
                    foreach (var entry in archive.Entries)
                    {
                        using var stream = entry.Open();
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        CheckBytes($"{file.FullName}!{entry.FullName}", buffer.ToArray(), forbidden, hits);
                    }
                }
            }
        }
    }

    private static void CheckBytes(string where, byte[] bytes, string[] forbiddenList, List<string> hits)
    {
        Check(where, Encoding.UTF8.GetString(bytes), forbiddenList, hits);
        Check(where + " (utf-16)", Encoding.Unicode.GetString(bytes), forbiddenList, hits);
    }

    private static void Check(string where, string text, string[] forbiddenList, List<string> hits)
    {
        foreach (var forbidden in forbiddenList)
        {
            var at = text.IndexOf(forbidden, StringComparison.Ordinal);
            if (at >= 0)
            {
                var start = Math.Max(0, at - 60);
                var length = Math.Min(text.Length - start, forbidden.Length + 120);
                hits.Add($"{where}: '{forbidden}' in …{text.Substring(start, length).ReplaceLineEndings(" ")}…");
            }
        }
    }
}
