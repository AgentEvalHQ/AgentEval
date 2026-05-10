using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval mc serve</c> (plan-08 MC1.7.1). Boots the Mission
/// Control web app — GraphQL + REST + SPA — on one port from any working
/// directory.
/// </summary>
/// <remarks>
/// <para>
/// Requires .NET 10. The CLI is multi-targeted (net8 / net9 / net10) but
/// <c>AgentEval.MissionControl</c> targets net10-only because Hot Chocolate 16
/// + the SPA static-asset pipeline depend on net10 features (<c>MapStaticAssets</c>).
/// On net8 / net9 builds, <see cref="RunAsync"/> prints a short help message
/// and returns exit code 2.
/// </para>
/// <para>
/// <b>Why a subprocess and not in-process hosting?</b>
/// Mission Control uses <c>Microsoft.NET.Sdk.Web</c> static web assets
/// (<c>MapStaticAssets</c>) which expect the entry assembly to BE the web
/// project — the SDK generates a <c>{EntryAssembly}.staticwebassets.endpoints.json</c>
/// manifest and <c>WebRootPath</c> defaults to <c>{ContentRoot}/wwwroot/</c>.
/// In-process hosting (CLI as entry assembly, MC types loaded as a library)
/// breaks that contract — the SPA's <c>index.html</c> + assets fail to
/// resolve. Spawning <c>AgentEval.MissionControl(.exe|.dll)</c> directly
/// puts MC back as the entry assembly with all SDK auto-wiring intact.
/// </para>
/// </remarks>
public static class McServeCommand
{
    public static async Task<int> RunAsync(int port, string? workspaceRoot)
    {
#if NET10_0_OR_GREATER
        var resolvedRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
        var agenteval = Path.Combine(resolvedRoot, ".agenteval");
        if (!Directory.Exists(agenteval))
        {
            Console.Error.WriteLine($"⚠  No .agenteval/ folder found at '{resolvedRoot}'.");
            Console.Error.WriteLine("    Run `agenteval init` first, or pass --workspace <path> to point at an initialized workspace.");
            // We still serve — the SPA renders a graceful empty / landing
            // state — but warn the user so they're not surprised.
        }

        // Pre-flight port check. Without it, a busy port surfaces as an
        // opaque Kestrel crash inside the subprocess + a misleading exit code.
        if (!TryProbePort(port, out var probeError))
        {
            Console.Error.WriteLine($"✖ Port {port} is not available: {probeError}");
            Console.Error.WriteLine($"    Try a different port: `agenteval mc serve --port {port + 1}`.");
            return 1;
        }

        // Locate the MC assembly + executable. Both ship next to the CLI's
        // own assembly because the CLI references AgentEval.MissionControl
        // (conditionally on net10).
        var cliDir = Path.GetDirectoryName(typeof(McServeCommand).Assembly.Location)!;
        var mcExeWindows = Path.Combine(cliDir, "AgentEval.MissionControl.exe");
        var mcDll        = Path.Combine(cliDir, "AgentEval.MissionControl.dll");

        // CRITICAL: keep the subprocess's working directory at the MC bin dir
        // (cliDir, since CLI + MC are co-located in bin). ASP.NET resolves
        // WebRootPath as `{ContentRootPath}/wwwroot/` and ContentRootPath
        // defaults to the process's CWD. If we set CWD to the workspace root,
        // wwwroot resolves to `{workspace}/wwwroot/` which doesn't exist.
        // The workspace itself is plumbed via the AgentEval__Root env var
        // — see McHost.ConfigureServices.
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows() && File.Exists(mcExeWindows))
        {
            psi = new ProcessStartInfo
            {
                FileName = mcExeWindows,
                WorkingDirectory = cliDir,
                UseShellExecute = false,
            };
        }
        else if (File.Exists(mcDll))
        {
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { mcDll },
                WorkingDirectory = cliDir,
                UseShellExecute = false,
            };
        }
        else
        {
            Console.Error.WriteLine($"✖ AgentEval.MissionControl assembly not found alongside CLI at '{cliDir}'.");
            Console.Error.WriteLine("    Expected: AgentEval.MissionControl.exe or AgentEval.MissionControl.dll");
            return 1;
        }

        psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        psi.Environment["AgentEval__Root"] = resolvedRoot;

        Console.WriteLine($"▶ Mission Control on http://localhost:{port}");
        Console.WriteLine($"  workspace: {resolvedRoot}");
        Console.WriteLine($"  graphql:   http://localhost:{port}/graphql");
        Console.WriteLine($"  rest:      http://localhost:{port}/api/v1/version");
        Console.WriteLine("  Ctrl+C to stop");

        // Wire Ctrl+C so it cleanly stops the child instead of orphaning it.
        // On Windows the console-control event reaches both processes (Kestrel
        // handles its own SIGINT), so we just need to wait for the child to
        // exit gracefully. If it doesn't exit within a short grace period we
        // kill the whole tree to avoid zombies.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // suppress immediate process termination
            cts.Cancel();
        };

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("⏹ Stopping Mission Control…");
                if (!proc.HasExited)
                {
                    using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await proc.WaitForExitAsync(graceCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(entireProcessTree: true); }
                        catch { /* best-effort */ }
                    }
                }
                return 0;
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✖ Failed to start Mission Control: {ex.Message}");
            return 1;
        }
#else
        // The MissionControl assembly isn't referenced on net8/net9 builds
        // (see AgentEval.Cli.csproj's conditional ProjectReference).
        await Task.CompletedTask;
        Console.Error.WriteLine("✖ `agenteval mc serve` requires .NET 10 or newer.");
        Console.Error.WriteLine("    Run with `dotnet --version` to check; install from https://dot.net.");
        _ = port; _ = workspaceRoot;
        return 2;
#endif
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// Probes the requested port with a short-lived bind. Returns true when
    /// the port is free; on failure populates <paramref name="error"/> with
    /// the reason ("address already in use", permission denied, etc.). Bound
    /// to localhost since that's all we serve on.
    /// </summary>
    private static bool TryProbePort(int port, out string error)
    {
        error = "";
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
#endif
}
