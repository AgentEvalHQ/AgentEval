// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Readiness;
using Microsoft.Extensions.AI;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The host seams of <see cref="DemoHost.RunAsync"/>, for tests and embedding hosts. Every member is optional; null keeps
/// the real behaviour. <see cref="InnerHandler"/> and <see cref="TokenProvider"/> reach the provider only through its
/// public seams (<see cref="Microsoft365AgentFactory.BuildLive"/> and
/// <see cref="Microsoft365ReadinessCheck.RunAsync(Microsoft365Config, FileInfo, ReadinessCheckOptions, HttpMessageHandler, IWorkIqTokenProvider, CancellationToken)"/>);
/// the demo never substitutes a mock target of its own.
/// </summary>
internal sealed record DemoHostOverrides
{
    /// <summary>The innermost HTTP handler of every Work IQ connection (readiness and the live agent). The demo never disposes it.</summary>
    public HttpMessageHandler? InnerHandler { get; init; }

    /// <summary>The token source of every Work IQ connection; null builds one with <see cref="TokenProviderFactory"/>.</summary>
    public IWorkIqTokenProvider? TokenProvider { get; init; }

    /// <summary>
    /// Builds the run's one token source from the loaded config and the demo's sign-in prompt writer (the screen shows
    /// the device-code message in full, console.log keeps it with the code masked). Called at most once per run; the
    /// readiness check and the live agent share what it returns, so one sign-in serves both. Null builds the provider's
    /// MSAL device-code sign-in (<see cref="DemoTokenProviders.DeviceCode"/>). <see cref="TokenProvider"/> wins when set.
    /// </summary>
    public Func<Microsoft365Config, TextWriter, IWorkIqTokenProvider>? TokenProviderFactory { get; init; }

    /// <summary>
    /// Whether the screen gets ASCII markers (<c>[PASS] [FAIL] [SKIP]</c>, <c>-</c>, <c>-&gt;</c>) instead of the symbols;
    /// null decides from the console (redirected output, or an encoding that cannot encode the symbols). console.log is
    /// always UTF-8 with the symbols.
    /// </summary>
    public bool? AsciiScreen { get; init; }

    /// <summary>The screen's standard output; null is <see cref="Console.Out"/> (and only then may lines be coloured).</summary>
    public TextWriter? Out { get; init; }

    /// <summary>The screen's error output; null is <see cref="Console.Error"/>.</summary>
    public TextWriter? Err { get; init; }

    /// <summary>Where default run folders are created; null is <c>./m365-demo-runs</c>. <c>--out</c> wins over it.</summary>
    public DirectoryInfo? RunsRoot { get; init; }

    /// <summary>The clock that names the run folder and stamps the reports; null is <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset>? Now { get; init; }

    /// <summary>The judge's chat client; null resolves one from the environment (<c>AI_INFERENCE_PROVIDER</c>). <c>--no-judge</c> wins.</summary>
    public IChatClient? JudgeClient { get; init; }

    /// <summary>Reads an environment variable (<c>M365_CONFIG_PATH</c>, the consent variable, the judge's provider, <c>NO_COLOR</c>); null reads the process environment.</summary>
    public Func<string, string?>? GetEnvironmentVariable { get; init; }

    /// <summary>
    /// Writes the <c>--zip</c> archive of the run folder and returns it; null is <see cref="RunFolder.WriteZip"/>. A test
    /// makes it throw to see what a zip that fails after console.log was closed leaves in the run folder.
    /// </summary>
    public Func<RunFolder, FileInfo>? ZipWriter { get; init; }
}

/// <summary>The token sources the demo builds when the host supplies none.</summary>
internal static class DemoTokenProviders
{
    /// <summary>
    /// The provider's MSAL device-code sign-in for <paramref name="config"/>, writing its prompt to
    /// <paramref name="prompt"/> (the demo's <see cref="SignInPromptWriter"/>). Its auth events reach the trace of whichever
    /// run uses it (the readiness check, then the live agent attach their diagnostics), and a sign-in that starts inside a
    /// turn waits for the code's expiry, not the turn deadline. Building it makes no network call.
    /// </summary>
    /// <param name="config">The loaded config (tenant, app, scope).</param>
    /// <param name="prompt">Where the device-code message goes.</param>
    public static IWorkIqTokenProvider DeviceCode(Microsoft365Config config, TextWriter prompt)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(prompt);
        return new WorkIqDeviceCodeTokenProvider(config.TenantId, config.AppClientId, config.Scope, prompt);
    }
}
