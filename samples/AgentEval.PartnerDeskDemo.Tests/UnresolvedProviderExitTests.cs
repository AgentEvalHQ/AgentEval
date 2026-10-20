// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentEval.Providers;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>
/// What the demo and the evaluation do when the environment asks for a live model they cannot have. A set
/// <c>AI_INFERENCE_PROVIDER</c> is a request for a live run: when it does not resolve, both entry points stop with
/// exit 2 and the resolver's reason, rather than run the scripted model in its place and report every oracle OK.
/// </summary>
/// <remarks>
/// Each case starts the real binary as a child process — the decision lives in <c>Main</c>, so only the process
/// shows it — with every provider variable removed from the CHILD's environment and only the case's own set. No
/// case can resolve a live provider (each one lacks a key or names nothing real), so no request leaves the machine.
/// Standard input is closed, so a run that reaches the menu quits at once.
/// </remarks>
public sealed class UnresolvedProviderExitTests
{
    private const string Demo = "AgentEval.PartnerDeskDemo";
    private const string Evals = "AgentEval.PartnerDeskDemo.Evals";
    private const string FakeKey = "test-key-not-a-real-credential-7f3a";

    // The prefixes the live-run helper strips, plus Core's own list: a variable from the developer's shell must
    // never reach the child, or a case meant to be unresolvable could resolve on their machine.
    private static readonly string[] ProviderPrefixes =
        ["AZURE_", "AZUREOPENAI_", "OPENAI_", "FOUNDRY_", "MS_FOUNDRY_", "MAFARTCOMP_", "BITDEER_"];

    public static TheoryData<string[], string> UnresolvedSelections => new()
    {
        { ["AI_INFERENCE_PROVIDER=bitdeer"], "AI_INFERENCE_PROVIDER=bitdeer, but it is missing: BITDEER_API_KEY" },
        { ["AI_INFERENCE_PROVIDER=bitdear"], "AI_INFERENCE_PROVIDER='bitdear' is not one of" },
        {
            ["AI_INFERENCE_PROVIDER=azure", "AZURE_OPENAI_ENDPOINT=https://example-resource.openai.azure.com/"],
            "AI_INFERENCE_PROVIDER=azure, but it is missing: AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT"
        },
        {
            // A key, but an endpoint the resolver refuses to send it to: still not live.
            ["AI_INFERENCE_PROVIDER=bitdeer", $"BITDEER_API_KEY={FakeKey}", "BITDEER_ENDPOINT=http://example.invalid/v1"],
            "BITDEER_ENDPOINT uses plain http to a non-loopback host"
        },
    };

    [Theory]
    [MemberData(nameof(UnresolvedSelections))]
    public async Task Demo_AnExplicitSelectorThatDoesNotResolve_ExitsTwoWithTheReason_AndRunsNothingScripted(
        string[] environment,
        string reason)
    {
        var result = await RunAsync(Demo, ["--all"], environment);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(reason, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--offline", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        // Stopped before the banner: no scripted model, no phase, no oracle.
        Assert.DoesNotContain("SCRIPTED", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ORACLE", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnresolvedSelections))]
    public async Task Evals_AnExplicitSelectorThatDoesNotResolve_ExitsTwoWithTheReason_AndMeasuresNothing(
        string[] environment,
        string reason)
    {
        var result = await RunAsync(Evals, ["--runs", "1"], environment);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(reason, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--offline", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Evaluating", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_AnExplicitSelectorThatDoesNotResolve_StillRunsScripted_WhenOfflineIsAsked()
    {
        var result = await RunAsync(Demo, ["--offline"], ["AI_INFERENCE_PROVIDER=bitdeer"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Model: SCRIPTED offline provider (--offline).", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_NothingConfigured_StillRunsScripted_AndTheBannerSaysSo()
    {
        var result = await RunAsync(Demo, [], []);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Model: SCRIPTED offline provider.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("For the live path, set AI_INFERENCE_PROVIDER", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evals_NothingConfigured_ExitsTwo_UnlessOffline()
    {
        var result = await RunAsync(Evals, ["--runs", "1"], []);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("No live model provider is configured.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Evaluating", result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunAsync(string program, string[] arguments, string[] environment)
    {
        var (command, prefix) = Launch(program);
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in prefix.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        var inherited = start.Environment.Keys.ToList();
        foreach (var name in inherited)
        {
            if (string.Equals(name, InferenceProviderEnvironment.SelectorVariable, StringComparison.OrdinalIgnoreCase)
                || InferenceProviderEnvironment.AllProviderVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                || ProviderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                start.Environment.Remove(name);
            }
        }

        foreach (var assignment in environment)
        {
            var split = assignment.IndexOf('=', StringComparison.Ordinal);
            start.Environment[assignment[..split]] = assignment[(split + 1)..];
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {program}.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{program} {string.Join(' ', arguments)} did not exit within 120 s.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>The apphost the test project's output carries, else the shared-framework launcher.</summary>
    private static (string Command, string[] Prefix) Launch(string program)
    {
        var appHost = Path.Combine(
            AppContext.BaseDirectory,
            program + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty));
        if (File.Exists(appHost))
        {
            return (appHost, []);
        }

        var assembly = Path.Combine(AppContext.BaseDirectory, program + ".dll");
        Assert.True(File.Exists(assembly), $"'{assembly}' is not in the test output.");
        return ("dotnet", [assembly]);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
