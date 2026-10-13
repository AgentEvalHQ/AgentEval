// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Tests.TestHelpers;

/// <summary>Retains only job evidence for independent runner verification when explicitly requested.</summary>
internal static class AefRunnerFixtures
{
    internal static void Retain(string source, string name, byte[]? plan = null, byte[]? manifest = null, bool budgetViolation = false)
    {
        if (Environment.GetEnvironmentVariable("AEF_RUNNER_FIXTURES") is not { Length: > 0 } fixtures
            || !File.Exists(Path.Combine(source, "events.ndjson"))) return;
        var destination = Path.Combine(fixtures, name);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        if (plan is not null) File.WriteAllBytes(Path.Combine(destination, "plan.json"), plan);
        if (manifest is not null) File.WriteAllBytes(Path.Combine(destination, "runner.json"), manifest);
        File.WriteAllText(Path.Combine(destination, "expected-stream.txt"), budgetViolation ? "budget-violation" : "valid");
    }
}
