// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentEval.Providers;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>
/// Actual process outcomes for unavailable configuration, failed phase oracles, runtime errors and report verdicts.
/// A live request never silently substitutes a passing scripted run; report text and exits retain their meaning.
/// </summary>
/// <remarks>
/// Each case starts the real binary as a child process — the decision lives in <c>Main</c>, so only the process
/// shows it — with every provider variable removed from the CHILD's environment and only the case's own set. No
/// Missing-configuration cases cannot resolve a provider; execution cases use only a local response fixture.
/// Standard input is closed except where a bounded menu sequence is explicitly supplied.
/// </remarks>
public sealed class ProcessOutcomeTests
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
    public async Task Demo_AnExplicitSelectorThatDoesNotResolve_ExitsElevenWithTheReason_AndRunsNothingScripted(
        string[] environment,
        string reason)
    {
        var result = await RunAsync(Demo, ["--all"], environment);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains(reason, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--offline", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        // Stopped before the banner: no scripted model, no phase, no oracle.
        Assert.DoesNotContain("SCRIPTED", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ORACLE", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnresolvedSelections))]
    public async Task Evals_AnExplicitSelectorThatDoesNotResolve_ExitsElevenWithTheReason_AndMeasuresNothing(
        string[] environment,
        string reason)
    {
        var result = await RunAsync(Evals, ["--runs", "1"], environment);

        Assert.Equal(11, result.ExitCode);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Demo_NoProviderResolves_ExitsElevenWithoutScriptedSubstitution(bool partialSetup)
    {
        var result = await RunAsync(Demo, [], partialSetup
            ? ["AZURE_OPENAI_ENDPOINT=https://example-resource.openai.azure.com/"] : []);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains("--offline", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("SCRIPTED", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evals_NothingConfigured_ExitsEleven_UnlessOffline()
    {
        var result = await RunAsync(Evals, ["--runs", "1"], []);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains("No live model provider is configured.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Evaluating", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public async Task Demo_PhaseOutcome_ReachesExplicitAndInteractiveExit(bool interactive, bool oracleFailure, int expected)
    {
        await using var server = new LoopbackChatServer(oracleFailure ? int.MaxValue : 0);
        var result = await RunAsync(Demo, interactive ? [] : ["--phase", "1"], server.Environment,
            interactive ? "1\nq\n" : null);

        Assert.Equal(expected, result.ExitCode);
        Assert.Contains(oracleFailure ? "PHASE 1 ORACLE" : "DID NOT COMPLETE", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(1, 10)]
    public async Task Evals_SomeRequestedRunsCannotComplete_KeepsKnownFailure(int successes, int expected)
    {
        await using var server = new LoopbackChatServer(successes);
        var result = await RunAsync(Evals, ["--runs", "2", "--arms", "1"], server.Environment);

        Assert.Equal(expected, result.ExitCode);
        Assert.Contains("SKIPPED after error", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(successes == 0 ? "RESULT: INCOMPLETE" : "RESULT: CHECK",
            successes == 0 ? result.StandardError : result.StandardOutput, StringComparison.Ordinal);
        if (successes > 0)
            Assert.DoesNotContain("RESULT: INCOMPLETE", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", 10, "RESULT: CHECK")]
    [InlineData("2", 11, "RESULT: n/a")]
    public async Task Evals_CompletedReport_UsesExistingTypedGateVerdict(string arm, int expected, string printed)
    {
        await using var server = new LoopbackChatServer(int.MaxValue);
        var result = await RunAsync(Evals, ["--runs", "1", "--arms", arm], server.Environment);

        Assert.Equal(expected, result.ExitCode);
        Assert.Contains(printed, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evals_CompleteOfflineCleanReport_ExitsZero()
    {
        var result = await RunAsync(Evals, ["--offline", "--runs", "1", "--arms", "1"], []);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("RESULT: PASS", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    public async Task Demo_UnexercisedPhase_ExitsEleven(string phase)
    {
        await using var server = new LoopbackChatServer(int.MaxValue);
        var result = await RunAsync(Demo, ["--phase", phase], server.Environment);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains("NOT MEASURED", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    public async Task Evals_UnexercisedGate_ReportsIncompleteAndExitsEleven(string arm)
    {
        await using var server = new LoopbackChatServer(int.MaxValue);
        var result = await RunAsync(Evals, ["--runs", "1", "--arms", arm], server.Environment);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains("RESULT: INCOMPLETE", result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunAsync(string program, string[] arguments, string[] environment, string? input = null)
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
        if (input is not null)
            await process.StandardInput.WriteAsync(input);
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
