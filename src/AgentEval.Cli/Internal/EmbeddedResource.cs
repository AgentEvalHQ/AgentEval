// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Cli.Internal;

internal static class EmbeddedResource
{
    /// <summary>Copies an embedded resource to the destination path if the file does not already exist.</summary>
    public static async Task CopyToAsync(string resourceFileName, string destinationPath)
    {
        if (File.Exists(destinationPath)) return;
        var asm = typeof(EmbeddedResource).Assembly;
        var fullName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("." + resourceFileName, StringComparison.OrdinalIgnoreCase));
        await using var src = asm.GetManifestResourceStream(fullName)!;
        await using var dst = File.Create(destinationPath);
        await src.CopyToAsync(dst);
    }
}
