// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The Microsoft 365 agent demo: readiness, one prompt and a small benchmark against a Microsoft 365 Copilot or
/// declarative agent over Work IQ A2A 1.0, with every trace and issue on the screen at the chosen verbosity and in a run
/// folder. <c>dotnet run --project samples/AgentEval.Microsoft365Demo -- --help</c> lists every option.
/// </summary>
public static class Program
{
    /// <summary>Runs the demo with the process console and environment; Ctrl+C stops the stage in progress and still writes the run folder.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The exit code: 0, 1, 2, 3 or 11 (see <c>--help</c>).</returns>
    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // The first Ctrl+C stops the run gracefully (the run folder is still written); a second one ends the process.
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        };

        Encoding? originalEncoding = null;
        try
        {
            if (!Console.IsOutputRedirected && Console.OutputEncoding.CodePage != Encoding.UTF8.CodePage)
            {
                // ✓ ✗ – and the arrows need UTF-8 on a console that defaults to a legacy code page; restored on exit. A
                // redirected screen keeps its encoding and gets ASCII markers instead ([PASS] [FAIL] [SKIP], see DemoHost).
                originalEncoding = Console.OutputEncoding;
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentException)
        {
            originalEncoding = null;
        }

        Console.CancelKeyPress += onCancel;
        try
        {
            return await DemoHost.RunAsync(args, overrides: null, cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (originalEncoding is not null)
            {
                try
                {
                    Console.OutputEncoding = originalEncoding;
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentException)
                {
                    // Leaving UTF-8 in place is harmless.
                }
            }
        }
    }
}
