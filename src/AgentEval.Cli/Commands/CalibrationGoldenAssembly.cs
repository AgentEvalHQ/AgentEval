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
/// The golden <c>*.jsonl</c> calibration datasets ship inside this CLI assembly, so
/// <c>agenteval bench {gdpr|eu-ai-act|agentic} calibrate</c> works from the installed tool. Users are told to
/// calibrate the judges for their own model; through 0.42.0-beta the goldens were embedded only in
/// <c>AgentEval.Tests.dll</c>, so calibrate failed anywhere but a built source tree.
/// </para>
/// <para>
/// The files themselves stay in the test project (one source of truth); <c>AgentEval.Cli.csproj</c> embeds the same
/// files under logical names that keep the fragment each loader filters on (for example
/// <c>.Compliance.Gdpr.Calibration.Golden.</c>).
/// </para>
/// </remarks>
internal static class CalibrationGoldenAssembly
{
    private const string GoldenFragment = ".Calibration.Golden.";

    /// <summary>
    /// Returns the assembly carrying the embedded golden datasets: this CLI assembly, or <c>AgentEval.Tests</c> as a
    /// fallback for an unusual build that left them out. <c>null</c> when neither carries them, in which case
    /// <see cref="NotFoundMessage"/> explains why.
    /// </summary>
    public static Assembly? TryLocate()
    {
        var cli = typeof(CalibrationGoldenAssembly).Assembly;
        if (CarriesGoldens(cli)) return cli;

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AgentEval.Tests");
        if (loaded is not null && CarriesGoldens(loaded)) return loaded;

        var thisDir = Path.GetDirectoryName(cli.Location);
        if (thisDir is null) return null;

        var candidate = Path.Combine(thisDir, "AgentEval.Tests.dll");
        if (!File.Exists(candidate)) return null;

        var tests = Assembly.LoadFrom(candidate);
        return CarriesGoldens(tests) ? tests : null;
    }

    private static bool CarriesGoldens(Assembly assembly) =>
        assembly.GetManifestResourceNames().Any(n =>
            n.Contains(GoldenFragment, StringComparison.OrdinalIgnoreCase) &&
            n.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));

    /// <summary>Actionable message for when no assembly carries the calibration golden datasets.</summary>
    public const string NotFoundMessage =
        "Could not locate the calibration golden datasets. They are embedded in the AgentEval CLI; this build " +
        "appears to have been made without them. Reinstall the tool (`dotnet tool update -g AgentEval.Cli --prerelease`) " +
        "or run from a built source tree (`dotnet run --project src/AgentEval.Cli -- bench <family> calibrate`).";
}
