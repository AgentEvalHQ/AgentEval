// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval mc doctor</c> (plan-08 MC1.7.4). Verifies that
/// Mission Control's runtime artefacts are co-located with the CLI and
/// that the SPA bundle is intact — useful diagnostic before <c>mc serve</c>
/// fails with a less-informative error.
/// </summary>
/// <remarks>
/// <para>
/// Sibling to <see cref="DoctorCommand"/>, which validates the
/// <c>.agenteval/</c> workspace itself (solution.json, run manifests,
/// audit chain). <see cref="DoctorCommand"/> doesn't know about MC; this
/// one doesn't know about workspaces. Run both to get a full picture:
/// <c>agenteval doctor</c> for the data, <c>agenteval mc doctor</c> for
/// the portal binaries.
/// </para>
/// <para>
/// Like <c>mc serve</c>, this requires .NET 10 — on net8/9 builds the
/// MissionControl assembly isn't referenced and the command exits 2.
/// </para>
/// </remarks>
public static class McDoctorCommand
{
    public static Task<int> RunAsync() => RunAsync(probeDirOverride: null);

    /// <summary>
    /// Test-friendly entry point that lets callers supply an explicit probe
    /// directory instead of inferring it from <c>Assembly.Location</c>.
    /// </summary>
    internal static Task<int> RunAsync(string? probeDirOverride)
    {
#if NET10_0_OR_GREATER
        var errors = 0;
        var warnings = 0;
        var ok = 0;

        // Resolve the probe directory. Prefer the override (tests) → assembly
        // location (normal `dotnet run` / `dotnet tool install`) → AppContext
        // .BaseDirectory (handles `PublishSingleFile=true` where Assembly
        // .Location returns empty string).
        string cliDir;
        if (!string.IsNullOrWhiteSpace(probeDirOverride))
        {
            cliDir = probeDirOverride;
        }
        else
        {
            var loc = typeof(McDoctorCommand).Assembly.Location;
            cliDir = !string.IsNullOrEmpty(loc)
                ? Path.GetDirectoryName(loc)!
                : AppContext.BaseDirectory;
        }
        Console.WriteLine($"  Probe directory: {cliDir}");

        // ─── 1. Mission Control assembly co-located ─────────────────────────
        var mcDll = Path.Combine(cliDir, "AgentEval.MissionControl.dll");
        var mcExe = Path.Combine(cliDir, "AgentEval.MissionControl.exe");
        if (File.Exists(mcDll))
        {
            Console.WriteLine("✔ AgentEval.MissionControl.dll present");
            ok++;
        }
        else
        {
            Console.Error.WriteLine($"✖ AgentEval.MissionControl.dll missing at {cliDir}.");
            Console.Error.WriteLine("    `agenteval mc serve` will fail. Re-publish the CLI on net10 to bundle MC.");
            errors++;
        }

        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(mcExe)) { Console.WriteLine("✔ AgentEval.MissionControl.exe present"); ok++; }
            else { Console.WriteLine("  ⚠ AgentEval.MissionControl.exe missing (Windows)."); warnings++; }
        }

        // ─── 2. SPA bundle — wwwroot, index.html, assets ────────────────────
        var wwwroot = Path.Combine(cliDir, "wwwroot");
        if (!Directory.Exists(wwwroot))
        {
            Console.Error.WriteLine($"✖ wwwroot/ missing at {cliDir}.");
            Console.Error.WriteLine("    The SPA hasn't been built. From the repo: `cd src/AgentEval.MissionControl.Spa && npm run build` (or `npm ci` first if dependencies aren't installed).");
            errors++;
        }
        else
        {
            Console.WriteLine("✔ wwwroot/ present");
            ok++;

            var indexHtml = Path.Combine(wwwroot, "index.html");
            if (File.Exists(indexHtml))
            {
                Console.WriteLine("✔ wwwroot/index.html present");
                ok++;
            }
            else
            {
                Console.Error.WriteLine("✖ wwwroot/index.html missing — the SPA build is incomplete.");
                errors++;
            }

            var assetsDir = Path.Combine(wwwroot, "assets");
            if (Directory.Exists(assetsDir))
            {
                var jsCount = Directory.GetFiles(assetsDir, "*.js").Length;
                var cssCount = Directory.GetFiles(assetsDir, "*.css").Length;
                if (jsCount > 0 && cssCount > 0)
                {
                    Console.WriteLine($"✔ wwwroot/assets/ has bundled output ({jsCount} JS, {cssCount} CSS)");
                    ok++;
                }
                else
                {
                    Console.Error.WriteLine($"✖ wwwroot/assets/ is missing JS or CSS output ({jsCount} JS, {cssCount} CSS).");
                    errors++;
                }
            }
            else
            {
                Console.Error.WriteLine("✖ wwwroot/assets/ missing — the SPA build is incomplete.");
                errors++;
            }
        }

        // ─── 3. Static-asset manifests (Microsoft.NET.Sdk.Web) ──────────────
        // The Web SDK emits one or both of these depending on the build flavour:
        //   - `…endpoints.json`     — Release / dotnet publish (used by MapStaticAssets)
        //   - `…runtime.json`        — Debug / dotnet build (used at runtime fallback)
        // Either is sufficient for the SPA to serve.
        var endpointsManifest = Path.Combine(cliDir, "AgentEval.MissionControl.staticwebassets.endpoints.json");
        var runtimeManifest   = Path.Combine(cliDir, "AgentEval.MissionControl.staticwebassets.runtime.json");
        if (File.Exists(endpointsManifest) || File.Exists(runtimeManifest))
        {
            Console.WriteLine("✔ Static-asset manifest present");
            ok++;
        }
        else
        {
            Console.WriteLine("  ⚠ Static-asset manifests missing — `MapStaticAssets` may fail at runtime.");
            warnings++;
        }

        // ─── 4. dotnet runtime on PATH (non-Windows subprocess path) ────────
        if (!OperatingSystem.IsWindows())
        {
            // McServeCommand spawns `dotnet AgentEval.MissionControl.dll` on
            // Linux/macOS (no native apphost). Probe `dotnet --version` so a
            // stripped image / minimal container surfaces a clear error.
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
                proc?.WaitForExit(2000);
                if (proc?.ExitCode == 0)
                {
                    Console.WriteLine("✔ dotnet runtime on PATH");
                    ok++;
                }
                else
                {
                    Console.Error.WriteLine("✖ dotnet runtime not on PATH — `mc serve` will fail to spawn the MC subprocess.");
                    errors++;
                }
            }
            catch
            {
                Console.Error.WriteLine("✖ dotnet runtime not on PATH — `mc serve` will fail to spawn the MC subprocess.");
                errors++;
            }
        }

        // ─── 5. Summary ──────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"Errors: {errors} | Warnings: {warnings} | OK: {ok}");

        return Task.FromResult(errors > 0 ? 2 : 0);
#else
        Console.Error.WriteLine("✖ `agenteval mc doctor` requires .NET 10 or newer.");
        Console.Error.WriteLine("    Mission Control's runtime is net10-only; the multi-targeted CLI's net8/9 builds don't bundle it.");
        return Task.FromResult(2);
#endif
    }
}
