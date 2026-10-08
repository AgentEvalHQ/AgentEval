// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Cli.Commands;

/// <summary>Where one installed skill came from, as a <c>skills-lock.json</c> records it.</summary>
/// <param name="Source">The source, e.g. <c>chillicream/agent-skills</c>.</param>
/// <param name="SourceUrl">The source URL, when the lock file records one (the project file usually does not).</param>
/// <param name="Ref">The git ref or commit the skill was installed at, when recorded.</param>
internal sealed record SkillLockProvenance(string? Source, string? SourceUrl, string? Ref);

/// <summary>
/// Reads a project-scoped <c>skills-lock.json</c>, the file skill installers such as ChilliCream's <c>skills</c> CLI
/// write beside a project: <c>{"version": 1, "skills": {"&lt;name&gt;": {"source", "sourceType", "ref", "skillPath",
/// "computedHash"}}}</c>. Only the provenance fields are read; nothing here is trusted for a verdict.
/// </summary>
/// <remarks>
/// The scan must not fail because of a third-party file: a lock file that is not valid JSON is reported on stderr and
/// ignored. An entry whose name or provenance holds a control character is dropped (it would reach the terminal
/// verbatim in the console report), as the installer itself drops such entries when it reads the file.
/// </remarks>
internal static class SkillsLockFile
{
    public const string FileName = "skills-lock.json";

    /// <summary>
    /// The lock file for a scan of <paramref name="scannedRoot"/>: the first <c>skills-lock.json</c> in that directory
    /// or a parent of it, up to the repository root (the first directory holding <c>.git</c>). Without a repository
    /// root above it, only <paramref name="scannedRoot"/> itself is looked at.
    /// </summary>
    public static string? Find(string scannedRoot)
    {
        var start = new DirectoryInfo(scannedRoot);
        var repoRoot = start;
        while (repoRoot is not null && !Directory.Exists(Path.Combine(repoRoot.FullName, ".git")) && !File.Exists(Path.Combine(repoRoot.FullName, ".git")))
        {
            repoRoot = repoRoot.Parent;
        }

        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (repoRoot is null || string.Equals(dir.FullName, repoRoot.FullName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>The provenance of each skill in the lock file at <paramref name="path"/>, by skill name.</summary>
    public static IReadOnlyDictionary<string, SkillLockProvenance> Read(string path)
    {
        var result = new Dictionary<string, SkillLockProvenance>(StringComparer.Ordinal);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  Warning: {path} could not be read as a skills lock file ({ex.Message}); no provenance shown.");
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine($"  Warning: {path} has no \"skills\" object; no provenance shown.");
                return result;
            }

            var dropped = 0;
            foreach (var skill in skills.EnumerateObject())
            {
                if (skill.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var provenance = new SkillLockProvenance(
                    Text(skill.Value, "source"), Text(skill.Value, "sourceUrl"), Text(skill.Value, "ref"));
                if (provenance.Source is null && provenance.SourceUrl is null)
                {
                    continue;
                }

                if (HasControl(skill.Name, provenance.Source, provenance.SourceUrl, provenance.Ref))
                {
                    dropped++;
                    continue;
                }

                result[skill.Name] = provenance;
            }

            if (dropped > 0)
            {
                Console.Error.WriteLine($"  Warning: {dropped} entr(y/ies) in {path} hold control or formatting characters and were ignored.");
            }
        }

        return result;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    // Format characters (bidi overrides such as U+202E, zero-width joiners) are not control characters but still
    // rewrite what a terminal or a rendered report shows.
    private static bool HasControl(params string?[] values) =>
        values.Any(v => v is not null && v.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format));
}
