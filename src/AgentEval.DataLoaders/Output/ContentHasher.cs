// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;

namespace AgentEval.Output;

internal static class ContentHasher
{
    /// <summary>Computes a SHA-256 hash over the canonical run files for deterministic content verification.</summary>
    public static async Task<string> HashRunAsync(
        FileSystemLayout layout,
        SubjectIdentity subject,
        string runId,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var summaryFile = layout.SummaryFile(subject, runId);
        if (File.Exists(summaryFile))
        {
            var bytes = await File.ReadAllBytesAsync(summaryFile, ct);
            hash.AppendData(bytes);
        }

        var scenariosDir = layout.ScenariosDir(subject, runId);
        if (Directory.Exists(scenariosDir))
        {
            var scenarioFiles = Directory.GetFiles(scenariosDir, "*.json")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray();

            foreach (var file in scenarioFiles)
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                hash.AppendData(bytes);
            }
        }

        var traceFile = layout.TraceFile(subject, runId);
        if (File.Exists(traceFile))
        {
            var bytes = await File.ReadAllBytesAsync(traceFile, ct);
            hash.AppendData(bytes);
        }

        var finalHash = hash.GetHashAndReset();
        return Convert.ToHexString(finalHash).ToLowerInvariant();
    }
}
