// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.Results.Ingestion;

namespace AgentEval.MissionControl.Hosting;

internal sealed class IngestionHttpOptions
{
    internal required byte[] KeyHash { get; init; }
    internal required AefStagingOptions Staging { get; init; }
    internal required int TimeoutSeconds { get; init; }

    internal static IngestionHttpOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("AgentEval:Ingestion");
        var key = section["ApiKey"];
        if (key is null || key.Length is < 32 or > 256 || key.Any(c => c < '!' || c > '~'))
            throw new InvalidOperationException("Expected: an ingestion ApiKey of 32..256 printable ASCII characters without spaces. " +
                "Actual: absent or invalid key. Suggestions: configure a random secret through the environment. Because: ingestion is a write endpoint.");
        var timeout = section.GetValue("TimeoutSeconds", 120);
        if (timeout is < 1 or > 600) throw new InvalidOperationException("Ingestion TimeoutSeconds must be 1..600.");
        var maxBytes = section.GetValue("MaxBytes", 64L << 20);
        return new()
        {
            KeyHash = SHA256.HashData(Encoding.ASCII.GetBytes(key)),
            TimeoutSeconds = timeout,
            Staging = new()
            {
                MaxFiles = section.GetValue("MaxFiles", 1_000),
                MaxBytes = maxBytes,
                MaxFileBytes = section.GetValue("MaxFileBytes", maxBytes),
                MaxConcurrentUploads = section.GetValue("MaxConcurrentUploads", 2)
            }
        };
    }
}
