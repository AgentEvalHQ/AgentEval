// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The device-code sign-in message through the whole demo, offline: whatever stage asks for a token — the readiness check
/// or the prompt and the benchmark — the message reaches the screen in full (the tester must read the page and the code)
/// and console.log with the code masked; no other file of the run folder, nor the zip, carries the code. One token source
/// serves the readiness check and the live agent, so a sign-in the check completed is not asked for again.
/// </summary>
public sealed class SignInPromptTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task DeviceCodePromptDuringReadiness_ScreenShowsTheCode_ConsoleLogTheZipAndEveryFileMaskIt()
    {
        _rig.WithHealthyEndpoint();
        PromptingTokenProvider? tokens = null;
        _rig.TokenProviderFactory = (_, prompt) => tokens = new PromptingTokenProvider(prompt);

        var run = await _rig.RunAsync("--readiness-only", "--zip", "--verbosity", "quiet");

        run.AssertExit(0);
        Assert.NotNull(tokens);
        Assert.Equal(1, tokens.Prompts);

        // The screen: the whole MSAL sentence, code included — even at the quietest verbosity.
        Assert.Contains(PromptingTokenProvider.MsalMessage, run.Out, StringComparison.Ordinal);

        // console.log keeps the sign-in message with the code masked…
        var log = run.Text("console.log");
        Assert.Contains("https://microsoft.com/devicelogin and enter the code [redacted] to authenticate.", log, StringComparison.Ordinal);

        // …and no file of the run folder, nor any entry of the zip, carries the code.
        Assert.True(run.Zip.Exists, "--zip wrote no zip.");
        var hits = LeakScanner.ScanFiles([PromptingTokenProvider.UserCode], run.Folder, run.Zip);
        Assert.True(hits.Count == 0, "The device code was found:\n" + string.Join("\n", hits));
        Assert.Empty(LeakScanner.Scan(run.Screen, run.Folder, run.Zip));
    }

    [Fact]
    public async Task DeviceCodePromptDuringThePrompt_GoesThroughTheDemosWriter_AndOneTokenSourceServesReadinessAndTheAgent()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var built = 0;
        PromptingTokenProvider? tokens = null;
        TextWriter? writer = null;
        _rig.TokenProviderFactory = (config, prompt) =>
        {
            Interlocked.Increment(ref built);
            Assert.Equal("11111111-1111-1111-1111-111111111111", config.TenantId);
            writer = prompt;
            // Every acquisition prompts, so the live agent's (after readiness) does too.
            return tokens = new PromptingTokenProvider(prompt) { PromptOn = _ => true };
        };

        var run = await _rig.RunAsync(DemoRig.Consent, "--no-benchmark");

        run.AssertExit(0);
        Assert.Equal(1, built);   // one token source for the readiness check and the live agent
        Assert.IsType<SignInPromptWriter>(writer);
        Assert.Equal(1, _rig.Handler.CountOf("SendMessage"));

        // The agent's acquisitions (after readiness) went through the same provider and wrote to the demo's writer: a sign-in
        // message is on the screen after the prompt step's heading.
        var lines = run.OutLines;
        var promptStep = Array.FindIndex(lines, l => l.StartsWith("Prompt (one turn", StringComparison.Ordinal));
        Assert.True(promptStep > 0, run.Describe());
        Assert.Contains(lines.Skip(promptStep), l => l.Contains(PromptingTokenProvider.MsalMessage, StringComparison.Ordinal));
        Assert.True(tokens!.Prompts >= 2, $"Expected prompts from readiness and from the agent, got {tokens.Prompts}.");

        // Every prompt is masked in console.log, and nothing else carries the code.
        var log = run.Text("console.log");
        Assert.Equal(tokens.Prompts, CountOf(log, "enter the code [redacted] to authenticate."));
        var hits = LeakScanner.ScanFiles([PromptingTokenProvider.UserCode], run.Folder);
        Assert.True(hits.Count == 0, "The device code was found:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void DefaultTokenSource_IsTheProvidersDeviceCodeSignIn_WritingToTheDemosPromptWriter()
    {
        var config = new Microsoft365Config { TenantId = "11111111-1111-1111-1111-111111111111", AppClientId = "22222222-2222-2222-2222-222222222222" };
        using var screen = new StringWriter();
        using var console = new DemoConsole(screen, TextWriter.Null, DemoVerbosity.Normal, colors: false);
        using var prompt = new SignInPromptWriter(console);

        var provider = Assert.IsType<WorkIqDeviceCodeTokenProvider>(DemoTokenProviders.DeviceCode(config, prompt));

        // Its prompt goes to the demo's writer (screen in full, console.log masked), never straight to Console.Error.
        Assert.Same(prompt, provider.DeviceCodePrompt);
        // No diagnostics of its own: the readiness check and the live agent attach theirs, so auth.* events reach the trace.
        Assert.True(provider.Diagnostics.IsNone);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
