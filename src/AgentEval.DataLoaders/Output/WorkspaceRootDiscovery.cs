// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

internal static class WorkspaceRootDiscovery
{
    /// <summary>Walks up from <paramref name="startDir"/> looking for a .sln/.slnx file or .git directory.</summary>
    public static string? Find(string startDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDir);

        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0 ||
                dir.GetFiles("*.slnx").Length > 0)
                return dir.FullName;

            if (dir.GetDirectories(".git").Length > 0)
                return dir.FullName;

            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Outcome of <see cref="CanonicaliseExistingDirectory"/>.</summary>
    public enum PathStatus
    {
        /// <summary>The path is blank (no override) or canonicalised to an existing directory.</summary>
        Ok,
        /// <summary>The path could not be canonicalised (malformed / too long / not supported).</summary>
        Malformed,
        /// <summary>The path canonicalised but no directory exists there.</summary>
        NotFound,
    }

    /// <summary>
    /// MNT-03: the single canonicalise-and-existence-check for an operator-supplied workspace-root path,
    /// shared by the CLI <c>--root</c> validator and the Mission Control <c>--workspace</c> startup path so
    /// the security-relevant canonicalisation rule lives in ONE place. Canonicalises via
    /// <see cref="Path.GetFullPath(string)"/> (stops accidental <c>../</c> traversal before downstream
    /// <c>Path.Combine</c>) and verifies the directory exists. Does NOT write output or exit — the caller
    /// decides how to surface the status (each has a slightly different message/exit contract).
    /// </summary>
    /// <param name="rawPath">Raw operator-supplied path; null/blank means "no override" → <see cref="PathStatus.Ok"/> with a null canonical.</param>
    /// <param name="basePath">
    /// Optional base directory for resolving a RELATIVE <paramref name="rawPath"/> (e.g. the shell's
    /// <c>PWD</c> for the Mission Control <c>--workspace</c> path). When null, a relative path resolves
    /// against the process current directory.
    /// </param>
    /// <returns>The canonical existing path (or null when blank), the status, and a detail (the exception
    /// message for <see cref="PathStatus.Malformed"/>, or the canonical path for <see cref="PathStatus.NotFound"/>).</returns>
    public static (string? Canonical, PathStatus Status, string? Detail) CanonicaliseExistingDirectory(
        string? rawPath, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return (null, PathStatus.Ok, null);

        string canonical;
        try
        {
            canonical = (basePath is null || Path.IsPathRooted(rawPath))
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(rawPath, basePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or System.Security.SecurityException or NotSupportedException)
        {
            return (null, PathStatus.Malformed, ex.Message);
        }

        if (!Directory.Exists(canonical))
            return (null, PathStatus.NotFound, canonical);

        return (canonical, PathStatus.Ok, null);
    }
}
