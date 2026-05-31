// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Locates the assembly that carries the embedded calibration golden datasets (ARC-09).
/// </summary>
/// <remarks>
/// <para>
/// The golden <c>*.jsonl</c> calibration datasets are <c>EmbeddedResource</c>s of <c>AgentEval.Tests</c>,
/// so the <c>agenteval bench {gdpr|eu-ai-act|agentic} calibrate</c> commands load them by reflecting over
/// that assembly. This makes <c>calibrate</c> a MAINTAINER / CI-only command: it is meant to be run from a
/// built solution tree (where <c>AgentEval.Tests.dll</c> sits next to the CLI), not from the published
/// <c>AgentEval</c> NuGet package — the goldens are intentionally test fixtures, not a shipped product
/// dataset. Previously each command duplicated this resolver and emitted an opaque "Could not locate
/// AgentEval.Tests assembly" error; this type centralises the lookup and the contract.
/// </para>
/// <para>
/// Promoting the goldens into a shipped product dataset (so calibrate works from the published CLI) is a
/// larger change tracked as vNext; until then the contract is explicit and the failure message actionable.
/// </para>
/// </remarks>
internal static class CalibrationGoldenAssembly
{
    /// <summary>
    /// Returns the assembly carrying the embedded golden datasets (currently <c>AgentEval.Tests</c>), or
    /// <c>null</c> when it cannot be located — in which case <see cref="NotFoundMessage"/> explains why.
    /// </summary>
    public static Assembly? TryLocate()
    {
        // Already loaded (the common in-process / test-host case).
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AgentEval.Tests");
        if (loaded is not null) return loaded;

        // Next to this binary (the built-solution-tree case the calibrate workflow runs in).
        var thisDir = Path.GetDirectoryName(typeof(CalibrationGoldenAssembly).Assembly.Location);
        if (thisDir is null) return null;

        var candidate = Path.Combine(thisDir, "AgentEval.Tests.dll");
        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    }

    /// <summary>Actionable message for when the golden-carrying assembly cannot be located.</summary>
    public const string NotFoundMessage =
        "Could not locate the calibration golden datasets (AgentEval.Tests assembly). " +
        "`calibrate` is a maintainer / CI command: run it from a built solution tree (so AgentEval.Tests.dll " +
        "sits next to the CLI), e.g. `dotnet run --project src/AgentEval.Cli -- bench <family> calibrate`, " +
        "not from the published AgentEval NuGet package.";
}
